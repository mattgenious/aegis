using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aegis.Core;

namespace Aegis.Backends;

public sealed class PiBackend : ISessionBackend
{
    private const string PiBinary = "pi";
    private const string MessageLineSuffix = ".messages.jsonl";
    private const string StatusSuffix = ".status.json";

    private readonly string _piBinary;
    private readonly string? _stateRoot;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public PiBackend(string? piBinary = null, string? stateRoot = null)
    {
        _piBinary = string.IsNullOrWhiteSpace(piBinary) ? PiBinary : piBinary;
        _stateRoot = stateRoot;
    }

    public BackendKind Kind => BackendKind.Pi;

    public async Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var backendSessionId = $"pi-{Guid.NewGuid():N}";
        var statePath = ResolveSessionPath(backendSessionId, request.Directory);
        var statusPath = statePath + StatusSuffix;
        var messagesPath = statePath + MessageLineSuffix;

        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        await SaveStatusAsync(statusPath, "idle", cancellationToken);
        await File.WriteAllTextAsync(messagesPath, "[]", cancellationToken);

        return new SessionRecord(
            SessionId: backendSessionId,
            Backend: BackendKind.Pi,
            BackendSessionId: backendSessionId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Directory: request.Directory,
            BackendMetadataPath: statePath,
            Metadata: null);
    }

    public async Task<CommandResult> PostPromptAsync(SessionRecord session, PromptRequest request, CancellationToken cancellationToken = default)
    {
        var statusPath = ResolveStatusPath(session);
        var messagesPath = ResolveMessagesPath(session);
        await SaveStatusAsync(statusPath, "running", cancellationToken);

        var prompt = request.Raw ? request.Text : BuildDelegationPrompt(request.Text, request.SummaryMarker);
        var args = new List<string>
        {
            "--mode",
            "json",
            "--print",
            prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.AddRange(["--model", request.Model]);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _piBinary,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(session.Directory))
        {
            startInfo.WorkingDirectory = session.Directory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pi.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitCodeTask = process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask, exitCodeTask);
            var exitCode = process.ExitCode;

            var stdout = await stdoutTask;
            var messages = ParsePiMessages(stdout);
            var parsingWarnings = messages.Count == 0 && !string.IsNullOrWhiteSpace(stdout)
                ? "pi emitted data, but no recognizable message events"
                : null;
            await PersistMessagesAsync(messagesPath, messages, cancellationToken);

            if (exitCode != 0)
            {
                var errorText = await stderrTask;
                await SaveStatusAsync(statusPath, $"error:{exitCode}", cancellationToken);
                return CommandResult.Failure(
                    exitCode,
                    "pi command failed",
                    ComposeFailureGuidance(errorText, parsingWarnings));
            }

            await SaveStatusAsync(statusPath, "idle", cancellationToken);

            if (parsingWarnings is not null)
            {
                return CommandResult.Failure(2, parsingWarnings, ComposeFailureGuidance(await stderrTask, parsingWarnings));
            }

            return CommandResult.Success();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            await SaveStatusAsync(statusPath, "error:pi-not-found", cancellationToken);
            return CommandResult.Failure(127, "pi executable not found on PATH", ex.Message);
        }
        catch (InvalidOperationException)
        {
            await SaveStatusAsync(statusPath, "error:pi-execution-failed", cancellationToken);
            return CommandResult.Failure(1, "Failed to execute pi in the current environment. Verify the executable and mode flags.");
        }
        catch (OperationCanceledException)
        {
            await SaveStatusAsync(statusPath, "error:aborted", cancellationToken);
            return CommandResult.Failure(124, "pi execution was cancelled");
        }
    }

    private static string BuildDelegationPrompt(string prompt, string marker)
    {
        return PromptTemplates.Render("delegation/pi.md", new Dictionary<string, string>
        {
            ["task"] = prompt,
            ["summary_marker"] = marker
        });
    }

    public async Task<SessionStateSnapshot> GetSessionStateAsync(SessionRecord session, int anchorMessageIndex = -1, CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(session, 0, cancellationToken);
        var status = await ReadStatusAsync(ResolveStatusPath(session), cancellationToken);
        var messageCount = messages.Count;
        var latestUserMessageId = LatestMessageId(messages, "user");
        var latestAssistantMessageId = LatestMessageId(messages, "assistant");
        var hasAssistantAfterAnchor = HasAssistantAfter(messages, anchorMessageIndex);
        var hasFreshSummary = FindLastSummaryText(messages, marker: "FINAL HANDOFF", anchorMessageIndex: anchorMessageIndex) is not null;

        return SessionStateNormalizer.Normalize(
            session.SessionId,
            session.BackendSessionId,
            status,
            messageCount,
            latestUserMessageId,
            latestAssistantMessageId,
            hasAssistantAfterAnchor,
            hasFreshSummary);
    }

    public async Task<IReadOnlyList<BackendMessage>> GetMessagesAsync(
        SessionRecord session,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var messagesPath = ResolveMessagesPath(session);
        if (!File.Exists(messagesPath))
        {
            return [];
        }

        var raw = await File.ReadAllTextAsync(messagesPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var parsed = JsonSerializer.Deserialize<List<PiStoredMessage>>(raw, _jsonOptions) ?? [];
        if (limit > 0 && parsed.Count > limit)
        {
            parsed = parsed.TakeLast(limit).ToList();
        }

        return parsed.Select(item => new BackendMessage(item.Id, item.Role, item.Text, item.PartId, item.Timestamp)).ToList();
    }

    public async IAsyncEnumerable<BackendMessage> WatchMessagesAsync(
        SessionRecord session,
        int limit = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = await GetMessagesAsync(session, limit, cancellationToken);
        foreach (var item in current)
        {
            if (seen.Add(item.Id))
            {
                yield return item;
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);
            var refreshed = await GetMessagesAsync(session, limit, cancellationToken);
            foreach (var item in refreshed)
            {
                if (seen.Add(item.Id))
                {
                    yield return item;
                }
            }
        }
    }

    public async Task<SummaryResult?> ExtractSummaryAsync(
        SessionRecord session,
        string marker,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(session, 0, cancellationToken);
        var found = FindLastSummaryText(messages, marker, anchorMessageIndex);
        return found is null
            ? null
            : new SummaryResult(session.SessionId, found.Value.MessageId, found.Value.PartId, found.Value.Text);
    }

    public async Task<CommandResult> AbortAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var statusPath = ResolveStatusPath(session);
        await SaveStatusAsync(statusPath, "error:abort-not-supported", cancellationToken);
        return CommandResult.Failure(1, "Pi currently does not expose a cancellable async session handle in this CLI mode.");
    }

    public async Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var statusPath = ResolveStatusPath(session);
        var messagesPath = ResolveMessagesPath(session);
        var removedMessages = false;
        var removedStatus = false;

        if (File.Exists(messagesPath))
        {
            File.Delete(messagesPath);
            removedMessages = true;
        }

        if (File.Exists(statusPath))
        {
            File.Delete(statusPath);
            removedStatus = true;
        }

        if (removedMessages || removedStatus)
        {
            return CommandResult.Success();
        }

        return CommandResult.Failure(1, "Session did not exist.");
    }

    private string ResolveSessionPath(string backendSessionId, string? baseDirectory)
    {
        return BackendStatePaths.ResolveSessionPath("pi", backendSessionId, baseDirectory, _stateRoot);
    }

    private string ResolveStatusPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + StatusSuffix;
    }

    private string ResolveMessagesPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + MessageLineSuffix;
    }

    private async Task PersistMessagesAsync(string messagesPath, List<PiStoredMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(messagesPath)!);
        List<PiStoredMessage> existing = [];
        if (File.Exists(messagesPath))
        {
            var existingText = await File.ReadAllTextAsync(messagesPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(existingText))
            {
                existing = JsonSerializer.Deserialize<List<PiStoredMessage>>(existingText, _jsonOptions) ?? [];
            }
        }

        existing.AddRange(messages);
        await File.WriteAllTextAsync(messagesPath, JsonSerializer.Serialize(existing, _jsonOptions), cancellationToken);
    }

    private static async Task SaveStatusAsync(string statusPath, string status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        var payload = new PiStatus(status, DateTimeOffset.UtcNow);
        var serialized = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(statusPath, serialized, cancellationToken);
    }

    private static async Task<string> ReadStatusAsync(string statusPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statusPath)) return "idle";
        var raw = await File.ReadAllTextAsync(statusPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return "idle";
        var parsed = JsonSerializer.Deserialize<PiStatus>(raw);
        return parsed?.ApiStatus ?? "idle";
    }

    private static List<PiStoredMessage> ParsePiMessages(string stdout)
    {
        var messages = new List<PiStoredMessage>();
        if (string.IsNullOrWhiteSpace(stdout)) return messages;

        var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line is "[DONE]" or "done") continue;
            if (line[0] != '{' && line[0] != '[')
            {
                continue;
            }

            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is null) continue;

            if (TryGetPiMessageEvent(node, index, out var eventMessage))
            {
                messages.Add(eventMessage);
                index++;
                continue;
            }

            if (TryGetStatusFromEvent(node, out var status))
            {
                messages.Add(new PiStoredMessage(
                    $"pi_evt_status_{index:D6}",
                    "system",
                    $"status:{status}",
                    $"pi_status_{index:D6}",
                    DateTimeOffset.UtcNow));
                index++;
                continue;
            }

            var role = (StringValue(node["role"]) ?? StringValue(node["sender"]) ?? "assistant").ToLowerInvariant();
            var text = ExtractText(node);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            messages.Add(new PiStoredMessage(
                StringValue(node["id"]) ?? StringValue(node["message_id"]) ?? $"pi_msg_{index:D6}",
                role,
                text,
                StringValue(node["part_id"]) ?? StringValue(node["part"]?["id"]) ?? $"pi_part_{index:D6}",
                ParseTimestamp(node)));
            index++;
        }

        return messages;
    }

    private static bool TryGetStatusFromEvent(JsonNode node, out string status)
    {
        status = string.Empty;
        var candidate = node["status"]?.GetValue<string>()
                        ?? node["state"]?.GetValue<string>()
                        ?? node["phase"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            status = candidate;
            return true;
        }

        var kind = node["type"]?.GetValue<string>();
        if (string.Equals(kind, "status", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "state", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "event", StringComparison.OrdinalIgnoreCase))
        {
            status = node["payload"]?["status"]?.GetValue<string>()
                     ?? node["data"]?["status"]?.GetValue<string>()
                     ?? string.Empty;

            return !string.IsNullOrWhiteSpace(status);
        }

        return false;
    }

    private static bool TryGetPiMessageEvent(JsonNode node, int index, out PiStoredMessage message)
    {
        message = new PiStoredMessage(string.Empty, string.Empty, string.Empty, string.Empty, null);
        var eventType = StringValue(node["type"]);
        if (!string.Equals(eventType, "message_end", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(eventType, "turn_end", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var messageNode = node["message"]?.AsObject();
        if (messageNode is null)
        {
            return false;
        }

        var role = StringValue(messageNode["role"]);
        if (string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        var text = ExtractText(messageNode);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        message = new PiStoredMessage(
            StringValue(messageNode["responseId"]) ?? $"pi_msg_{index:D6}",
            role.ToLowerInvariant(),
            text,
            $"pi_part_{index:D6}",
            ParseTimestamp(messageNode));
        return true;
    }

    private static string ExtractText(JsonNode node)
    {
        var direct = StringValue(node["text"])
            ?? StringValue(node["content"])
            ?? StringValue(node["message"])
            ?? StringValue(node["output"]);

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        var contentText = ExtractContentArrayText(node["content"]);
        if (!string.IsNullOrWhiteSpace(contentText))
        {
            return contentText.Trim();
        }

        var nestedFromMessage = node["message"]?.AsObject();
        if (nestedFromMessage is not null)
        {
            var nestedText = StringValue(nestedFromMessage["text"])
                             ?? StringValue(nestedFromMessage["content"])
                             ?? ExtractContentArrayText(nestedFromMessage["content"]);
            if (!string.IsNullOrWhiteSpace(nestedText))
            {
                return nestedText.Trim();
            }
        }

        var payload = node["payload"]?.AsObject();
        if (payload is not null)
        {
            var payloadText = StringValue(payload["text"])
                              ?? StringValue(payload["content"])
                              ?? ExtractContentArrayText(payload["content"])
                              ?? StringValue(payload["output"])
                              ?? StringValue(payload["message"]);
            if (!string.IsNullOrWhiteSpace(payloadText))
            {
                return payloadText.Trim();
            }

            var details = payload["message"]?.AsObject();
            if (details is not null)
            {
                var detailsText = StringValue(details["text"])
                                  ?? StringValue(details["content"])
                                  ?? ExtractContentArrayText(details["content"]);
                if (!string.IsNullOrWhiteSpace(detailsText))
                {
                    return detailsText.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static string? ExtractContentArrayText(JsonNode? node)
    {
        if (node is not JsonArray content)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in content)
        {
            var itemText = StringValue(item?["text"]);
            if (!string.IsNullOrWhiteSpace(itemText))
            {
                parts.Add(itemText);
            }
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static string? StringValue(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode node)
    {
        var timestampText = StringValue(node["timestamp"])
                            ?? StringValue(node["time"])
                            ?? StringValue(node["created_at"])
                            ?? StringValue(node["createdAt"])
                            ?? StringValue(node["ts"]);

        return DateTimeOffset.TryParse(timestampText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }

    private static string? LatestMessageId(IReadOnlyList<BackendMessage> messages, string role)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (string.Equals(messages[index].Role, role, StringComparison.OrdinalIgnoreCase))
            {
                return messages[index].Id;
            }
        }

        return null;
    }

    private static bool HasAssistantAfter(IReadOnlyList<BackendMessage> messages, int anchorMessageIndex)
    {
        if (anchorMessageIndex < 0) anchorMessageIndex = -1;
        for (var index = anchorMessageIndex + 1; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string MessageId, string PartId, string Text)? FindLastSummaryText(
        IReadOnlyList<BackendMessage> messages,
        string marker,
        int anchorMessageIndex = -1)
    {
        for (var messageIndex = messages.Count - 1; messageIndex > anchorMessageIndex; messageIndex--)
        {
            var message = messages[messageIndex];
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var markerIndex = message.Text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var summary = message.Text[(markerIndex + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            return (message.Id, message.PartId ?? string.Empty, summary);
        }

        return null;
    }

    private static string ComposeFailureGuidance(string? stderr, string? parserHint)
    {
        if (!string.IsNullOrWhiteSpace(parserHint) && !string.IsNullOrWhiteSpace(stderr))
        {
            return $"{parserHint}. pi stderr: {stderr}";
        }

        if (!string.IsNullOrWhiteSpace(stderr)) return stderr!;
        if (!string.IsNullOrWhiteSpace(parserHint)) return parserHint;
        return "pi returned no actionable output in --mode json.";
    }

    private sealed record PiStatus(string ApiStatus, DateTimeOffset UpdatedAt);

    private sealed record PiStoredMessage(string Id, string Role, string Text, string PartId, DateTimeOffset? Timestamp);
}
