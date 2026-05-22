using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using HarnessCli.Core;

namespace HarnessCli.Backends;

public sealed class PiBackend : ISessionBackend
{
    private const string PiBinary = "pi";
    private const string MessageLineSuffix = ".messages.jsonl";
    private const string StatusSuffix = ".status.json";

    private readonly string _piBinary;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public PiBackend(string? piBinary = null)
    {
        _piBinary = string.IsNullOrWhiteSpace(piBinary) ? PiBinary : piBinary;
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

        var prompt = request.Raw ? request.Text : BuildHarnessPrompt(request.Text, request.SummaryMarker);
        var args = new List<string>
        {
            "--mode",
            "json",
            "--prompt",
            prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.AddRange(["--model", request.Model]);
        }

        if (!string.IsNullOrWhiteSpace(session.Directory))
        {
            args.AddRange(["--cwd", session.Directory]);
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

    private static string BuildHarnessPrompt(string prompt, string marker)
    {
        return $"""
You are running as a delegated Pi pseudo-subagent.

Task:
{prompt}

Operating contract:
- Do the task autonomously within the available tools and context.
- Prefer concise, factual work over broad exploration.
- If you cannot complete something, say exactly what blocked it.
- Your final handoff must contain a complete handoff summary for the orchestrator.
- Put the final handoff under this exact marker on its own line: {marker}
- After the marker, include only the relevant findings, files changed/read, commands run, errors, and recommended next action.
""";
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

    private static string ResolveSessionPath(string backendSessionId, string? baseDirectory)
    {
        var root = Path.Combine(
            string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.GetTempPath()
                : Path.GetFullPath(baseDirectory),
            ".harness-cli",
            "pi");
        Directory.CreateDirectory(root);
        return Path.Combine(root, backendSessionId);
    }

    private static string ResolveStatusPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + StatusSuffix;
    }

    private static string ResolveMessagesPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + MessageLineSuffix;
    }

    private async Task PersistMessagesAsync(string messagesPath, List<PiStoredMessage> messages, CancellationToken cancellationToken)
    {
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

            var role = (node["role"]?.GetValue<string>() ?? node["sender"]?.GetValue<string>() ?? "assistant").ToLowerInvariant();
            var text = ExtractText(node);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            messages.Add(new PiStoredMessage(
                node["id"]?.GetValue<string>() ?? node["message_id"]?.GetValue<string>() ?? $"pi_msg_{index:D6}",
                role,
                text,
                node["part_id"]?.GetValue<string>() ?? node["part"]?["id"]?.GetValue<string>() ?? $"pi_part_{index:D6}",
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

    private static string ExtractText(JsonNode node)
    {
        var direct = node["text"]?.GetValue<string>()
            ?? node["content"]?.GetValue<string>()
            ?? node["message"]?.GetValue<string>()
            ?? node["output"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        var nestedFromMessage = node["message"]?.AsObject();
        if (nestedFromMessage is not null)
        {
            var nestedText = nestedFromMessage["text"]?.GetValue<string>()
                             ?? nestedFromMessage["content"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(nestedText))
            {
                return nestedText.Trim();
            }
        }

        var payload = node["payload"]?.AsObject();
        if (payload is not null)
        {
            var payloadText = payload["text"]?.GetValue<string>()
                              ?? payload["content"]?.GetValue<string>()
                              ?? payload["output"]?.GetValue<string>()
                              ?? payload["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(payloadText))
            {
                return payloadText.Trim();
            }

            var details = payload["message"]?.AsObject();
            if (details is not null)
            {
                var detailsText = details["text"]?.GetValue<string>() ?? details["content"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(detailsText))
                {
                    return detailsText.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode node)
    {
        var timestampText = node["timestamp"]?.GetValue<string>()
                            ?? node["time"]?.GetValue<string>()
                            ?? node["created_at"]?.GetValue<string>()
                            ?? node["createdAt"]?.GetValue<string>()
                            ?? node["ts"]?.GetValue<string>();

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
