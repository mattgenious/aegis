using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using HarnessCli.Core;

namespace HarnessCli.Backends;

public sealed class OpencodeBackend : ISessionBackend
{
    private readonly OpenCodeClient _client;

    public OpencodeBackend(OpenCodeClient client)
    {
        _client = client;
    }

    public BackendKind Kind => BackendKind.Opencode;

    public async Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(request.Title)) body["title"] = request.Title;
        if (!string.IsNullOrWhiteSpace(request.ParentSessionId)) body["parentID"] = request.ParentSessionId;

        var created = await _client.PostJson(WithDirectory("session", request.Directory), body, cancellationToken);
        var backendSessionId = created?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Create session response did not include id.");

        return new SessionRecord(
            SessionId: $"opencode-{Guid.NewGuid():N}",
            Backend: BackendKind.Opencode,
            BackendSessionId: backendSessionId,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Directory: request.Directory,
            Metadata: null);
    }

    public async Task<CommandResult> PostPromptAsync(SessionRecord session, PromptRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildPromptBody(request);
        var target = request.NoReply
            ? $"session/{session.BackendSessionId}/message"
            : $"session/{session.BackendSessionId}/prompt_async";

        await _client.PostNoContent(WithDirectory(target, session.Directory), body, cancellationToken);
        return CommandResult.Success();
    }

    public async Task<SessionStateSnapshot> GetSessionStateAsync(
        SessionRecord session,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(session, 0, cancellationToken);
        var apiStatus = await GetSessionApiStatusAsync(session, cancellationToken);
        var messageCount = messages.Count;
        var latestUserMessageId = LatestMessageId(messages, "user");
        var latestAssistantMessageId = LatestMessageId(messages, "assistant");
        var hasAssistantAfterAnchor = HasAssistantAfter(messages, anchorMessageIndex);
        var hasFreshSummary = FindLastSummaryText(messages, marker: "FINAL HANDOFF", anchorMessageIndex: anchorMessageIndex) is not null;

        return SessionStateNormalizer.Normalize(
            session.SessionId,
            session.BackendSessionId,
            apiStatus,
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
        var path = $"session/{session.BackendSessionId}/message";
        if (limit > 0) path += $"?limit={limit}";
        var response = await _client.GetJson(WithDirectory(path, session.Directory), cancellationToken);
        if (response is not JsonArray array) return [];

        var messages = new List<BackendMessage>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject message) continue;
            messages.Add(ToBackendMessage(message));
        }

        return messages;
    }

    public async IAsyncEnumerable<BackendMessage> WatchMessagesAsync(
        SessionRecord session,
        int limit = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!cancellationToken.IsCancellationRequested)
        {
            var messages = await GetMessagesAsync(session, limit > 0 ? limit : 20, cancellationToken);
            foreach (var message in messages)
            {
                if (!seen.Add(message.Id)) continue;
                yield return message;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    public async Task<SummaryResult?> ExtractSummaryAsync(
        SessionRecord session,
        string marker,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(session, 0, cancellationToken);
        var summary = FindLastSummaryText(messages, marker, anchorMessageIndex);
        return summary is null
            ? null
            : new SummaryResult(session.SessionId, summary.Value.MessageId, summary.Value.PartId, summary.Value.Text);
    }

    public async Task<CommandResult> AbortAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var result = await _client.PostEmpty(WithDirectory($"session/{session.BackendSessionId}/abort", session.Directory), cancellationToken);
        return result is null
            ? CommandResult.Failure(1, "abort endpoint returned an empty response")
            : CommandResult.Success("Abort acknowledged");
    }

    public async Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        var result = await _client.PostEmpty(WithDirectory($"session/{session.BackendSessionId}/abort", session.Directory), cancellationToken);
        return result is null
            ? CommandResult.Failure(1, "teardown endpoint returned an empty response")
            : CommandResult.Success("Teardown acknowledged");
    }

    private static JsonObject BuildPromptBody(PromptRequest request)
    {
        var fullPrompt = request.Raw ? request.Text : BuildHarnessPrompt(request.Text, request.SummaryMarker);
        var body = new JsonObject
        {
            ["parts"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = fullPrompt
            })
        };

        AddPromptMetadata(body, request);
        return body;
    }

    private static string BuildHarnessPrompt(string prompt, string marker)
    {
        return $"""
You are running as a delegated OpenCode pseudo-subagent.

Task:
{prompt}

Operating contract:
- Do the task autonomously within the available tools and context.
- Prefer concise, factual work over broad exploration.
- If you cannot complete something, say exactly what blocked it.
- Your final assistant message must contain a complete handoff summary for the orchestrator.
- Put the final handoff under this exact marker on its own line: {marker}
- After the marker, include only the relevant findings, files changed/read, commands run, errors, and recommended next action.
""";
    }

    private static void AddPromptMetadata(JsonObject body, PromptRequest request)
    {
        if (request.Agent is not null) body["agent"] = request.Agent;
        if (request.ModelProvider is not null) body["provider"] = request.ModelProvider;
        if (request.Model is not null) body["model"] = request.Model;
        if (request.Variant is not null) body["variant"] = request.Variant;
        if (request.Directory is not null) body["directory"] = request.Directory;
        if (request.Options is { Count: >0 })
        {
            var options = new JsonObject();
            foreach (var pair in request.Options)
            {
                options[pair.Key] = pair.Value;
            }

            body["options"] = options;
        }
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
            if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;

            var markerIndex = message.Text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;

            var summary = message.Text[(markerIndex + marker.Length)..].Trim();
            if (string.IsNullOrWhiteSpace(summary)) continue;
            return (message.Id, message.PartId ?? string.Empty, summary);
        }

        return null;
    }

    private static BackendMessage ToBackendMessage(JsonObject item)
    {
        var info = item["info"]?.AsObject();
        var id = info?["id"]?.GetValue<string>() ?? string.Empty;
        var role = info?["role"]?.GetValue<string>() ?? "unknown";

        var text = new StringBuilder();
        var partId = string.Empty;
        var parts = item["parts"]?.AsArray();
        if (parts is not null)
        {
            foreach (var rawPart in parts)
            {
                var part = rawPart?.AsObject();
                if (part is null || part["type"]?.GetValue<string>() != "text") continue;
                var partText = part["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(partText)) continue;
                if (text.Length > 0) text.AppendLine();
                text.Append(partText);
                if (string.IsNullOrEmpty(partId)) partId = part["id"]?.GetValue<string>() ?? string.Empty;
            }
        }

        var timestampText = info?["timestamp"]?.GetValue<string>();
        DateTimeOffset? timestamp = null;
        if (!string.IsNullOrWhiteSpace(timestampText) && DateTimeOffset.TryParse(timestampText, out var parsed))
        {
            timestamp = parsed;
        }

        return new BackendMessage(
            id,
            role,
            text.ToString(),
            partId,
            timestamp);
    }

    private static string WithDirectory(string path, string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? path
            : path.Contains('?')
                ? $"{path}&directory={Uri.EscapeDataString(Path.GetFullPath(directory))}"
                : $"{path}?directory={Uri.EscapeDataString(Path.GetFullPath(directory))}";

    private async Task<string?> GetSessionApiStatusAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        var map = await _client.GetJson("session/status", cancellationToken);
        var node = map?[session.BackendSessionId];
        if (node is null) return null;
        var type = node["type"]?.GetValue<string>();
        if (type == "retry") return $"retry:{node["message"]?.GetValue<string>()}";
        return type;
    }
}
