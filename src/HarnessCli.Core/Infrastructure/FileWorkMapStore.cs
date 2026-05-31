using System.Text.Json;
using HarnessCli.Core;

namespace HarnessCli.Infrastructure;

public sealed class DefaultWorkMapPathProvider : IWorkMapPathProvider
{
    public string DirectoryPath { get; } = ResolvePath();

    private static string ResolvePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("HARNESS_CLI_WORK_MAP_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(appData, "harness-cli", "work-map")
            : Path.Combine(Path.GetTempPath(), "harness-cli", "work-map");
    }
}

public sealed class FileWorkMapStore : IWorkMapStore
{
    private readonly IWorkMapPathProvider _pathProvider;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FileWorkMapStore(IWorkMapPathProvider? pathProvider = null)
    {
        _pathProvider = pathProvider ?? new DefaultWorkMapPathProvider();
    }

    public Task SaveMissionAsync(WorkMapMissionRecord mission, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("missions", mission.Id), mission, cancellationToken);

    public Task<WorkMapMissionRecord?> GetMissionAsync(string missionId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapMissionRecord>(PathFor("missions", missionId), cancellationToken);

    public Task<IReadOnlyList<WorkMapMissionRecord>> GetMissionsAsync(CancellationToken cancellationToken = default) =>
        LoadAllAsync<WorkMapMissionRecord>("missions", cancellationToken);

    public Task SaveWorkstreamAsync(WorkMapWorkstreamRecord workstream, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("workstreams", workstream.Id), workstream, cancellationToken);

    public Task<WorkMapWorkstreamRecord?> GetWorkstreamAsync(string workstreamId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapWorkstreamRecord>(PathFor("workstreams", workstreamId), cancellationToken);

    public async Task<IReadOnlyList<WorkMapWorkstreamRecord>> GetWorkstreamsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync<WorkMapWorkstreamRecord>("workstreams", cancellationToken);
        return all.Where(item => string.Equals(item.MissionId, missionId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public Task SaveAgentSessionAsync(WorkMapAgentSessionRecord session, CancellationToken cancellationToken = default) =>
        SaveAsync(PathFor("sessions", session.Id), session, cancellationToken);

    public Task<WorkMapAgentSessionRecord?> GetAgentSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        LoadAsync<WorkMapAgentSessionRecord>(PathFor("sessions", sessionId), cancellationToken);

    public async Task<IReadOnlyList<WorkMapAgentSessionRecord>> GetAgentSessionsAsync(
        string missionId,
        CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync<WorkMapAgentSessionRecord>("sessions", cancellationToken);
        return all.Where(item => string.Equals(item.MissionId, missionId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
