namespace HarnessCli.Backends;

internal sealed record CopilotStatus(string ApiStatus, DateTimeOffset UpdatedAt);

internal sealed record CopilotStoredMessage(string Id, string Role, string Text, string PartId, DateTimeOffset? Timestamp);
