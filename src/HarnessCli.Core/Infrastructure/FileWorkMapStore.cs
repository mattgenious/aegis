using System.Text.Json;
using HarnessCli.Core;

namespace HarnessCli.Infrastructure;

public sealed class DefaultWorkMapPathProvider : IWorkMapPathProvider
{
    public string DirectoryPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AEGIS_CELL_DIR")
                           ?? Environment.GetEnvironmentVariable("HARNESS_CLI_WORK_MAP_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var aegisPath = Path.Combine(appData, "aegis", "cells");
            var legacyPath = Path.Combine(appData, "harness-cli", "work-map");
            return !Directory.Exists(aegisPath) && Directory.Exists(legacyPath) ? legacyPath : aegisPath;
        }

        var tempAegisPath = Path.Combine(Path.GetTempPath(), "aegis", "cells");
        var tempLegacyPath = Path.Combine(Path.GetTempPath(), "harness-cli", "work-map");
        return !Directory.Exists(tempAegisPath) && Directory.Exists(tempLegacyPath) ? tempLegacyPath : tempAegisPath;
    }
}

public sealed class FileWorkMapStore : IWorkMapStore
{
    private const int MaxFileAttempts = 25;

    private readonly IWorkMapPathProvider _pathProvider;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileWorkMapStore(IWorkMapPathProvider? pathProvider = null)
    {
        _pathProvider = pathProvider ?? new DefaultWorkMapPathProvider();
    }

    public string DirectoryPath => _pathProvider.DirectoryPath;

    public Task SaveMissionAsync(WorkMapMissionRecord mission, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("missions", mission.Id), mission, cancellationToken);

    public Task<WorkMapMissionRecord?> GetMissionAsync(string missionId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapMissionRecord>(PathFor("missions", missionId), cancellationToken);

    public Task<IReadOnlyList<WorkMapMissionRecord>> GetMissionsAsync(CancellationToken cancellationToken = default) =>
        LoadAllAsync<WorkMapMissionRecord>("missions", cancellationToken);

    public Task<WorkMapMissionRecord> UpdateMissionAsync(
        string missionId,
        Func<WorkMapMissionRecord, WorkMapMissionRecord> update,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(PathFor("missions", missionId), update, cancellationToken);

    public Task SaveWorkstreamAsync(WorkMapWorkstreamRecord workstream, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("workstreams", workstream.Id), workstream, cancellationToken);

    public Task<WorkMapWorkstreamRecord?> GetWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapWorkstreamRecord>(PathFor("workstreams", workstreamId), cancellationToken);

    public Task<IReadOnlyList<WorkMapWorkstreamRecord>> GetWorkstreamsAsync(CancellationToken cancellationToken = default) =>
        LoadAllAsync<WorkMapWorkstreamRecord>("workstreams", cancellationToken);

    public async Task<IReadOnlyList<WorkMapWorkstreamRecord>> GetWorkstreamsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var all = await GetWorkstreamsAsync(cancellationToken);
        return all.Where(item => string.Equals(item.MissionId, missionId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public Task<WorkMapWorkstreamRecord> UpdateWorkstreamAsync(
        string workstreamId,
        Func<WorkMapWorkstreamRecord, WorkMapWorkstreamRecord> update,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(PathFor("workstreams", workstreamId), update, cancellationToken);

    public Task DeleteWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default) =>
        DeleteAsync(PathFor("workstreams", workstreamId), cancellationToken);

    public Task SaveAgentSessionAsync(WorkMapAgentSessionRecord session, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("sessions", session.Id), session, cancellationToken);

    public Task<WorkMapAgentSessionRecord?> GetAgentSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapAgentSessionRecord>(PathFor("sessions", sessionId), cancellationToken);

    public Task<IReadOnlyList<WorkMapAgentSessionRecord>> GetAgentSessionsAsync(CancellationToken cancellationToken = default) =>
        LoadAllAsync<WorkMapAgentSessionRecord>("sessions", cancellationToken);

    public async Task<IReadOnlyList<WorkMapAgentSessionRecord>> GetAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAgentSessionsAsync(cancellationToken);
        return all.Where(item => string.Equals(item.MissionId, missionId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public Task<WorkMapAgentSessionRecord> UpdateAgentSessionAsync(
        string sessionId,
        Func<WorkMapAgentSessionRecord, WorkMapAgentSessionRecord> update,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(PathFor("sessions", sessionId), update, cancellationToken);

    private async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await WithRecordLockAsync(path, () => SaveUnlockedAsync(path, value, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> UpdateAsync<T>(string path, Func<T, T> update, CancellationToken cancellationToken)
    {
        T? updated = default;
        await WithRecordLockAsync(
            path,
            async () =>
            {
                var current = await LoadUnlockedAsync<T>(path, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    throw new ArgumentException($"Unknown cell record '{Path.GetFileNameWithoutExtension(path)}'.");
                }

                updated = update(current);
                await SaveUnlockedAsync(path, updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return updated!;
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await WithRecordLockAsync(
            path,
            async () =>
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                await Task.CompletedTask.ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveUnlockedAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return await RetryFileOperationAsync(
            () => LoadUnlockedAsync<T>(path, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> LoadUnlockedAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> LoadAllAsync<T>(string folder, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_pathProvider.DirectoryPath, folder);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<T>();
        }

        var records = new List<T>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var loaded = await LoadAsync<T>(file, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                records.Add(loaded);
            }
        }

        return records;
    }

    private static async Task WithRecordLockAsync(string path, Func<Task> action, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lockPath = path + ".lock";
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                await action().ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < MaxFileAttempts)
            {
                await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxFileAttempts)
            {
                await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> RetryFileOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < MaxFileAttempts)
            {
                await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxFileAttempts)
            {
                await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Task DelayBeforeRetry(int attempt, CancellationToken cancellationToken)
    {
        var delay = Math.Min(250, 10 + (attempt * 15));
        return Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
    }

    private string PathFor(string folder, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Work-map record id is required.");
        }

        var safeFileName = string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_pathProvider.DirectoryPath, folder, safeFileName + ".json");
    }
}
