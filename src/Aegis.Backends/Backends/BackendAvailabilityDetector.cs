using System.Text.Json.Serialization;
using Aegis.Core;

namespace Aegis.Backends;

public sealed class BackendAvailabilityProbeOptions
{
    public string? Path { get; init; }

    public string? PathExt { get; init; }

    public bool? IsWindows { get; init; }

    public Func<string, bool>? FileExists { get; init; }

    public Func<string, string?>? GetEnvironmentVariable { get; init; }
}

public sealed record BackendAvailabilityReport(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<BackendAvailability> Backends)
{
    [JsonIgnore]
    public BackendKind? PreferredBackendKind => Backends.FirstOrDefault(backend => backend.Available)?.Kind;

    public string? PreferredBackend => PreferredBackendKind?.ToOptionValue();

    public IReadOnlyList<string> SelectionOrder => Backends.Select(backend => backend.Backend).ToArray();

    public IReadOnlyList<string> AvailableBackends => Backends.Where(backend => backend.Available).Select(backend => backend.Backend).ToArray();
}

public sealed record BackendAvailability(
    [property: JsonIgnore] BackendKind Kind,
    int Rank,
    string Command,
    string Probe,
    bool Available,
    string? CommandPath,
    bool SupportsDetachedAsync,
    string LaunchMode,
    string Caveat)
{
    public string Backend => Kind.ToOptionValue();
}

public static class BackendAvailabilityDetector
{
    public static readonly IReadOnlyList<BackendKind> PreferredOrder =
    [
        BackendKind.Codex,
        BackendKind.Opencode,
        BackendKind.Pi,
        BackendKind.Copilot
    ];

    public static BackendAvailabilityReport Detect(BackendAvailabilityProbeOptions? options = null)
    {
        options ??= new BackendAvailabilityProbeOptions();
        var backends = PreferredOrder
            .Select((backend, index) => DetectBackend(backend, index + 1, options))
            .ToArray();

        return new BackendAvailabilityReport(DateTimeOffset.UtcNow, backends);
    }

    public static bool SupportsDetachedAsync(BackendKind backend) =>
        backend is BackendKind.Codex or BackendKind.Opencode;

    public static BackendKind? PreferredAvailableBackend(BackendAvailabilityProbeOptions? options = null) =>
        Detect(options).PreferredBackendKind;

    private static BackendAvailability DetectBackend(
        BackendKind backend,
        int rank,
        BackendAvailabilityProbeOptions options)
    {
        var command = ResolveConfiguredCommand(backend, options);
        var commandPath = FindCommand(command.Command, options);
        var available = commandPath is not null;

        return new BackendAvailability(
            Kind: backend,
            Rank: rank,
            Command: command.Command,
            Probe: command.Probe,
            Available: available,
            CommandPath: commandPath,
            SupportsDetachedAsync: SupportsDetachedAsync(backend),
            LaunchMode: SupportsDetachedAsync(backend) ? "detached-async" : "blocking",
            Caveat: CaveatFor(backend));
    }

    private static (string Command, string Probe) ResolveConfiguredCommand(
        BackendKind backend,
        BackendAvailabilityProbeOptions options)
    {
        var getEnvironmentVariable = options.GetEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        return backend switch
        {
            BackendKind.Codex => ConfiguredCommand(
                getEnvironmentVariable,
                "codex",
                "AEGIS_CODEX_BINARY",
                "HARNESS_CLI_CODEX_BINARY"),
            BackendKind.Copilot => ConfiguredCommand(
                getEnvironmentVariable,
                "copilot",
                "AEGIS_COPILOT_BINARY",
                "HARNESS_CLI_COPILOT_BINARY"),
            BackendKind.Pi => ("pi", "path-command"),
            _ => ("opencode", "path-command")
        };
    }

    private static (string Command, string Probe) ConfiguredCommand(
        Func<string, string?> getEnvironmentVariable,
        string fallback,
        params string[] variableNames)
    {
        foreach (var variableName in variableNames)
        {
            var configured = getEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return (configured, $"env:{variableName}");
            }
        }

        return (fallback, "path-command");
    }

    private static string? FindCommand(string command, BackendAvailabilityProbeOptions options)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var fileExists = options.FileExists ?? File.Exists;
        var isWindows = options.IsWindows ?? OperatingSystem.IsWindows();

        if (IsPathCommand(command))
        {
            return fileExists(command) ? command : null;
        }

        var path = options.Path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in CandidateCommandPaths(directory, command, options.PathExt, isWindows))
            {
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateCommandPaths(
        string directory,
        string command,
        string? pathExt,
        bool isWindows)
    {
        yield return Path.Combine(directory, command);

        if (!isWindows || Path.HasExtension(command))
        {
            yield break;
        }

        var extensions = (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
        {
            yield return Path.Combine(directory, command + extension);
        }
    }

    private static bool IsPathCommand(string command) =>
        Path.IsPathRooted(command)
        || command.Contains(Path.DirectorySeparatorChar)
        || command.Contains(Path.AltDirectorySeparatorChar);

    private static string CaveatFor(BackendKind backend) => backend switch
    {
        BackendKind.Codex =>
            "Command detection only; authentication and model readiness require a live backend smoke. Preferred backend when available.",
        BackendKind.Opencode =>
            "Command detection only; run `aegis ensure-server` and `aegis health` to verify the OpenCode server before use.",
        BackendKind.Pi =>
            "Command detection only; the current Pi adapter runs prompts synchronously instead of detached async fan-out.",
        BackendKind.Copilot =>
            "Command detection only; standalone GitHub Copilot CLI backend is blocking one-shot and does not support Aegis --async yet.",
        _ => "Command detection only; run a live backend smoke before relying on this backend."
    };
}
