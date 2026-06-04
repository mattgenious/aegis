using Aegis.Core;

namespace Aegis.Infrastructure;

public interface ISessionRegistry
{
    Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default);

    Task AddOrUpdateAsync(SessionRecord session, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionRecord>> GetByBackendAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<int> RemoveExpiredAsync(
        TimeSpan maxAge,
        CancellationToken cancellationToken = default);
}

public interface ISessionRegistryPathProvider
{
    string DirectoryPath { get; }
}
