using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace HarnessCli.IntegrationTests;

public class IntegrationSmokeTests
{
    [Fact]
    public void PlaceholderTest()
    {
        Assert.True(true);
    }

    [Fact]
    public async Task WorkMapLaunchDryRunPlansOnlyEligibleStreams()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var mission = JsonNode.Parse((await RunCli(tempRoot, "work-map", "create", "--title", "Fan out")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();

            await RunCli(tempRoot, "work-map", "stream", "add", "--mission", missionId, "--name", "API slice", "--status", "planned");
            await RunCli(tempRoot, "work-map", "stream", "add", "--mission", missionId, "--name", "Done slice", "--status", "complete");
            var linkedStream = JsonNode.Parse((await RunCli(tempRoot, "work-map", "stream", "add", "--mission", missionId, "--name", "Linked slice")).Stdout)!;
            var linkedStreamId = linkedStream["id"]!.GetValue<string>();
            await RunCli(tempRoot, "work-map", "session", "link", "--mission", missionId, "--stream", linkedStreamId, "--session", "codex-existing", "--backend", "codex");

            var launch = JsonNode.Parse((await RunCli(tempRoot, "work-map", "launch", "--mission", missionId, "--dry-run")).Stdout)!;

            Assert.Equal("codex", launch["backend"]!.GetValue<string>());
            Assert.True(launch["dryRun"]!.GetValue<bool>());
            Assert.Equal(1, launch["eligible"]!.GetValue<int>());
            Assert.Equal(1, launch["launchedCount"]!.GetValue<int>());
            Assert.Equal(2, launch["skippedCount"]!.GetValue<int>());
            Assert.Equal("planned", launch["launched"]!.AsArray()[0]!["status"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task WorkMapStoreExportsAndImportsPortableSnapshot()
    {
        var sourceStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var targetStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(sourceStore);
            Directory.CreateDirectory(targetStore);
            var mission = JsonNode.Parse((await RunCli(sourceStore, "work-map", "create", "--title", "Snapshot")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(sourceStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Portable slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();
            await RunCli(sourceStore, "work-map", "session", "link", "--mission", missionId, "--stream", streamId, "--session", "codex-portable", "--backend", "codex");

            await RunCli(sourceStore, "work-map", "store", "export", "--output", snapshot);
            Assert.True(File.Exists(snapshot));

            await RunCli(targetStore, "work-map", "store", "import", "--file", snapshot);
            var info = JsonNode.Parse((await RunCli(targetStore, "work-map", "store", "info")).Stdout)!;

            Assert.Equal("json-directory", info["provider"]!.GetValue<string>());
            Assert.Equal(1, info["missions"]!.GetValue<int>());
            Assert.Equal(1, info["workstreams"]!.GetValue<int>());
            Assert.Equal(1, info["sessions"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(sourceStore)) Directory.Delete(sourceStore, true);
            if (Directory.Exists(targetStore)) Directory.Delete(targetStore, true);
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }

    [Fact]
    public async Task WorkMapSessionSyncUsesPortableSessionRecordWithoutRegistry()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Portable sync")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Imported session")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            await RunCli(
                workMapStore,
                "work-map",
                "session",
                "link",
                "--mission",
                missionId,
                "--stream",
                streamId,
                "--session",
                "codex-imported",
                "--backend",
                "codex",
                "--backend-session",
                "codex-imported-backend",
                "--directory",
                workspace);

            var stateRoot = Path.Combine(workMapStore, "backend-state");
            var statePath = Path.Combine(stateRoot, "codex", WorkspaceKey(workspace), "codex-imported-backend");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(
                statePath + ".status.json",
                """{"ApiStatus":"idle","UpdatedAt":"2026-01-01T12:00:00+00:00"}""");
            await File.WriteAllTextAsync(
                statePath + ".messages.jsonl",
                """
                [{"Id":"msg_1","Role":"assistant","Text":"FINAL HANDOFF\nportable sync works","PartId":"part_1","Timestamp":"2026-01-01T12:00:00+00:00"}]
                """);

            var synced = JsonNode.Parse((await RunCli(workMapStore, "work-map", "session", "sync", "--session", "codex-imported")).Stdout)!;

            Assert.Equal("handoff", synced["status"]!.GetValue<string>());
            Assert.Contains("portable sync works", synced["finalHandoff"]!["text"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    private static async Task<CliResult> RunCli(string workMapStore, params string[] args)
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "harness-cli.dll");
        if (!File.Exists(cliPath))
        {
            cliPath = Path.Combine(LocateRepoRoot(Directory.GetCurrentDirectory()), "src", "HarnessCli", "bin", "Debug", "net10.0", "harness-cli.dll");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["HARNESS_CLI_WORK_MAP_DIR"] = workMapStore;
        startInfo.Environment["HARNESS_CLI_SESSION_DIR"] = Path.Combine(workMapStore, "sessions");
        startInfo.Environment["HARNESS_CLI_BACKEND_STATE_DIR"] = Path.Combine(workMapStore, "backend-state");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start harness-cli test process.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out waiting for harness-cli test process.");
        }

        var result = new CliResult(process.ExitCode, await stdout, await stderr);
        Assert.True(result.ExitCode == 0, $"harness-cli failed with exit {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        return result;
    }

    private static string WorkspaceKey(string workspaceDirectory)
    {
        var fullPath = Path.GetFullPath(workspaceDirectory);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string LocateRepoRoot(string startPath)
    {
        var path = Path.GetFullPath(startPath);
        for (var depth = 0; depth < 12; depth++)
        {
            if (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }

            path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Unable to find repo root.");
        }

        throw new InvalidOperationException("Could not locate repository root from test working directory.");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
