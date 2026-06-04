using System.Collections.Immutable;
using Aegis.Core;

namespace Aegis.Infrastructure;

public sealed class UnknownSessionException(string sessionId)
    : InvalidOperationException($"Unknown or expired session id: '{sessionId}'. Use a valid session returned by aegis.")
{
    public string SessionId { get; } = sessionId;
}

public sealed class SessionRegistryService
{
    private readonly ISessionRegistry _registry;

    public SessionRegistryService(ISessionRegistry registry)
    {
        _registry = registry;
    }

    public string GenerateSessionId(BackendKind backend) => $"{backend.ToOptionValue()}-{Guid.NewGuid():N}";

    public SessionRecord CreateRecord(
        BackendKind backend,
        string backendSessionId,
        DateTimeOffset? createdAtUtc = null,
        string? directory = null,
        string? backendMetadataPath = null,
        ImmutableDictionary<string, string>? metadata = null)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = createdAtUtc ?? now;

        return new SessionRecord(
            SessionId: GenerateSessionId(backend),
            Backend: backend,
            BackendSessionId: backendSessionId,
            CreatedAtUtc: createdAt,
            Directory: directory,
            BackendMetadataPath: backendMetadataPath,
            Metadata: metadata)
        {
            LastUpdatedUtc = now
        };
    }

    public async Task<SessionRecord> CreateAndStoreAsync(
        BackendKind backend,
        string backendSessionId,
        DateTimeOffset? createdAtUtc = null,
        string? directory = null,
        string? backendMetadataPath = null,
        ImmutableDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var session = CreateRecord(backend, backendSessionId, createdAtUtc, directory, backendMetadataPath, metadata);
        await _registry.AddOrUpdateAsync(session, cancellationToken);
        return session;
    }

    public async Task<bool> HasAsync(string sessionId, CancellationToken cancellationToken = default) =>
        await _registry.ExistsAsync(sessionId, cancellationToken);

    public Task<SessionRecord?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _registry.GetAsync(sessionId, cancellationToken);

    public async Task<SessionRecord> RequireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _registry.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            throw new UnknownSessionException(sessionId);
        }

        return session;
    }

    public async Task TouchAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await RequireAsync(sessionId, cancellationToken);
        await _registry.AddOrUpdateAsync(session with { LastUpdatedUtc = DateTimeOffset.UtcNow }, cancellationToken);
    }

    public Task<IReadOnlyList<SessionRecord>> GetForBackendAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default) =>
        _registry.GetByBackendAsync(backend, cancellationToken);

    public Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default) =>
        _registry.RemoveExpiredAsync(maxAge, cancellationToken);
}
