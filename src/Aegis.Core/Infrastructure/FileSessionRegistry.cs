using System.Text.Json;
using System.Linq;
using Aegis.Core;

namespace Aegis.Infrastructure;

public sealed class DefaultSessionRegistryPathProvider : ISessionRegistryPathProvider
{
    public string DirectoryPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AEGIS_SESSION_DIR")
                           ?? Environment.GetEnvironmentVariable("HARNESS_CLI_SESSION_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var aegisPath = Path.Combine(appData, "aegis", "sessions");
            var legacyPath = Path.Combine(appData, "aegis", "sessions");
            return !Directory.Exists(aegisPath) && Directory.Exists(legacyPath) ? legacyPath : aegisPath;
        }

        var tempAegisPath = Path.Combine(Path.GetTempPath(), "aegis", "sessions");
        var tempLegacyPath = Path.Combine(Path.GetTempPath(), "aegis", "sessions");
        return !Directory.Exists(tempAegisPath) && Directory.Exists(tempLegacyPath) ? tempLegacyPath : tempAegisPath;
    }
}

public sealed class FileSessionRegistry : ISessionRegistry
{
    private readonly ISessionRegistryPathProvider _pathProvider;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public FileSessionRegistry(ISessionRegistryPathProvider? pathProvider = null)
    {
        _pathProvider = pathProvider ?? new DefaultSessionRegistryPathProvider();
    }

    public async Task AddOrUpdateAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_pathProvider.DirectoryPath);
        var path = SessionFilePath(session.SessionId);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, session, _options, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(SessionFilePath(sessionId)));

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = SessionFilePath(sessionId);
        if (!File.Exists(path)) return false;

        await Task.Run(() => File.Delete(path), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var path = SessionFilePath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var session = await JsonSerializer.DeserializeAsync<SessionRecord>(stream, _options, cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<SessionRecord>> GetByBackendAsync(
        BackendKind backend,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        return all.Where(session => session.Backend == backend).ToArray();
    }

    public async Task<IReadOnlyList<SessionRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_pathProvider.DirectoryPath))
        {
            return Array.Empty<SessionRecord>();
        }

        var files = Directory.EnumerateFiles(_pathProvider.DirectoryPath, "*.json");
        var sessions = new List<SessionRecord>();

        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var session = await JsonSerializer.DeserializeAsync<SessionRecord>(stream, _options, cancellationToken);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions;
    }

    public async Task<int> RemoveExpiredAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var sessions = await GetAllAsync(cancellationToken);
        var removed = 0;

        foreach (var session in sessions)
        {
            if (session.LastUpdatedUtc >= cutoff) continue;
            var removedThis = await DeleteAsync(session.SessionId, cancellationToken);
            if (removedThis) removed++;
        }

        return removed;
    }

    private string SessionFilePath(string sessionId)
    {
        var safeFileName = string.Concat(sessionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = safeFileName + ".json";
        return Path.Combine(_pathProvider.DirectoryPath, fileName);
    }
}
