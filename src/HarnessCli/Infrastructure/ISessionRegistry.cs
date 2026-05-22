using HarnessCli.Core;

namespace HarnessCli.Infrastructure;

public interface ISessionRegistry
{
    Task AddOrUpdateAsync(SessionRecord session, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface ISessionRegistryPathProvider
{
    string DirectoryPath { get; }
}

