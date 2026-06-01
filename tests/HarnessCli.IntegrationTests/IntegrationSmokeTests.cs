using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task WorkMapSessionRunAttachesSessionBeforeBlockingCodexCompletes()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fakeBin = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Process? process = null;
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(fakeBin);
            CreateFakeSlowCodex(fakeBin);

            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Visible run")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Slow codex")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            process = StartCliProcess(
                workMapStore,
                startInfo =>
                {
                    var path = startInfo.Environment.TryGetValue("PATH", out var existingPath)
                        ? existingPath
                        : Environment.GetEnvironmentVariable("PATH");
                    startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + path;
                    startInfo.Environment["HARNESS_CLI_CODEX_BINARY"] = OperatingSystem.IsWindows()
                        ? Path.Combine(fakeBin, "codex.cmd")
                        : Path.Combine(fakeBin, "codex");
                },
                "work-map",
                "session",
                "run",
                "--mission",
                missionId,
                "--stream",
                streamId,
                "--backend",
                "codex",
                "--directory",
                workspace,
                "--prompt",
                "slow fake codex",
                "--timeout",
                "12");

            var earlySession = await WaitForMissionSession(workMapStore, missionId, process);

            Assert.False(process.HasExited);
            Assert.Equal("running", earlySession["status"]!.GetValue<string>());
            Assert.Equal(missionId, earlySession["missionId"]!.GetValue<string>());
            Assert.Equal(streamId, earlySession["workstreamId"]!.GetValue<string>());

            await WaitForCliExit(process, TimeSpan.FromSeconds(30));
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            Assert.True(process.ExitCode == 0, $"harness-cli failed with exit {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            var finalSession = await ReadWorkMapSession(workMapStore, earlySession["id"]!.GetValue<string>());
            Assert.Equal("handoff", finalSession["status"]!.GetValue<string>());
            Assert.Contains("fake codex done", finalSession["finalHandoff"]!["text"]!.GetValue<string>());
        }
        finally
        {
            if (process is not null)
            {
                await StopProcess(process);
            }

            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            if (Directory.Exists(fakeBin)) Directory.Delete(fakeBin, true);
        }
    }

    [Fact]
    public async Task WorkMapSessionRunForwardsCopilotPermissionFlags()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fakeBin = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(fakeBin);
            var argsPath = CreateFakeCopilot(fakeBin);

            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Copilot flags")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Copilot slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            await RunCli(
                workMapStore,
                startInfo =>
                {
                    var path = startInfo.Environment.TryGetValue("PATH", out var existingPath)
                        ? existingPath
                        : Environment.GetEnvironmentVariable("PATH");
                    startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + path;
                    startInfo.Environment["HARNESS_CLI_COPILOT_BINARY"] = OperatingSystem.IsWindows()
                        ? Path.Combine(fakeBin, "fake-copilot.ps1")
                        : Path.Combine(fakeBin, "copilot");
                },
                "work-map",
                "session",
                "run",
                "--mission",
                missionId,
                "--stream",
                streamId,
                "--backend",
                "copilot",
                "--directory",
                workspace,
                "--prompt",
                "fake copilot",
                "--copilot-allow-tool",
                "Edit",
                "--copilot-allow-tool",
                "Bash",
                "--copilot-allow-url",
                "https://github.com",
                "--copilot-allow-all",
                "--timeout",
                "12");

            var args = await File.ReadAllTextAsync(argsPath);
            Assert.Contains("--allow-tool", args);
            Assert.Contains("Edit", args);
            Assert.Contains("Bash", args);
            Assert.Contains("--allow-url", args);
            Assert.Contains("https://github.com", args);
            Assert.Contains("--allow-all", args);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            if (Directory.Exists(fakeBin)) Directory.Delete(fakeBin, true);
        }
    }

    [Fact]
    public async Task WorkMapServeWritesJsonlAccessLog()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var accessLog = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "access.jsonl");
        var port = GetFreeTcpPort();
        Process? process = null;
        try
        {
            Directory.CreateDirectory(workMapStore);
            process = StartCliProcess(
                workMapStore,
                "work-map",
                "serve",
                "--host",
                "127.0.0.1",
                "--port",
                port.ToString(),
                "--access-log",
                accessLog);

            await WaitForObserver(port, process);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HarnessCliTest", "1.0"));
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var entry = await WaitForAccessLogEntry(accessLog, "/api/health", "HarnessCliTest/1.0");

            Assert.Equal("GET", entry["method"]!.GetValue<string>());
            Assert.Equal("/api/health", entry["path"]!.GetValue<string>());
            Assert.Equal(200, entry["statusCode"]!.GetValue<int>());
            Assert.Equal("HarnessCliTest/1.0", entry["userAgent"]!.GetValue<string>());
        }
        finally
        {
            if (process is not null)
            {
                await StopProcess(process);
            }

            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            var accessLogDirectory = Path.GetDirectoryName(accessLog);
            if (accessLogDirectory is not null && Directory.Exists(accessLogDirectory))
            {
                Directory.Delete(accessLogDirectory, true);
            }
        }
    }

    [Fact]
    public async Task WorkMapHelpDocumentsAccessLogAndTailscaleServe()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var help = await RunCli(workMapStore, "help", "work-map");

            Assert.Contains("--access-log FILE", help.Stdout);
            Assert.Contains("tailscale serve --bg http://127.0.0.1:4896/", help.Stdout);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    private static Task<CliResult> RunCli(string workMapStore, params string[] args) =>
        RunCli(workMapStore, null, args);

    private static async Task<CliResult> RunCli(string workMapStore, Action<ProcessStartInfo>? configureStartInfo, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(LocateCliPath());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["HARNESS_CLI_WORK_MAP_DIR"] = workMapStore;
        startInfo.Environment["HARNESS_CLI_SESSION_DIR"] = Path.Combine(workMapStore, "session-registry");
        startInfo.Environment["HARNESS_CLI_BACKEND_STATE_DIR"] = Path.Combine(workMapStore, "backend-state");
        configureStartInfo?.Invoke(startInfo);

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

    private static Process StartCliProcess(string workMapStore, params string[] args) =>
        StartCliProcess(workMapStore, null, args);

    private static Process StartCliProcess(
        string workMapStore,
        Action<ProcessStartInfo>? configureStartInfo,
        params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(LocateCliPath());
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["HARNESS_CLI_WORK_MAP_DIR"] = workMapStore;
        startInfo.Environment["HARNESS_CLI_SESSION_DIR"] = Path.Combine(workMapStore, "session-registry");
        startInfo.Environment["HARNESS_CLI_BACKEND_STATE_DIR"] = Path.Combine(workMapStore, "backend-state");
        configureStartInfo?.Invoke(startInfo);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start harness-cli test process.");
        process.StandardInput.Close();
        return process;
    }

    private static void CreateFakeSlowCodex(string fakeBin)
    {
        const string outputLine = """{"type":"item.completed","item":{"type":"agent_message","id":"msg_fake","text":"FINAL HANDOFF\nfake codex done"},"timestamp":"2026-01-01T12:00:00+00:00"}""";
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(fakeBin, "fake-codex.ps1");
            File.WriteAllText(
                scriptPath,
                string.Join(
                    Environment.NewLine,
                    "Start-Sleep -Seconds 5",
                    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
                    $"Write-Output '{outputLine}'",
                    "exit 0"));
            File.WriteAllText(
                Path.Combine(fakeBin, "codex.cmd"),
                string.Join(Environment.NewLine, "@echo off", "powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-codex.ps1\""));
            return;
        }

        var executablePath = Path.Combine(fakeBin, "codex");
        File.WriteAllText(
            executablePath,
            string.Join(
                "\n",
                "#!/bin/sh",
                "sleep 5",
                "printf '%s\\n' '" + outputLine.Replace("'", "'\"'\"'") + "'",
                "exit 0"));
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string CreateFakeCopilot(string fakeBin)
    {
        var argsPath = Path.Combine(fakeBin, "copilot-args.txt");
        const string outputLine = "FINAL HANDOFF\\nfake copilot done";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(fakeBin, "fake-copilot.ps1"),
                string.Join(
                    Environment.NewLine,
                    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
                    "[string]::Join(' ', $args) | Set-Content -LiteralPath \"" + argsPath + "\"",
                    "Write-Output '" + outputLine + "'",
                    "exit 0"));
            File.WriteAllText(
                Path.Combine(fakeBin, "copilot.cmd"),
                string.Join(Environment.NewLine, "@echo off", "powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-copilot.ps1\" %*"));
            return argsPath;
        }

        var executablePath = Path.Combine(fakeBin, "copilot");
        File.WriteAllText(
            executablePath,
            string.Join(
                "\n",
                "#!/bin/sh",
                "printf '%s\\n' \"$*\" > '" + argsPath.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'",
                "printf '%s\\n' '" + outputLine + "'",
                "exit 0"));
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return argsPath;
    }

    private static async Task<JsonNode> WaitForMissionSession(string workMapStore, string missionId, Process process)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"work-map session run exited before a session record appeared with {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }

            var sessionsDirectory = Path.Combine(workMapStore, "sessions");
            if (Directory.Exists(sessionsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
                {
                    try
                    {
                        var node = JsonNode.Parse(await File.ReadAllTextAsync(file));
                        if (node is not null
                            && string.Equals(node["missionId"]?.GetValue<string>(), missionId, StringComparison.OrdinalIgnoreCase))
                        {
                            return node;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or JsonException)
                    {
                    }
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for a work-map session record for mission {missionId}.");
    }

    private static async Task<JsonNode> ReadWorkMapSession(string workMapStore, string sessionId)
    {
        var sessionPath = Path.Combine(workMapStore, "sessions", sessionId + ".json");
        return JsonNode.Parse(await File.ReadAllTextAsync(sessionPath))!;
    }

    private static async Task WaitForCliExit(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out waiting for harness-cli test process.");
        }
    }

    private static async Task WaitForObserver(int port, Process process)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"work-map serve exited early with {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }

            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for work-map serve on port {port}: {lastException?.Message}");
    }

    private static async Task<JsonNode> WaitForAccessLogEntry(string accessLog, string path, string? userAgent = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(accessLog))
                {
                    foreach (var line in await File.ReadAllLinesAsync(accessLog))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var node = JsonNode.Parse(line);
                        if (node is not null
                            && string.Equals(node["path"]?.GetValue<string>(), path, StringComparison.Ordinal)
                            && (userAgent is null
                                || string.Equals(node["userAgent"]?.GetValue<string>(), userAgent, StringComparison.Ordinal)))
                        {
                            return node;
                        }
                    }
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for access log entry for {path} in {accessLog}.");
    }

    private static async Task StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string LocateCliPath()
    {
        var cliPath = Path.Combine(AppContext.BaseDirectory, "harness-cli.dll");
        if (!File.Exists(cliPath))
        {
            cliPath = Path.Combine(LocateRepoRoot(Directory.GetCurrentDirectory()), "src", "HarnessCli", "bin", "Debug", "net10.0", "harness-cli.dll");
        }

        return cliPath;
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
