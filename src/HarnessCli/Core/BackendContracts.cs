using System.Collections.Immutable;

namespace HarnessCli.Core;

public enum PromptSourceKind
{
    Inline,
    File,
    Stdin
}

public sealed record CreateSessionRequest(
    string? Title,
    string? ParentSessionId = null,
    string? Directory = null);

public sealed record PromptRequest(
    string Text,
    PromptSourceKind SourceKind,
    string? SourceLocation,
    string? ModelProvider = null,
    string? Model = null,
    string? Variant = null,
    string SummaryMarker = "FINAL HANDOFF",
    string? Directory = null,
    string? Agent = null,
    string? System = null,
    bool NoReply = false,
    bool Raw = false,
    ImmutableDictionary<string, string>? Options = null)
{
    public ImmutableDictionary<string, string> Options { get; init; } = Options ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record BackendMessage(
    string Id,
    string Role,
    string Text,
    string? PartId = null,
    DateTimeOffset? Timestamp = null);

public sealed record SessionRecord(
    string SessionId,
    BackendKind Backend,
    string BackendSessionId,
    DateTimeOffset CreatedAtUtc,
    string? Directory = null,
    string? BackendMetadataPath = null,
    ImmutableDictionary<string, string>? Metadata = null)
{
    public ImmutableDictionary<string, string> Metadata { get; init; } = Metadata ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record SessionStateSnapshot(
    string SessionId,
    string BackendSessionId,
    string? ApiStatus,
    string EffectiveStatus,
    string DerivedStatus,
    int MessageCount = 0,
    string? LatestUserMessageId = null,
    string? LatestAssistantMessageId = null,
    bool HasFreshSummary = false);

public sealed record CommandResult(
    bool IsSuccess,
    int ExitCode = 0,
    string? Message = null,
    string? Error = null)
{
    public static CommandResult Success(string? message = null) => new(true, 0, message, null);

    public static CommandResult Failure(int exitCode, string? message = null, string? error = null) =>
        new(false, exitCode, message, error);
}

public sealed record SummaryResult(string SessionId, string MessageId, string PartId, string Text);
