using System.Text.Json;
using HarnessCli.Core;

namespace HarnessCli.Infrastructure;

public sealed class DefaultSessionRegistryPathProvider : ISessionRegistryPathProvider
{
    public string DirectoryPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("HARNESS_CLI_SESSION_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "harness-cli", "sessions");
        }

        return Path.Combine(Path.GetTempPath(), "harness-cli", "sessions");
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

    private string SessionFilePath(string sessionId)
    {
        var safeFileName = string.Concat(sessionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = safeFileName + ".json";
        return Path.Combine(_pathProvider.DirectoryPath, fileName);
    }
}

