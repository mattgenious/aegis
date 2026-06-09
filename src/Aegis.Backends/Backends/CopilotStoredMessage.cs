namespace Aegis.Backends;

internal sealed record CopilotStatus(
    string ApiStatus,
    DateTimeOffset UpdatedAt,
    string? CopilotSessionId = null,
    int? ExitCode = null);

internal sealed record CopilotStoredMessage(string Id, string Role, string Text, string PartId, DateTimeOffset? Timestamp);

internal sealed record CopilotParseResult(
    List<CopilotStoredMessage> Messages,
    string? SessionId = null,
    int? ExitCode = null);
