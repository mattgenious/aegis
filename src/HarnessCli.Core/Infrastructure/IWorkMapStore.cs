using HarnessCli.Core;

namespace HarnessCli.Infrastructure;

public interface IWorkMapPathProvider
{
    string DirectoryPath { get; }
}

public interface IWorkMapStore
{
    Task SaveMissionAsync(WorkMapMissionRecord mission, CancellationToken cancellationToken = default);

    Task<WorkMapMissionRecord?> GetMissionAsync(string missionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkMapMissionRecord>> GetMissionsAsync(CancellationToken cancellationToken = default);

    Task<WorkMapMissionRecord> UpdateMissionAsync(
        string missionId,
        Func<WorkMapMissionRecord, WorkMapMissionRecord> update,
        CancellationToken cancellationToken = default);

    Task SaveWorkstreamAsync(WorkMapWorkstreamRecord workstream, CancellationToken cancellationToken = default);

    Task<WorkMapWorkstreamRecord?> GetWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkMapWorkstreamRecord>> GetWorkstreamsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkMapWorkstreamRecord>> GetWorkstreamsAsync(string missionId, CancellationToken cancellationToken = default);

    Task<WorkMapWorkstreamRecord> UpdateWorkstreamAsync(
        string workstreamId,
        Func<WorkMapWorkstreamRecord, WorkMapWorkstreamRecord> update,
        CancellationToken cancellationToken = default);

    Task DeleteWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default);

    Task SaveAgentSessionAsync(WorkMapAgentSessionRecord session, CancellationToken cancellationToken = default);

    Task<WorkMapAgentSessionRecord?> GetAgentSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkMapAgentSessionRecord>> GetAgentSessionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkMapAgentSessionRecord>> GetAgentSessionsAsync(string missionId, CancellationToken cancellationToken = default);

    Task<WorkMapAgentSessionRecord> UpdateAgentSessionAsync(
        string sessionId,
        Func<WorkMapAgentSessionRecord, WorkMapAgentSessionRecord> update,
        CancellationToken cancellationToken = default);
}
