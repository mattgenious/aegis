using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aegis.Core;

namespace Aegis.Backends;

public sealed class CopilotBackend : ISessionBackend
{
    private const string CopilotBinary = "copilot";
    private const string MessageLineSuffix = ".messages.jsonl";
    private const string StatusSuffix = ".status.json";
    private const string ShareSuffix = ".share.md";
    private const string CopilotSessionMetadataKey = "copilot.sessionId";

    private readonly string _copilotBinary;
    private readonly string? _stateRoot;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CopilotBackend(string? copilotBinary = null, string? stateRoot = null)
    {
        _copilotBinary = ResolveCopilotBinary(copilotBinary);
        _stateRoot = stateRoot;
    }

    public BackendKind Kind => BackendKind.Copilot;

    public async Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var copilotSessionId = Guid.NewGuid().ToString("D");
        var backendSessionId = $"copilot-{copilotSessionId}";
        var statePath = ResolveSessionPath(backendSessionId, request.Directory);
        var statusPath = statePath + StatusSuffix;
        var messagesPath = statePath + MessageLineSuffix;

        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        await SaveStatusAsync(statusPath, "idle", cancellationToken, copilotSessionId);
        await File.WriteAllTextAsync(messagesPath, "[]", cancellationToken);

        return new SessionRecord(
            SessionId: backendSessionId,
            Backend: BackendKind.Copilot,
            BackendSessionId: backendSessionId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Directory: request.Directory,
            BackendMetadataPath: statePath,
            Metadata: ImmutableDictionary<string, string>.Empty.Add(CopilotSessionMetadataKey, copilotSessionId));
    }

    public async Task<CommandResult> PostPromptAsync(SessionRecord session, PromptRequest request, CancellationToken cancellationToken = default)
    {
        var statusPath = ResolveStatusPath(session);
        var messagesPath = ResolveMessagesPath(session);
        var copilotSessionId = ResolveCopilotSessionId(session);
        await SaveStatusAsync(statusPath, "running", cancellationToken, copilotSessionId);

        if (request.Options.TryGetValue("harness.async", out var asyncValue)
            && bool.TryParse(asyncValue, out var isAsync)
            && isAsync)
        {
            await SaveStatusAsync(statusPath, "error:async-not-supported", cancellationToken, copilotSessionId);
            return CommandResult.Failure(
                1,
                "Copilot backend does not support --async for non-interactive prompts yet.",
                "GitHub Copilot CLI remote control currently requires a long-running interactive local CLI session and is not available in --prompt mode. Run without --async for a blocking prompt, or use OpenCode/subagents until Aegis has an interactive Copilot process supervisor.");
        }

        if (request.NoReply)
        {
            await PersistMessagesAsync(messagesPath,
            [
                new CopilotStoredMessage(
                    NewMessageId("user"),
                    "user",
                    request.Text,
                    NewPartId("user"),
                    DateTimeOffset.UtcNow)
            ], cancellationToken);
            await SaveStatusAsync(statusPath, "idle", cancellationToken, copilotSessionId);
            return CommandResult.Success("Prompt recorded without calling Copilot because --no-reply was set.");
        }

        var prompt = request.Raw ? request.Text : BuildDelegationPrompt(request.Text, request.SummaryMarker);
        var startInfo = CopilotProcess.CreateStartInfo(_copilotBinary, BuildArguments(session, request, prompt));

        if (!string.IsNullOrWhiteSpace(session.Directory))
        {
            startInfo.WorkingDirectory = session.Directory;
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start copilot.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var exitCodeTask = process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask, exitCodeTask);
            var exitCode = process.ExitCode;

            var stdout = await stdoutTask;
            var parseResult = CopilotMessageParser.ParseTranscript(stdout);
            var messages = parseResult.Messages;
            if (messages.Count == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                messages.Add(new CopilotStoredMessage(NewMessageId("assistant"), "assistant", stdout.Trim(), NewPartId("assistant"), DateTimeOffset.UtcNow));
            }

            if (!messages.Any(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)))
            {
                messages.Insert(0, new CopilotStoredMessage(NewMessageId("user"), "user", request.Text, NewPartId("user"), DateTimeOffset.UtcNow));
            }

            await PersistMessagesAsync(messagesPath, messages, cancellationToken);
            var emittedSessionId = parseResult.SessionId ?? copilotSessionId;
            var effectiveExitCode = parseResult.ExitCode ?? exitCode;

            if (effectiveExitCode != 0)
            {
                var errorText = await stderrTask;
                await SaveStatusAsync(statusPath, $"error:{effectiveExitCode}", cancellationToken, emittedSessionId, effectiveExitCode);
                return CommandResult.Failure(effectiveExitCode, "copilot command failed", ComposeFailureGuidance(errorText, effectiveExitCode));
            }

            await SaveStatusAsync(statusPath, "idle", cancellationToken, emittedSessionId, effectiveExitCode);
            return CommandResult.Success();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            await SaveStatusAsync(statusPath, "error:copilot-not-found", cancellationToken, copilotSessionId);
            return CommandResult.Failure(127, "copilot executable not found on PATH", "Install GitHub Copilot CLI, authenticate with `copilot login`, or set COPILOT_GITHUB_TOKEN/GH_TOKEN/GITHUB_TOKEN for headless runs.");
        }
        catch (OperationCanceledException)
        {
            await SaveStatusAsync(statusPath, "error:aborted", CancellationToken.None, copilotSessionId);
            return CommandResult.Failure(124, "copilot execution was cancelled");
        }
    }

    public async Task<SessionStateSnapshot> GetSessionStateAsync(
        SessionRecord session,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(session, 0, cancellationToken);
        var status = await ReadStatusAsync(ResolveStatusPath(session), cancellationToken);
        var latestUserMessageId = LatestMessageId(messages, "user");
        var latestAssistantMessageId = LatestMessageId(messages, "assistant");
        var hasAssistantAfterAnchor = HasAssistantAfter(messages, anchorMessageIndex);
        var hasFreshSummary = FindLastSummaryText(messages, marker: "FINAL HANDOFF", anchorMessageIndex: anchorMessageIndex) is not null;

        return SessionStateNormalizer.Normalize(
            session.SessionId,
            session.BackendSessionId,
            status,
            messages.Count,
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
        if (!File.Exists(messagesPath)) return [];

        var raw = await File.ReadAllTextAsync(messagesPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var parsed = JsonSerializer.Deserialize<List<CopilotStoredMessage>>(raw, _jsonOptions) ?? [];
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
            if (seen.Add(item.Id)) yield return item;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);
            var refreshed = await GetMessagesAsync(session, limit, cancellationToken);
            foreach (var item in refreshed)
            {
                if (seen.Add(item.Id)) yield return item;
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
        await SaveStatusAsync(ResolveStatusPath(session), "error:abort-not-supported", cancellationToken, ResolveCopilotSessionId(session));
        return CommandResult.Failure(1, "Copilot backend runs one non-interactive process per prompt and does not currently expose a cancellable async session handle.");
    }

    public async Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var statusPath = ResolveStatusPath(session);
        var messagesPath = ResolveMessagesPath(session);
        var removed = false;

        if (File.Exists(messagesPath))
        {
            File.Delete(messagesPath);
            removed = true;
        }

        if (File.Exists(statusPath))
        {
            File.Delete(statusPath);
            removed = true;
        }

        return removed ? CommandResult.Success() : CommandResult.Failure(1, "Session did not exist.");
    }

    private IReadOnlyList<string> BuildArguments(SessionRecord session, PromptRequest request, string prompt)
    {
        var args = new List<string>
        {
            "--prompt",
            prompt,
            "--output-format",
            "json",
            "--no-remote",
            "--stream=off",
            "--no-ask-user",
            "--share",
            (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + ShareSuffix
        };

        var copilotSessionId = ResolveCopilotSessionId(session);
        if (!string.IsNullOrWhiteSpace(copilotSessionId))
        {
            args.AddRange(["--session-id", copilotSessionId]);
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.AddRange(["--model", request.Model]);
        }

        if (!string.IsNullOrWhiteSpace(request.Agent))
        {
            args.AddRange(["--agent", request.Agent]);
        }

        if (!string.IsNullOrWhiteSpace(request.Variant))
        {
            args.AddRange(["--reasoning-effort", request.Variant]);
        }

        foreach (var allow in ReadRepeatedOption(request, "copilot.allowTool"))
        {
            args.AddRange(["--allow-tool", allow]);
        }

        foreach (var allow in ReadRepeatedOption(request, "copilot.allowUrl"))
        {
            args.AddRange(["--allow-url", allow]);
        }

        if (request.Options.TryGetValue("copilot.allowAll", out var allowAll) && bool.TryParse(allowAll, out var parsed) && parsed)
        {
            args.Add("--allow-all");
        }

        return args;
    }

    private string ResolveSessionPath(string backendSessionId, string? baseDirectory)
    {
        return BackendStatePaths.ResolveSessionPath("copilot", backendSessionId, baseDirectory, _stateRoot);
    }

    private string ResolveStatusPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + StatusSuffix;
    }

    private string ResolveMessagesPath(SessionRecord session)
    {
        return (session.BackendMetadataPath ?? ResolveSessionPath(session.BackendSessionId, session.Directory)) + MessageLineSuffix;
    }

    private async Task PersistMessagesAsync(string messagesPath, List<CopilotStoredMessage> messages, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(messagesPath)!);
        List<CopilotStoredMessage> existing = [];
        if (File.Exists(messagesPath))
        {
            var existingText = await File.ReadAllTextAsync(messagesPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(existingText))
            {
                existing = JsonSerializer.Deserialize<List<CopilotStoredMessage>>(existingText, _jsonOptions) ?? [];
            }
        }

        existing.AddRange(messages);
        await File.WriteAllTextAsync(messagesPath, JsonSerializer.Serialize(existing, _jsonOptions), cancellationToken);
    }

    private static async Task SaveStatusAsync(
        string statusPath,
        string status,
        CancellationToken cancellationToken,
        string? copilotSessionId = null,
        int? exitCode = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(new CopilotStatus(status, DateTimeOffset.UtcNow, copilotSessionId, exitCode)), cancellationToken);
    }

    private static async Task<string> ReadStatusAsync(string statusPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statusPath)) return "idle";
        var raw = await File.ReadAllTextAsync(statusPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return "idle";
        var parsed = JsonSerializer.Deserialize<CopilotStatus>(raw);
        return parsed?.ApiStatus ?? "idle";
    }

    private static string BuildDelegationPrompt(string prompt, string marker)
    {
        return PromptTemplates.Render("delegation/copilot.md", new Dictionary<string, string>
        {
            ["task"] = prompt,
            ["summary_marker"] = marker
        });
    }

    private static IEnumerable<string> ReadRepeatedOption(PromptRequest request, string key)
    {
        return request.Options.TryGetValue(key, out var value)
            ? value.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    private static string ResolveCopilotBinary(string? copilotBinary)
    {
        if (!string.IsNullOrWhiteSpace(copilotBinary)) return copilotBinary;
        var configured = Environment.GetEnvironmentVariable("AEGIS_COPILOT_BINARY")
                         ?? Environment.GetEnvironmentVariable("HARNESS_CLI_COPILOT_BINARY");
        return string.IsNullOrWhiteSpace(configured) ? CopilotBinary : configured;
    }

    private static string? LatestMessageId(IReadOnlyList<BackendMessage> messages, string role)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (string.Equals(messages[index].Role, role, StringComparison.OrdinalIgnoreCase)) return messages[index].Id;
        }

        return null;
    }

    private static bool HasAssistantAfter(IReadOnlyList<BackendMessage> messages, int anchorMessageIndex)
    {
        if (anchorMessageIndex < 0) anchorMessageIndex = -1;
        for (var index = anchorMessageIndex + 1; index < messages.Count; index++)
        {
            if (string.Equals(messages[index].Role, "assistant", StringComparison.OrdinalIgnoreCase)) return true;
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
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;

            var markerIndex = message.Text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;

            var summary = message.Text[(markerIndex + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(summary)) continue;

            return (message.Id, message.PartId ?? string.Empty, summary);
        }

        return null;
    }

    private static string ComposeFailureGuidance(string? stderr, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(stderr)) return stderr;
        return exitCode == 9009
            ? "copilot returned exit code 9009 with no stderr. On Windows this usually means the command launch path was not found; verify `copilot` is on PATH or set AEGIS_COPILOT_BINARY to the full copilot.cmd/copilot.exe path."
            : "copilot returned no actionable error output. Verify authentication with `copilot login` or COPILOT_GITHUB_TOKEN/GH_TOKEN/GITHUB_TOKEN.";
    }

    private static string? ResolveCopilotSessionId(SessionRecord session)
    {
        if (session.Metadata.TryGetValue(CopilotSessionMetadataKey, out var metadataSessionId)
            && TryNormalizeGuid(metadataSessionId, out var parsedMetadataSessionId))
        {
            return parsedMetadataSessionId;
        }

        var backendSessionId = session.BackendSessionId;
        if (backendSessionId.StartsWith("copilot-", StringComparison.OrdinalIgnoreCase))
        {
            backendSessionId = backendSessionId["copilot-".Length..];
        }

        return TryNormalizeGuid(backendSessionId, out var parsedBackendSessionId) ? parsedBackendSessionId : null;
    }

    private static bool TryNormalizeGuid(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Guid.TryParse(value, out var parsed)) return false;
        normalized = parsed.ToString("D");
        return true;
    }

    private static string NewMessageId(string role) => $"copilot_{role}_{Guid.NewGuid():N}";

    private static string NewPartId(string role) => $"copilot_{role}_part_{Guid.NewGuid():N}";
}
