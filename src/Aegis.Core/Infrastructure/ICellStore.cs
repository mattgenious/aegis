using Aegis.Core;

namespace Aegis.Infrastructure;

public interface ICellPathProvider
{
    string DirectoryPath { get; }
}

public interface ICellStore
{
    Task SaveMissionAsync(CellMissionRecord mission, CancellationToken cancellationToken = default);

    Task<CellMissionRecord?> GetMissionAsync(string missionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CellMissionRecord>> GetMissionsAsync(CancellationToken cancellationToken = default);

    Task<CellMissionRecord> UpdateMissionAsync(
        string missionId,
        Func<CellMissionRecord, CellMissionRecord> update,
        CancellationToken cancellationToken = default);

    Task SaveWorkstreamAsync(CellWorkstreamRecord workstream, CancellationToken cancellationToken = default);

    Task<CellWorkstreamRecord?> GetWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CellWorkstreamRecord>> GetWorkstreamsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CellWorkstreamRecord>> GetWorkstreamsAsync(string missionId, CancellationToken cancellationToken = default);

    Task<CellWorkstreamRecord> UpdateWorkstreamAsync(
        string workstreamId,
        Func<CellWorkstreamRecord, CellWorkstreamRecord> update,
        CancellationToken cancellationToken = default);

    Task DeleteWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default);

    Task SaveAgentSessionAsync(CellAgentSessionRecord session, CancellationToken cancellationToken = default);

    Task<CellAgentSessionRecord?> GetAgentSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CellAgentSessionRecord>> GetAgentSessionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CellAgentSessionRecord>> GetAgentSessionsAsync(string missionId, CancellationToken cancellationToken = default);

    Task<CellAgentSessionRecord> UpdateAgentSessionAsync(
        string sessionId,
        Func<CellAgentSessionRecord, CellAgentSessionRecord> update,
        CancellationToken cancellationToken = default);
}
