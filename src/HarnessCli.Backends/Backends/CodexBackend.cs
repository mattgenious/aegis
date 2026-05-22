using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessCli.Core;

namespace HarnessCli.Backends;

public sealed class CodexBackend : ISessionBackend
{
    private const string CodexBinary = "codex";
    private const string MessageLineSuffix = ".messages.jsonl";
    private const string StatusSuffix = ".status.json";

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public BackendKind Kind => BackendKind.Codex;

    public async Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var backendSessionId = $"codex-{Guid.NewGuid():N}";
        var statePath = ResolveSessionPath(backendSessionId, request.Directory);
        var statusPath = statePath + StatusSuffix;
        var messagesPath = statePath + MessageLineSuffix;

        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        await SaveStatusAsync(statusPath, "idle", cancellationToken);
        await File.WriteAllTextAsync(messagesPath, "[]", cancellationToken);

        return new SessionRecord(
            SessionId: backendSessionId,
            Backend: BackendKind.Codex,
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
            "exec",
            "--json",
            "--dangerously-bypass-approvals-and-sandbox",
            "--skip-git-repo-check",
            prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Insert(3, "-m");
            args.Insert(4, request.Model);
        }

        if (!string.IsNullOrWhiteSpace(session.Directory))
        {
            args.Add("--cd");
            args.Add(session.Directory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = CodexBinary,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start codex.");
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitCodeTask = process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask, exitCodeTask);

            var exitCode = process.ExitCode;
            var stdout = await stdoutTask;
            var messages = ParseCodexMessages(stdout);
            await PersistMessagesAsync(messagesPath, messages, cancellationToken);

            if (exitCode == 0)
            {
                await SaveStatusAsync(statusPath, "idle", cancellationToken);
                return CommandResult.Success();
            }

            var errorText = await stderrTask;
            await SaveStatusAsync(statusPath, $"error:{exitCode}", cancellationToken);
            return CommandResult.Failure(exitCode, "codex exec failed", errorText);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            await SaveStatusAsync(statusPath, "error:codex-not-found", cancellationToken);
            return CommandResult.Failure(127, "codex executable not found on PATH");
        }
    }

    public async Task<SessionStateSnapshot> GetSessionStateAsync(
        SessionRecord session,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default)
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

        var parsed = JsonSerializer.Deserialize<List<CodexStoredMessage>>(raw, _jsonOptions) ?? [];
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
        return CommandResult.Failure(1, "Codex does not currently expose a cancellable async session handle in this non-interactive mode.");
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
        var root = Path.Combine(string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.GetTempPath()
            : Path.GetFullPath(baseDirectory), ".harness-cli", "codex");
        Directory.CreateDirectory(root);
        return Path.Combine(root, backendSessionId);
    }

    private static string ResolveStatusPath(SessionRecord session)
    {
        return (session.BackendMetadataPath
                ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + StatusSuffix;
    }

    private static string ResolveMessagesPath(SessionRecord session)
    {
        return (session.BackendMetadataPath
                ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + MessageLineSuffix;
    }

    private async Task PersistMessagesAsync(string messagesPath, List<CodexStoredMessage> messages, CancellationToken cancellationToken)
    {
        List<CodexStoredMessage> existing = [];
        if (File.Exists(messagesPath))
        {
            var existingText = await File.ReadAllTextAsync(messagesPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(existingText))
            {
                existing = JsonSerializer.Deserialize<List<CodexStoredMessage>>(existingText, _jsonOptions) ?? [];
            }
        }

        existing.AddRange(messages);
        var serialized = JsonSerializer.Serialize(existing, _jsonOptions);
        await File.WriteAllTextAsync(messagesPath, serialized, cancellationToken);
    }

    private static async Task SaveStatusAsync(string statusPath, string status, CancellationToken cancellationToken)
    {
        var payload = new CodexStatus(status, DateTimeOffset.UtcNow);
        var serialized = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(statusPath, serialized, cancellationToken);
    }

    private static async Task<string> ReadStatusAsync(string statusPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statusPath)) return "idle";
        var raw = await File.ReadAllTextAsync(statusPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return "idle";
        var parsed = JsonSerializer.Deserialize<CodexStatus>(raw);
        return parsed?.ApiStatus ?? "idle";
    }

    private static string BuildHarnessPrompt(string prompt, string marker)
    {
        return PromptTemplates.Render("delegation/codex.md", new Dictionary<string, string>
        {
            ["task"] = prompt,
            ["summary_marker"] = marker
        });
    }

    private static List<CodexStoredMessage> ParseCodexMessages(string stdout)
    {
        var messages = new List<CodexStoredMessage>();
        if (string.IsNullOrWhiteSpace(stdout)) return messages;

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = 0;
        foreach (var line in lines)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is null) continue;

            if (TryGetCurrentEventMessage(node, index, out var currentEventMessage))
            {
                messages.Add(currentEventMessage);
                index++;
                continue;
            }

            var msg = node["msg"]?.AsObject();
            var type = msg?["type"]?.GetValue<string>();
            if (type != "text" && type != "error")
            {
                continue;
            }

            if (msg is null)
            {
                continue;
            }

            var text = msg["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var role = type == "error" ? "assistant" : "assistant";
            var messageText = text.Trim();
            var timestamp = DateTimeOffset.TryParse(msg["timestamp"]?.GetValue<string>() ?? node["timestamp"]?.GetValue<string>(), out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;
            messages.Add(new CodexStoredMessage(
                $"msg_{index:D6}",
                role,
                messageText,
                $"part_{index:D6}",
                timestamp));
            index++;
        }

        return messages;
    }

    private static bool TryGetCurrentEventMessage(JsonNode node, int index, out CodexStoredMessage message)
    {
        message = new CodexStoredMessage(string.Empty, string.Empty, string.Empty, string.Empty, null);
        var eventType = node["type"]?.GetValue<string>();
        if (!string.Equals(eventType, "item.completed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var item = node["item"]?.AsObject();
        if (item is null)
        {
            return false;
        }

        var itemType = item["type"]?.GetValue<string>();
        var text = item["text"]?.GetValue<string>();
        if (!string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var timestamp = DateTimeOffset.TryParse(node["timestamp"]?.GetValue<string>(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        message = new CodexStoredMessage(
            item["id"]?.GetValue<string>() ?? $"msg_{index:D6}",
            "assistant",
            text.Trim(),
            $"part_{index:D6}",
            timestamp);
        return true;
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
            if (markerIndex < 0) continue;

            var summary = message.Text[(markerIndex + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(summary)) continue;

            return (message.Id, message.PartId ?? string.Empty, summary);
        }

        return null;
    }

    private sealed record CodexStatus(string ApiStatus, DateTimeOffset UpdatedAt);

    private sealed record CodexStoredMessage(string Id, string Role, string Text, string PartId, DateTimeOffset? Timestamp);
}
