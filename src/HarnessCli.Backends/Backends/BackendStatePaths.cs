using System.Security.Cryptography;
using System.Text;

namespace HarnessCli.Backends;

internal static class BackendStatePaths
{
    public static string ResolveSessionPath(
        string backend,
        string backendSessionId,
        string? workspaceDirectory,
        string? stateRoot = null)
    {
        var root = ResolveRoot(stateRoot);
        var workspaceKey = ResolveWorkspaceKey(workspaceDirectory);
        var directory = Path.Combine(root, backend, workspaceKey);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, backendSessionId);
    }

    private static string ResolveRoot(string? stateRoot)
    {
        if (!string.IsNullOrWhiteSpace(stateRoot))
        {
            return Path.GetFullPath(stateRoot);
        }

        var explicitPath = Environment.GetEnvironmentVariable("AEGIS_BACKEND_STATE_DIR")
                           ?? Environment.GetEnvironmentVariable("HARNESS_CLI_BACKEND_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var aegisPath = Path.Combine(appData, "aegis", "backend-state");
            var legacyPath = Path.Combine(appData, "harness-cli", "backend-state");
            return !Directory.Exists(aegisPath) && Directory.Exists(legacyPath) ? legacyPath : aegisPath;
        }

        var tempAegisPath = Path.Combine(Path.GetTempPath(), "aegis", "backend-state");
        var tempLegacyPath = Path.Combine(Path.GetTempPath(), "harness-cli", "backend-state");
        return !Directory.Exists(tempAegisPath) && Directory.Exists(tempLegacyPath) ? tempLegacyPath : tempAegisPath;
    }

    private static string ResolveWorkspaceKey(string? workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return "no-workspace";
        }

        var fullPath = Path.GetFullPath(workspaceDirectory);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
