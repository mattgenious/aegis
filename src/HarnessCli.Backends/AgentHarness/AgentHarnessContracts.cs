using HarnessCli.Core;

namespace HarnessCli.Backends;

public sealed record CreateAgentSessionRequest(
    string? Title = null,
    string? ParentSessionId = null,
    string? Directory = null);

public sealed record AgentRunRequest
{
    public required string Prompt { get; init; }

    public string? SessionId { get; init; }

    public string? Title { get; init; }

    public string? ParentSessionId { get; init; }

    public string? Directory { get; init; }

    public string? ModelProvider { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public string SummaryMarker { get; init; } = "FINAL HANDOFF";

    public string? Agent { get; init; }

    public string? System { get; init; }

    public bool NoReply { get; init; }

    public bool Raw { get; init; }

    public bool Async { get; init; }

    public bool Wait { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    public PromptSourceKind SourceKind { get; init; } = PromptSourceKind.Inline;

    public string? SourceLocation { get; init; }
}

public sealed record AgentSession(
    string SessionId,
    BackendKind Backend,
    string BackendSessionId,
    DateTimeOffset CreatedAtUtc,
    string? Directory)
{
    public static AgentSession FromSessionRecord(SessionRecord session) =>
        new(session.SessionId, session.Backend, session.BackendSessionId, session.CreatedAtUtc, session.Directory);
}

public sealed record AgentRunResult(
    AgentSession Session,
    bool IsSuccess,
    int ExitCode,
    string? Message,
    string? Error,
    AgentSummary? Summary);

public sealed record AgentSessionState(
    string SessionId,
    string BackendSessionId,
    string? ApiStatus,
    string EffectiveStatus,
    string DerivedStatus,
    int MessageCount,
    string? LatestUserMessageId,
    string? LatestAssistantMessageId,
    bool HasFreshSummary)
{
    public static AgentSessionState FromSnapshot(SessionStateSnapshot state) =>
        new(
            state.SessionId,
            state.BackendSessionId,
            state.ApiStatus,
            state.EffectiveStatus,
            state.DerivedStatus,
            state.MessageCount,
            state.LatestUserMessageId,
            state.LatestAssistantMessageId,
            state.HasFreshSummary);
}

public sealed record AgentMessage(
    string Id,
    string Role,
    string Text,
    string? PartId,
    DateTimeOffset? Timestamp)
{
    public static AgentMessage FromBackendMessage(BackendMessage message) =>
        new(message.Id, message.Role, message.Text, message.PartId, message.Timestamp);
}

public sealed record AgentSummary(
    string SessionId,
    string MessageId,
    string PartId,
    string Text)
{
    public static AgentSummary FromSummaryResult(SummaryResult summary) =>
        new(summary.SessionId, summary.MessageId, summary.PartId, summary.Text);
}

public sealed record AgentAbortResult(
    AgentSession Session,
    bool IsSuccess,
    int ExitCode,
    string? Message,
    string? Error);
