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
    public async Task WorkMapSessionLinkAcceptsExternalBackendAndSyncSkipsIt()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "External worker")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Background shipper")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var linked = JsonNode.Parse((await RunCli(
                workMapStore,
                "work-map",
                "session",
                "link",
                "--mission",
                missionId,
                "--stream",
                streamId,
                "--session",
                "synthesis-shipper",
                "--backend",
                "Shipper",
                "--status",
                "running")).Stdout)!;

            Assert.Equal("shipper", linked["backend"]!.GetValue<string>());
            Assert.Equal("running", linked["status"]!.GetValue<string>());

            var synced = JsonNode.Parse((await RunCli(workMapStore, "work-map", "session", "sync", "--mission", missionId, "--all")).Stdout)!.AsArray();
            Assert.Single(synced);
            Assert.Equal("running", synced[0]!["status"]!.GetValue<string>());
            Assert.Equal("shipper", synced[0]!["backend"]!.GetValue<string>());
            Assert.Contains(synced[0]!["events"]!.AsArray(), item => item?["type"]?.GetValue<string>() == "syncSkipped");

            var supervision = JsonNode.Parse((await RunCli(workMapStore, "work-map", "supervise", "--mission", missionId, "--max-runs", "1")).Stdout)!;
            Assert.Equal(1, supervision["active"]!.GetValue<int>());
            Assert.Equal(0, supervision["blocked"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task WorkMapLaunchIgnoresArchivedSessionsWhenFindingEligibleStreams()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Relaunch")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Retry slice")).Stdout)!;
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
                "stale-shipper",
                "--backend",
                "shipper",
                "--status",
                "blocked");
            await RunCli(workMapStore, "work-map", "session", "archive", "--session", "stale-shipper");

            var launch = JsonNode.Parse((await RunCli(workMapStore, "work-map", "launch", "--mission", missionId, "--dry-run")).Stdout)!;

            Assert.Equal(1, launch["eligible"]!.GetValue<int>());
            Assert.Equal(1, launch["launchedCount"]!.GetValue<int>());
            Assert.Equal(0, launch["skippedCount"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task WorkMapShowEmitsNextCommandHintsWithoutPollutingJson()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Hinted stream");

            var shown = await RunCli(workMapStore, "work-map", "show", "--mission", missionId);
            var bundle = JsonNode.Parse(shown.Stdout)!;
            Assert.Equal(missionId, bundle["mission"]!["id"]!.GetValue<string>());
            AssertNextCommandHints(shown.Stderr, missionId, "<stream>");

            var markdown = await RunCli(workMapStore, "work-map", "show", "--mission", missionId, "--format", "md");
            Assert.Contains("# Hints", markdown.Stdout);
            AssertNextCommandHints(markdown.Stdout, missionId, "<stream>");

            var html = await RunCli(workMapStore, "work-map", "show", "--mission", missionId, "--format", "html");
            Assert.Contains("<!doctype html>", html.Stdout);
            AssertNextCommandHints(html.Stderr, missionId, "<stream>");
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task WorkMapStreamAddEmitsNextCommandHintsWithNewStreamContext()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Stream hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();

            var added = await RunCli(
                workMapStore,
                "work-map",
                "stream",
                "add",
                "--mission",
                missionId,
                "--name",
                "Worker slice",
                "--role",
                "builder",
                "--clone",
                workspace);
            var stream = JsonNode.Parse(added.Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            AssertNextCommandHints(added.Stderr, missionId, streamId);
            Assert.Contains("--directory \"" + workspace, added.Stderr);
            Assert.Contains("--role \"builder\"", added.Stderr);

            var nonDefaultFormat = await RunCli(
                workMapStore,
                "work-map",
                "stream",
                "add",
                "--mission",
                missionId,
                "--name",
                "Format tolerant",
                "--format",
                "md");
            var formatTolerantStream = JsonNode.Parse(nonDefaultFormat.Stdout)!;
            AssertNextCommandHints(nonDefaultFormat.Stderr, missionId, formatTolerantStream["id"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task WorkMapMissionUpdateAndEvidenceAddEmitNextCommandHints()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Mutation hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Evidence stream")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var updated = await RunCli(workMapStore, "work-map", "mission", "update", "--mission", missionId, "--status", "in-progress");
            _ = JsonNode.Parse(updated.Stdout)!;
            AssertNextCommandHints(updated.Stderr, missionId, "<stream>");

            var evidence = await RunCli(workMapStore, "work-map", "evidence", "add", "--mission", missionId, "--stream", streamId, "--summary", "Useful fact");
            _ = JsonNode.Parse(evidence.Stdout)!;
            AssertNextCommandHints(evidence.Stderr, missionId, streamId);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
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
                "--model",
                "codex-model",
                "--variant",
                "high",
                "--agent",
                "build",
                "--directory",
                workspace,
                "--title",
                "Slow codex title",
                "--prompt",
                "slow fake codex",
                "--timeout",
                "12");

            var earlySession = await WaitForMissionSession(workMapStore, missionId, process);

            Assert.False(process.HasExited);
            Assert.Equal("running", earlySession["status"]!.GetValue<string>());
            Assert.Equal(missionId, earlySession["missionId"]!.GetValue<string>());
            Assert.Equal(streamId, earlySession["workstreamId"]!.GetValue<string>());
            Assert.Equal("codex", earlySession["backend"]!.GetValue<string>());
            Assert.Equal("codex-model", earlySession["model"]!.GetValue<string>());
            Assert.Equal("high", earlySession["variant"]!.GetValue<string>());
            Assert.Equal("build", earlySession["agent"]!.GetValue<string>());
            Assert.Equal(workspace, earlySession["directory"]!.GetValue<string>());

            await WaitForCliExit(process, TimeSpan.FromSeconds(30));
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            Assert.True(process.ExitCode == 0, $"harness-cli failed with exit {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            var output = JsonNode.Parse(stdout)!;
            Assert.Equal("codex", output["backend"]!.GetValue<string>());
            Assert.Equal("codex-model", output["model"]!.GetValue<string>());
            Assert.Equal("high", output["variant"]!.GetValue<string>());
            Assert.Equal("build", output["agent"]!.GetValue<string>());
            Assert.Equal(workspace, output["directory"]!.GetValue<string>());
            Assert.Contains("work-map supervise", output["nextCommands"]![0]!.GetValue<string>());
            Assert.Contains("last-summary --backend codex", output["nextCommands"]![1]!.GetValue<string>());

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
    public async Task WorkMapSessionRunRoutesGithubCopilotProviderModelThroughOpenCode()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var server = await FakeOpenCodeServer.StartAsync();
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Provider model")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "OpenCode route")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var result = await RunCli(
                workMapStore,
                "work-map",
                "session",
                "run",
                "--mission",
                missionId,
                "--stream",
                streamId,
                "--backend",
                "copilot",
                "--model",
                "github-copilot/gpt-5.5",
                "--variant",
                "high",
                "--agent",
                "build",
                "--directory",
                workspace,
                "--server",
                server.Url,
                "--prompt",
                "fake opencode",
                "--no-reply");

            var output = JsonNode.Parse(result.Stdout)!;
            Assert.Equal("copilot", output["requestedBackend"]!.GetValue<string>());
            Assert.Equal("opencode", output["backend"]!.GetValue<string>());
            Assert.Equal("github-copilot", output["provider"]!.GetValue<string>());
            Assert.Equal("gpt-5.5", output["model"]!.GetValue<string>());
            Assert.Equal("high", output["variant"]!.GetValue<string>());
            Assert.Equal("build", output["agent"]!.GetValue<string>());
            Assert.Equal(workspace, output["directory"]!.GetValue<string>());
            Assert.Contains("work-map supervise", output["nextCommands"]![0]!.GetValue<string>());
            Assert.Contains("last-summary --backend opencode", output["nextCommands"]![1]!.GetValue<string>());

            var sessions = JsonNode.Parse((await RunCli(workMapStore, "work-map", "show", "--mission", missionId)).Stdout)!["sessions"]!.AsArray();
            var session = Assert.Single(sessions);
            Assert.Equal("opencode", session!["backend"]!.GetValue<string>());
            Assert.Equal("github-copilot", session["provider"]!.GetValue<string>());
            Assert.Equal("gpt-5.5", session["model"]!.GetValue<string>());
            Assert.Equal("high", session["variant"]!.GetValue<string>());
            Assert.Equal("build", session["agent"]!.GetValue<string>());

            var promptBody = await server.WaitForPromptBodyAsync();
            Assert.Equal("build", promptBody["agent"]!.GetValue<string>());
            Assert.Equal("github-copilot", promptBody["model"]!["providerID"]!.GetValue<string>());
            Assert.Equal("gpt-5.5", promptBody["model"]!["modelID"]!.GetValue<string>());
            Assert.Equal("high", promptBody["variant"]!.GetValue<string>());
            Assert.Equal(workspace, promptBody["directory"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
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
    public async Task WorkMapSessionRunRejectsCopilotAsyncBeforeCreatingSessionRecord()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "work-map", "create", "--title", "Copilot async")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "work-map", "stream", "add", "--mission", missionId, "--name", "Rejected async")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var result = await RunCliAllowFailure(
                workMapStore,
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
                "--async");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("does not support --async", result.Stderr);
            var sessionsDirectory = Path.Combine(workMapStore, "sessions");
            Assert.True(!Directory.Exists(sessionsDirectory) || !Directory.EnumerateFiles(sessionsDirectory, "*.json").Any());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
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
            var sessionRunHelp = await RunCli(workMapStore, "work-map", "session", "run", "--help");

            Assert.Contains("--access-log FILE", help.Stdout);
            Assert.Contains("manual external backend labels", help.Stdout);
            Assert.Contains("session archive --session ID", help.Stdout);
            Assert.Contains("tailscale serve --bg http://127.0.0.1:4896/", help.Stdout);
            Assert.Contains("--model MODEL", sessionRunHelp.Stdout);
            Assert.Contains("--variant NAME", sessionRunHelp.Stdout);
            Assert.Contains("--reasoning", sessionRunHelp.Stdout);
            Assert.Contains("--agent NAME", sessionRunHelp.Stdout);
            Assert.Contains("--directory PATH", sessionRunHelp.Stdout);
            Assert.Contains("--summary-marker TEXT", sessionRunHelp.Stdout);
            Assert.Contains("same execution controls as ask", sessionRunHelp.Stdout);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    private static Task<CliResult> RunCli(string workMapStore, params string[] args) =>
        RunCli(workMapStore, null, args);

    private static Task<CliResult> RunCliAllowFailure(string workMapStore, params string[] args) =>
        RunCli(workMapStore, null, assertSuccess: false, args);

    private static void AssertNextCommandHints(string text, string missionId, string streamId)
    {
        Assert.Contains("Next useful commands:", text);
        Assert.Contains("harness-cli work-map session run", text);
        Assert.Contains($"--mission {missionId} --stream {streamId}", text);
        Assert.Contains("--model github-copilot/gpt-5.5 --variant high --agent build", text);
        Assert.Contains("harness-cli ask --model github-copilot/gpt-5.5 --variant high --agent build", text);
        Assert.Contains("--prompt-file \"<brief.md>\" --timeout 900", text);
        Assert.Contains($"harness-cli work-map session link --mission {missionId} --stream {streamId}", text);
        Assert.Contains("harness-cli last-summary --session <ses_...> --plain", text);
        Assert.Contains("Use --backend copilot without a github-copilot provider model only for the standalone Copilot CLI backend.", text);
        Assert.Contains("Use --model github-copilot/gpt-5.5 with work-map session run or harness-cli ask for OpenCode sessions using the GitHub Copilot provider.", text);
        Assert.DoesNotContain("opencode run", text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CliResult> RunCli(string workMapStore, Action<ProcessStartInfo>? configureStartInfo, params string[] args)
    {
        return await RunCli(workMapStore, configureStartInfo, assertSuccess: true, args);
    }

    private static async Task<CliResult> RunCli(
        string workMapStore,
        Action<ProcessStartInfo>? configureStartInfo,
        bool assertSuccess,
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
        if (assertSuccess)
        {
            Assert.True(result.ExitCode == 0, $"harness-cli failed with exit {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
        }

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

    private sealed class FakeOpenCodeServer : IAsyncDisposable
    {
        private const string SessionId = "ses_fake_opencode";
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;
        private readonly TaskCompletionSource<JsonNode> _promptBody = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeOpenCodeServer(TcpListener listener)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptLoop);
        }

        public string Url { get; }

        public static Task<FakeOpenCodeServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeOpenCodeServer(listener));
        }

        public async Task<JsonNode> WaitForPromptBodyAsync()
        {
            var completed = await Task.WhenAny(_promptBody.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != _promptBody.Task)
            {
                throw new TimeoutException("Timed out waiting for fake OpenCode prompt body.");
            }

            return await _promptBody.Task;
        }

        private async Task AcceptLoop()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                if (requestLine is null)
                {
                    return;
                }

                var parts = requestLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : string.Empty;
                var path = parts.Length > 1 ? parts[1] : "/";
                var contentLength = 0;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(header["Content-Length:".Length..].Trim(), out var parsed))
                    {
                        contentLength = parsed;
                    }
                }

                var body = string.Empty;
                if (contentLength > 0)
                {
                    var buffer = new char[contentLength];
                    var read = 0;
                    while (read < contentLength)
                    {
                        var count = await reader.ReadAsync(buffer, read, contentLength - read);
                        if (count == 0) break;
                        read += count;
                    }

                    body = new string(buffer, 0, read);
                }

                if (method == "POST" && path.StartsWith("/session?", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, "200 OK", """{"id":"ses_fake_opencode"}""");
                    return;
                }

                if (method == "GET" && path.StartsWith($"/session/{SessionId}/message", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, "200 OK", "[]");
                    return;
                }

                if (method == "POST" && path.StartsWith($"/session/{SessionId}/message", StringComparison.Ordinal))
                {
                    _promptBody.TrySetResult(JsonNode.Parse(body)!);
                    await WriteResponseAsync(stream, "204 No Content", string.Empty);
                    return;
                }

                if (method == "GET" && path.Equals("/session/status", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, "200 OK", """{"ses_fake_opencode":{"type":"idle"}}""");
                    return;
                }

                await WriteResponseAsync(stream, "404 Not Found", "{}");
            }
        }

        private static async Task WriteResponseAsync(Stream stream, string status, string body)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }

            _cancellation.Dispose();
        }
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
