namespace Aegis.Core;

public sealed record CellMissionRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "cell";

    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? Intent { get; init; }

    public string Status { get; init; } = "planned";

    public string? ParentCellId { get; init; }

    public List<string> ChildCellIds { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<string> WorkstreamIds { get; init; } = [];

    public List<string> SessionIds { get; init; } = [];

    public List<CellEvidenceRecord> Evidence { get; init; } = [];

    public List<CellEdgeRecord> Edges { get; init; } = [];

    public List<CellEventRecord> Events { get; init; } = [];

    public string? NextAction { get; init; }
}

public sealed record CellWorkstreamRecord
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

    public List<CellEvidenceRecord> Evidence { get; init; } = [];

    public string? IntegrationAction { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CellAgentSessionRecord
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

    public string? Agent { get; init; }

    public string? Directory { get; init; }

    public int? TimeoutSeconds { get; init; }

    public string Status { get; init; } = "planned";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<CellEventRecord> Events { get; init; } = [];

    public List<CellEvidenceRecord> Evidence { get; init; } = [];

    public List<CellMessageRecord> Messages { get; init; } = [];

    public List<CellStatusObservationRecord> StatusObservations { get; init; } = [];

    public CellHandoffRecord? FinalHandoff { get; init; }

    public CellBlockerRecord? Blocker { get; init; }

    public List<CellVerificationRecord> Verification { get; init; } = [];
}

public sealed record CellEdgeRecord
{
    public string FromId { get; init; } = string.Empty;

    public string ToId { get; init; } = string.Empty;

    public string Kind { get; init; } = "dependsOn";

    public string? Summary { get; init; }
}

public sealed record CellEventRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Type { get; init; } = string.Empty;

    public string? Summary { get; init; }
}

public sealed record CellEvidenceRecord
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = "note";

    public string? Path { get; init; }

    public string? Summary { get; init; }

    public DateTimeOffset AddedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CellMessageRecord
{
    public string Id { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string? Text { get; init; }

    public string? PartId { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public int Sequence { get; init; }

    public bool IsExcerpt { get; init; } = true;
}

public sealed record CellStatusObservationRecord
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

public sealed record CellHandoffRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Text { get; init; } = string.Empty;
}

public sealed record CellBlockerRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Summary { get; init; } = string.Empty;

    public string? Evidence { get; init; }
}

public sealed record CellVerificationRecord
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Kind { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public string? Summary { get; init; }
}

public sealed record CellStoreSnapshot
{
    public int SchemaVersion { get; init; } = 1;

    public string Kind { get; init; } = "cellStoreSnapshot";

    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public List<CellMissionRecord> Missions { get; init; } = [];

    public List<CellWorkstreamRecord> Workstreams { get; init; } = [];

    public List<CellAgentSessionRecord> Sessions { get; init; } = [];
}
