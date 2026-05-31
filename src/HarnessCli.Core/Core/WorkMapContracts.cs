namespace HarnessCli.Core;

public sealed record WorkMapMissionRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "mission";

    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Intent { get; init; }

    public string Status { get; init; } = "planned";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<string> WorkstreamIds { get; init; } = [];

    public List<string> SessionIds { get; init; } = [];

    public List<WorkMapEvidenceRecord> Evidence { get; init; } = [];

    public List<WorkMapEdgeRecord> Edges { get; init; } = [];

    public List<WorkMapEventRecord> Events { get; init; } = [];

    public string? NextAction { get; init; }
}

public sealed record WorkMapWorkstreamRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "workstream";

    public string Id { get; init; } = string.Empty;

    public string MissionId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Role { get; init; }

    public string? Target { get; init; }

    public string? ClonePath { get; init; }

    public string? SourceRepoPath { get; init; }

    public string? Branch { get; init; }

    public string Status { get; init; } = "planned";

    public List<string> DependsOn { get; init; } = [];

    public List<string> SessionIds { get; init; } = [];

    public List<WorkMapEvidenceRecord> Evidence { get; init; } = [];

    public string? IntegrationAction { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record WorkMapAgentSessionRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "agentSession";

    public string Id { get; init; } = string.Empty;

    public string MissionId { get; init; } = string.Empty;

    public string WorkstreamId { get; init; } = string.Empty;

    public string? DisplayName { get; init; }

    public string? Title { get; init; }

    public string? Role { get; init; }

    public string Backend { get; init; } = string.Empty;

    public string? BackendSessionId { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public string? Directory { get; init; }

    public string Status { get; init; } = "planned";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<WorkMapEventRecord> Events { get; init; } = [];

    public List<WorkMapEvidenceRecord> Evidence { get; init; } = [];

    public List<WorkMapMessageRecord> Messages { get; init; } = [];

    public List<WorkMapStatusObservationRecord> StatusObservations { get; init; } = [];

    public WorkMapHandoffRecord? FinalHandoff { get; init; }

    public WorkMapBlockerRecord? Blocker { get; init; }

    public List<WorkMapVerificationRecord> Verification { get; init; } = [];
}

public sealed record WorkMapEdgeRecord
{
    public string FromId { get; init; } = string.Empty;

    public string ToId { get; init; } = string.Empty;

    public string Kind { get; init; } = "dependsOn";

    public string? Summary { get; init; }
}

public sealed record WorkMapEventRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Type { get; init; } = string.Empty;

    public string? Summary { get; init; }
}

public sealed record WorkMapEvidenceRecord
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = "note";

    public string? Path { get; init; }

    public string? Summary { get; init; }

    public DateTimeOffset AddedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record WorkMapMessageRecord
{
    public string Id { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string? Text { get; init; }

    public string? PartId { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public int Sequence { get; init; }

    public bool IsExcerpt { get; init; } = true;
}

public sealed record WorkMapStatusObservationRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? ApiStatus { get; init; }

    public string EffectiveStatus { get; init; } = string.Empty;

    public string DerivedStatus { get; init; } = string.Empty;

    public int MessageCount { get; init; }

    public string? LatestUserMessageId { get; init; }

    public string? LatestAssistantMessageId { get; init; }

    public bool HasFreshSummary { get; init; }
}

public sealed record WorkMapHandoffRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Text { get; init; } = string.Empty;
}

public sealed record WorkMapBlockerRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Summary { get; init; } = string.Empty;

    public string? Evidence { get; init; }
}

public sealed record WorkMapVerificationRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Kind { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public string? Summary { get; init; }
}

public sealed record WorkMapStoreSnapshot
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "workMapSnapshot";

    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<WorkMapMissionRecord> Missions { get; init; } = [];

    public List<WorkMapWorkstreamRecord> Workstreams { get; init; } = [];

    public List<WorkMapAgentSessionRecord> Sessions { get; init; } = [];
}
