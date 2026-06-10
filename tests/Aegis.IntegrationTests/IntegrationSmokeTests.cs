using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Aegis.IntegrationTests;

public class IntegrationSmokeTests
{
    [Fact]
    public void PlaceholderTest()
    {
        Assert.True(true);
    }

    [Fact]
    public async Task CellLaunchDryRunPlansOnlyEligibleStreams()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var mission = JsonNode.Parse((await RunCli(tempRoot, "cell", "create", "--title", "Fan out")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();

            await RunCli(tempRoot, "cell", "stream", "add", "--cell", missionId, "--name", "API slice", "--status", "planned");
            await RunCli(tempRoot, "cell", "stream", "add", "--cell", missionId, "--name", "Done slice", "--status", "complete");
            var linkedStream = JsonNode.Parse((await RunCli(tempRoot, "cell", "stream", "add", "--cell", missionId, "--name", "Linked slice")).Stdout)!;
            var linkedStreamId = linkedStream["id"]!.GetValue<string>();
            await RunCli(tempRoot, "cell", "session", "link", "--cell", missionId, "--stream", linkedStreamId, "--session", "codex-existing", "--backend", "codex");

            var launch = JsonNode.Parse((await RunCli(tempRoot, "cell", "launch", "--cell", missionId, "--dry-run", "--backend", "codex")).Stdout)!;

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
    public async Task CellLaunchDryRunAutoSelectsDetectedBackend()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fakeBin = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(fakeBin);
            CreateFakeCommand(fakeBin, "opencode");

            var mission = JsonNode.Parse((await RunCli(tempRoot, "cell", "create", "--title", "Auto backend")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            await RunCli(tempRoot, "cell", "stream", "add", "--cell", missionId, "--name", "Auto slice", "--status", "planned");

            var launch = JsonNode.Parse((await RunCli(
                tempRoot,
                ConfigureBackendDetectionPath(fakeBin),
                "cell",
                "launch",
                "--cell",
                missionId,
                "--dry-run")).Stdout)!;

            Assert.Equal("opencode", launch["backend"]!.GetValue<string>());
            Assert.Equal("opencode", launch["launched"]!.AsArray()[0]!["backend"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            if (Directory.Exists(fakeBin)) Directory.Delete(fakeBin, true);
        }
    }

    [Fact]
    public async Task CellSessionRunRejectsAsyncForBlockingBackends()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Blocking async")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Pi slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var result = await RunCliAllowFailure(
                workMapStore,
                "cell",
                "session",
                "run",
                "--cell",
                missionId,
                "--stream",
                streamId,
                "--backend",
                "pi",
                "--async",
                "--prompt",
                "should not start pi");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("pi backend does not support --async yet", result.Stderr);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task BackendDetectReportsPreferredAvailableBackend()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fakeBin = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(fakeBin);
            CreateFakeCommand(fakeBin, "opencode");
            CreateFakeCommand(fakeBin, "copilot");

            var result = await RunCli(
                tempRoot,
                ConfigureBackendDetectionPath(fakeBin),
                "backend",
                "detect");

            var output = JsonNode.Parse(result.Stdout)!;
            Assert.Equal("opencode", output["preferredBackend"]!.GetValue<string>());
            Assert.Equal("codex", output["selectionOrder"]!.AsArray()[0]!.GetValue<string>());
            Assert.Equal("opencode", output["selectionOrder"]!.AsArray()[1]!.GetValue<string>());
            Assert.Contains(output["backends"]!.AsArray(), backend =>
                backend!["backend"]!.GetValue<string>() == "copilot"
                && backend["available"]!.GetValue<bool>()
                && backend["launchMode"]!.GetValue<string>() == "blocking");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            if (Directory.Exists(fakeBin)) Directory.Delete(fakeBin, true);
        }
    }

    private static Action<ProcessStartInfo> ConfigureBackendDetectionPath(string fakeBin) =>
        startInfo =>
        {
            startInfo.Environment["PATH"] = fakeBin;
            startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            startInfo.Environment.Remove("AEGIS_CODEX_BINARY");
            startInfo.Environment.Remove("HARNESS_CLI_CODEX_BINARY");
            startInfo.Environment.Remove("AEGIS_COPILOT_BINARY");
            startInfo.Environment.Remove("HARNESS_CLI_COPILOT_BINARY");
        };

    [Fact]
    public async Task CellStoreExportsAndImportsPortableSnapshot()
    {
        var sourceStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var targetStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(sourceStore);
            Directory.CreateDirectory(targetStore);
            var mission = JsonNode.Parse((await RunCli(sourceStore, "cell", "create", "--title", "Snapshot")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(sourceStore, "cell", "stream", "add", "--cell", missionId, "--name", "Portable slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();
            await RunCli(sourceStore, "cell", "session", "link", "--cell", missionId, "--stream", streamId, "--session", "codex-portable", "--backend", "codex");

            await RunCli(sourceStore, "cell", "store", "export", "--output", snapshot);
            Assert.True(File.Exists(snapshot));
            var exported = JsonNode.Parse(await File.ReadAllTextAsync(snapshot))!;
            Assert.Equal("cellStoreSnapshot", exported["kind"]!.GetValue<string>());

            await RunCli(targetStore, "cell", "store", "import", "--file", snapshot);
            var info = JsonNode.Parse((await RunCli(targetStore, "cell", "store", "info")).Stdout)!;

            Assert.Equal("json-directory", info["provider"]!.GetValue<string>());
            Assert.Equal(1, info["cells"]!.GetValue<int>());
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
    public async Task CellStoreImportsLegacyCellSnapshot()
    {
        var targetStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(targetStore);
            var legacyMissionId = "mission-legacy";
            var now = DateTimeOffset.UtcNow.ToString("O");
            var legacySnapshot = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["kind"] = "workMapSnapshot",
                ["exportedAtUtc"] = now,
                ["missions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["schemaVersion"] = 1,
                        ["kind"] = "mission",
                        ["id"] = legacyMissionId,
                        ["title"] = "Legacy mission",
                        ["status"] = "planned",
                        ["createdAtUtc"] = now,
                        ["updatedAtUtc"] = now
                    }
                },
                ["workstreams"] = new JsonArray(),
                ["sessions"] = new JsonArray()
            };
            await File.WriteAllTextAsync(snapshot, legacySnapshot.ToJsonString());

            await RunCli(targetStore, "cell", "store", "import", "--file", snapshot);
            var shown = JsonNode.Parse((await RunCli(targetStore, "cell", "show", "--cell", legacyMissionId)).Stdout)!;

            var record = shown["cell"] ?? shown["mission"]!;
            Assert.Equal(legacyMissionId, record["id"]!.GetValue<string>());
            Assert.Equal("mission", record["kind"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(targetStore)) Directory.Delete(targetStore, true);
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }

    [Fact]
    public async Task CellForkCreatesRecursiveChildCellLink()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var parent = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Parent cell")).Stdout)!;
            var parentId = parent["id"]!.GetValue<string>();

            var child = JsonNode.Parse((await RunCli(workMapStore, "cell", "fork", "--cell", parentId, "--title", "Child cell", "--intent", "Recursive slice")).Stdout)!;
            var childId = child["id"]!.GetValue<string>();

            Assert.StartsWith("cell-", parentId, StringComparison.Ordinal);
            Assert.StartsWith("cell-", childId, StringComparison.Ordinal);
            Assert.Equal("cell", parent["kind"]!.GetValue<string>());
            Assert.Equal("cell", child["kind"]!.GetValue<string>());
            Assert.Equal(parentId, child["parentCellId"]!.GetValue<string>());

            var shown = JsonNode.Parse((await RunCli(workMapStore, "cell", "show", "--cell", parentId)).Stdout)!;
            var parentRecord = shown["cell"] ?? shown["mission"]!;
            Assert.Contains(parentRecord["childCellIds"]!.AsArray(), item => item!.GetValue<string>() == childId);
            Assert.Contains(parentRecord["edges"]!.AsArray(), item =>
                item!["fromId"]!.GetValue<string>() == parentId
                && item!["toId"]!.GetValue<string>() == childId
                && item!["kind"]!.GetValue<string>() == "contains");
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task CellSessionSyncUsesPortableSessionRecordWithoutRegistry()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Portable sync")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Imported session")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            await RunCli(
                workMapStore,
                "cell",
                "session",
                "link",
                "--cell",
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

            var synced = JsonNode.Parse((await RunCli(workMapStore, "cell", "session", "sync", "--session", "codex-imported")).Stdout)!;

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
    public async Task CellSessionLinkAcceptsExternalBackendAndSyncSkipsIt()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "External worker")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Background shipper")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var linked = JsonNode.Parse((await RunCli(
                workMapStore,
                "cell",
                "session",
                "link",
                "--cell",
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

            var synced = JsonNode.Parse((await RunCli(workMapStore, "cell", "session", "sync", "--cell", missionId, "--all")).Stdout)!.AsArray();
            Assert.Single(synced);
            Assert.Equal("running", synced[0]!["status"]!.GetValue<string>());
            Assert.Equal("shipper", synced[0]!["backend"]!.GetValue<string>());
            Assert.Contains(synced[0]!["events"]!.AsArray(), item => item?["type"]?.GetValue<string>() == "syncSkipped");

            var supervision = JsonNode.Parse((await RunCli(workMapStore, "cell", "supervise", "--cell", missionId, "--max-runs", "1")).Stdout)!;
            Assert.Equal(1, supervision["active"]!.GetValue<int>());
            Assert.Equal(0, supervision["blocked"]!.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task CellLaunchIgnoresArchivedSessionsWhenFindingEligibleStreams()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Relaunch")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Retry slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();
            await RunCli(
                workMapStore,
                "cell",
                "session",
                "link",
                "--cell",
                missionId,
                "--stream",
                streamId,
                "--session",
                "stale-shipper",
                "--backend",
                "shipper",
                "--status",
                "blocked");
            await RunCli(workMapStore, "cell", "session", "archive", "--session", "stale-shipper");

            var launch = JsonNode.Parse((await RunCli(workMapStore, "cell", "launch", "--cell", missionId, "--dry-run")).Stdout)!;

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
    public async Task CellShowEmitsNextCommandHintsWithoutPollutingJson()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Hinted stream");

            var shown = await RunCli(workMapStore, "cell", "show", "--cell", missionId);
            var bundle = JsonNode.Parse(shown.Stdout)!;
            Assert.Equal(missionId, bundle["mission"]!["id"]!.GetValue<string>());
            AssertNextActionHint(shown, missionId);

            var markdown = await RunCli(workMapStore, "cell", "show", "--cell", missionId, "--format", "md");
            Assert.Contains("# Hints", markdown.Stdout);
            AssertInlineNextCommandHints(markdown.Stdout, missionId, "<stream>");
            AssertNextActionHint(markdown, missionId);

            var markdownPath = Path.Combine(workMapStore, "show.md");
            var markdownOutput = await RunCli(workMapStore, "cell", "show", "--cell", missionId, "--format", "md", "--output", markdownPath);
            Assert.Empty(markdownOutput.Stdout);
            Assert.DoesNotContain("Next useful commands:", await File.ReadAllTextAsync(markdownPath));
            AssertNextActionHint(markdownOutput, missionId);

            var html = await RunCli(workMapStore, "cell", "show", "--cell", missionId, "--format", "html");
            Assert.Contains("<!doctype html>", html.Stdout);
            AssertNextActionHint(html, missionId);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task CellStreamAddEmitsNextCommandHintsWithNewStreamContext()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Stream hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();

            var added = await RunCli(
                workMapStore,
                "cell",
                "stream",
                "add",
                "--cell",
                missionId,
                "--name",
                "Worker slice",
                "--role",
                "builder",
                "--clone",
                workspace);
            var stream = JsonNode.Parse(added.Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var addedHint = AssertNextActionHint(added, missionId, streamId);
            var addedCommands = addedHint["nextCommands"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
            Assert.Contains(addedCommands, command => command.Contains("--directory \"" + workspace, StringComparison.Ordinal));
            Assert.Contains(addedCommands, command => command.Contains("--role \"builder\"", StringComparison.Ordinal));

            var nonDefaultFormat = await RunCli(
                workMapStore,
                "cell",
                "stream",
                "add",
                "--cell",
                missionId,
                "--name",
                "Format tolerant",
                "--format",
                "md");
            var formatTolerantStream = JsonNode.Parse(nonDefaultFormat.Stdout)!;
            AssertNextActionHint(nonDefaultFormat, missionId, formatTolerantStream["id"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task CellMissionUpdateAndEvidenceAddEmitNextCommandHints()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Mutation hints")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Evidence stream")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var updated = await RunCli(workMapStore, "cell", "mission", "update", "--cell", missionId, "--status", "in-progress");
            _ = JsonNode.Parse(updated.Stdout)!;
            AssertNextActionHint(updated, missionId);

            var evidence = await RunCli(workMapStore, "cell", "evidence", "add", "--cell", missionId, "--stream", streamId, "--summary", "Useful fact");
            _ = JsonNode.Parse(evidence.Stdout)!;
            AssertNextActionHint(evidence, missionId, streamId);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
        }
    }

    [Fact]
    public async Task CellCommandsEmitStructuredNextActionForAgents()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var snapshot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);

            var create = await RunCli(workMapStore, "cell", "create", "--title", "Agent hints", "--intent", "Keep agents routed");
            var mission = JsonNode.Parse(create.Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            AssertNextActionHint(create, missionId);

            AssertNextActionHint(await RunCli(workMapStore, "cell", "list"), missionId: null);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "list", "--format", "md"), missionId: null);

            var streamAdd = await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Agent stream", "--role", "builder", "--clone", workspace);
            var stream = JsonNode.Parse(streamAdd.Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();
            AssertNextActionHint(streamAdd, missionId, streamId);

            AssertNextActionHint(await RunCli(workMapStore, "cell", "show", "--cell", missionId), missionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "brief", "--cell", missionId, "--stream", streamId), missionId, streamId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "stream", "update", "--cell", missionId, "--stream", streamId, "--status", "in-progress"), missionId, streamId);

            const string sessionId = "external-agent-session";
            var linkHint = AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "link", "--cell", missionId, "--stream", streamId, "--session", sessionId, "--backend", "external", "--role", "builder"), missionId, streamId, sessionId);
            Assert.DoesNotContain("last-summary --backend external", linkHint.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cell session handoff", linkHint.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "update", "--session", sessionId, "--status", "running"), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "handoff", "--session", sessionId, "--summary", "handoff ready"), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "blocker", "set", "--session", sessionId, "--summary", "blocked for test"), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "verify", "--session", sessionId, "--kind", "parent-review", "--result", "pass", "--summary", "looks good"), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "sync", "--session", sessionId), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "sync", "--cell", missionId, "--all"), missionId);

            var evidenceAdd = await RunCli(workMapStore, "cell", "evidence", "add", "--cell", missionId, "--stream", streamId, "--summary", "agent fact");
            var evidenceId = JsonNode.Parse(evidenceAdd.Stdout)!["id"]!.GetValue<string>();
            AssertNextActionHint(evidenceAdd, missionId, streamId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "evidence", "remove", "--cell", missionId, "--stream", streamId, "--evidence-id", evidenceId), missionId, streamId);

            AssertNextActionHint(await RunCli(workMapStore, "cell", "launch", "--cell", missionId, "--dry-run"), missionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "supervise", "--cell", missionId, "--max-runs", "1"), missionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "store", "info"), missionId: null);

            var export = await RunCli(workMapStore, "cell", "store", "export", "--output", snapshot);
            Assert.Empty(export.Stdout);
            AssertNextActionHint(export, missionId: null);
            _ = JsonNode.Parse(await File.ReadAllTextAsync(snapshot))!;
            var importConflict = await RunCliAllowFailure(workMapStore, "cell", "store", "import", "--file", snapshot, "--dry-run");
            Assert.Equal(1, importConflict.ExitCode);
            AssertNextActionHint(importConflict, missionId: null);

            AssertNextActionHint(await RunCli(workMapStore, "help", "cell"), missionId: null);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "--help"), missionId: null);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "session", "archive", "--session", sessionId), missionId, streamId, sessionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "stream", "delete", "--cell", missionId, "--stream", streamId, "--force"), missionId);
            AssertNextActionHint(await RunCli(workMapStore, "cell", "mission", "update", "--cell", missionId, "--status", "done"), missionId);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }
    }

    [Fact]
    public async Task CellSessionRunAttachesSessionBeforeBlockingCodexCompletes()
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

            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Visible run")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Slow codex")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            process = StartCliProcess(
                workMapStore,
                startInfo =>
                {
                    var path = startInfo.Environment.TryGetValue("PATH", out var existingPath)
                        ? existingPath
                        : Environment.GetEnvironmentVariable("PATH");
                    startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + path;
                    startInfo.Environment["AEGIS_CODEX_BINARY"] = OperatingSystem.IsWindows()
                        ? Path.Combine(fakeBin, "codex.cmd")
                        : Path.Combine(fakeBin, "codex");
                },
                "cell",
                "session",
                "run",
                "--cell",
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
            Assert.True(process.ExitCode == 0, $"aegis failed with exit {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            var output = JsonNode.Parse(stdout)!;
            Assert.Equal("codex", output["backend"]!.GetValue<string>());
            Assert.Equal("codex-model", output["model"]!.GetValue<string>());
            Assert.Equal("high", output["variant"]!.GetValue<string>());
            Assert.Equal("build", output["agent"]!.GetValue<string>());
            Assert.Equal(workspace, output["directory"]!.GetValue<string>());
            Assert.Contains("cell supervise", output["nextCommands"]![0]!.GetValue<string>());
            Assert.Contains("last-summary --backend codex", output["nextCommands"]![1]!.GetValue<string>());
            AssertNextActionHint(new CliResult(process.ExitCode, stdout, stderr), missionId, streamId, output["sessionID"]!.GetValue<string>());

            var finalSession = await ReadCellSession(workMapStore, earlySession["id"]!.GetValue<string>());
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
    public async Task CellSessionRunRoutesGithubCopilotProviderModelThroughOpenCode()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var server = await FakeOpenCodeServer.StartAsync();
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Provider model")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "OpenCode route")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var result = await RunCli(
                workMapStore,
                "cell",
                "session",
                "run",
                "--cell",
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
            Assert.Contains("cell supervise", output["nextCommands"]![0]!.GetValue<string>());
            Assert.Contains("last-summary --backend opencode", output["nextCommands"]![1]!.GetValue<string>());
            AssertNextActionHint(result, missionId, streamId, output["sessionID"]!.GetValue<string>());

            var sessions = JsonNode.Parse((await RunCli(workMapStore, "cell", "show", "--cell", missionId)).Stdout)!["sessions"]!.AsArray();
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
    public async Task DirectOpenCodeCommandsResolveCellWrapperSessionIds()
    {
        const string messagesJson = """
[
  {"info":{"id":"msg_user","role":"user"},"parts":[{"id":"part_user","type":"text","text":"finish"}]},
  {"info":{"id":"msg_assistant","role":"assistant"},"parts":[{"id":"part_assistant","type":"text","text":"FINAL HANDOFF\nwrapper resolved"}]}
]
""";
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var server = await FakeOpenCodeServer.StartAsync(messagesJson);
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Wrapper resolve")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "OpenCode linked")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();
            const string wrapperSessionId = "opencode-wrapper-test";

            await RunCli(
                workMapStore,
                "cell",
                "session",
                "link",
                "--cell",
                missionId,
                "--stream",
                streamId,
                "--session",
                wrapperSessionId,
                "--backend",
                "opencode",
                "--backend-session",
                "ses_fake_opencode",
                "--directory",
                workspace);

            var summary = await RunCli(
                workMapStore,
                "last-summary",
                "--backend",
                "opencode",
                "--session",
                wrapperSessionId,
                "--server",
                server.Url,
                "--plain");
            Assert.Contains("wrapper resolved", summary.Stdout);

            var tail = await RunCli(
                workMapStore,
                "tail",
                "--backend",
                "opencode",
                "--session",
                wrapperSessionId,
                "--server",
                server.Url,
                "--limit",
                "5",
                "--once");
            Assert.Contains("wrapper resolved", tail.Stdout);
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Theory]
    [InlineData("[]", "no assistant message")]
    [InlineData("""[{"info":{"id":"msg_empty","role":"assistant"},"parts":[{"id":"part_empty","type":"text","text":""}]}]""", "text was empty")]
    [InlineData("""[{"info":{"id":"msg_no_handoff","role":"assistant"},"parts":[{"id":"part_no_handoff","type":"text","text":"READY"}]}]""", "no 'FINAL HANDOFF' marker")]
    public async Task CellSessionSyncKeepsAsyncRunQueuedUntilTimeoutThenMarksIdleProviderSessionForRestartOrNudge(string messagesJson, string expectedEvidence)
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await using var server = await FakeOpenCodeServer.StartAsync(messagesJson);
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Provider no handoff")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "OpenCode idle")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var run = JsonNode.Parse((await RunCli(
                workMapStore,
                "cell",
                "session",
                "run",
                "--cell",
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
                "--timeout",
                "1",
                "--async")).Stdout)!;

            Assert.Equal("opencode", run["backend"]!.GetValue<string>());
            Assert.Equal("queued", run["status"]!.GetValue<string>());
            var sessionId = run["sessionID"]!.GetValue<string>();

            var firstSync = JsonNode.Parse((await RunCli(workMapStore, "cell", "session", "sync", "--session", sessionId, "--server", server.Url)).Stdout)!;
            Assert.Equal("queued", firstSync["status"]!.GetValue<string>());
            Assert.Null(firstSync["blocker"]);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var synced = JsonNode.Parse((await RunCli(workMapStore, "cell", "session", "sync", "--session", sessionId, "--server", server.Url)).Stdout)!;

            Assert.Equal("needs-restart-or-nudge", synced["status"]!.GetValue<string>());
            Assert.Null(synced["blocker"]);
            var restartEvent = synced["events"]!.AsArray()
                .Select(item => item!)
                .Last(item => item["type"]!.GetValue<string>() == "restartOrNudgeNeeded");
            Assert.Contains(expectedEvidence, restartEvent["summary"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(workMapStore)) Directory.Delete(workMapStore, true);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task CellSessionRunForwardsCopilotPermissionFlags()
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

            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Copilot flags")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Copilot slice")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            await RunCli(
                workMapStore,
                startInfo =>
                {
                    var path = startInfo.Environment.TryGetValue("PATH", out var existingPath)
                        ? existingPath
                        : Environment.GetEnvironmentVariable("PATH");
                    startInfo.Environment["PATH"] = fakeBin + Path.PathSeparator + path;
                    startInfo.Environment["AEGIS_COPILOT_BINARY"] = OperatingSystem.IsWindows()
                        ? Path.Combine(fakeBin, "fake-copilot.ps1")
                        : Path.Combine(fakeBin, "copilot");
                },
                "cell",
                "session",
                "run",
                "--cell",
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
    public async Task CellSessionRunRejectsCopilotAsyncBeforeCreatingSessionRecord()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workMapStore);
            Directory.CreateDirectory(workspace);
            var mission = JsonNode.Parse((await RunCli(workMapStore, "cell", "create", "--title", "Copilot async")).Stdout)!;
            var missionId = mission["id"]!.GetValue<string>();
            var stream = JsonNode.Parse((await RunCli(workMapStore, "cell", "stream", "add", "--cell", missionId, "--name", "Rejected async")).Stdout)!;
            var streamId = stream["id"]!.GetValue<string>();

            var result = await RunCliAllowFailure(
                workMapStore,
                "cell",
                "session",
                "run",
                "--cell",
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
    public async Task CellServeWritesJsonlAccessLog()
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
                "cell",
                "serve",
                "--host",
                "127.0.0.1",
                "--port",
                port.ToString(),
                "--access-log",
                accessLog);

            await WaitForObserver(port, process);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AegisTest", "1.0"));
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var cellsResponse = await http.GetAsync($"http://127.0.0.1:{port}/api/cells");
            Assert.Equal(HttpStatusCode.OK, cellsResponse.StatusCode);
            var cellsPayload = JsonNode.Parse(await cellsResponse.Content.ReadAsStringAsync())!;
            Assert.NotNull(cellsPayload["cells"]);
            Assert.NotNull(cellsPayload["missions"]);

            var entry = await WaitForAccessLogEntry(accessLog, "/api/health", "AegisTest/1.0");

            Assert.Equal("GET", entry["method"]!.GetValue<string>());
            Assert.Equal("/api/health", entry["path"]!.GetValue<string>());
            Assert.Equal(200, entry["statusCode"]!.GetValue<int>());
            Assert.Equal("AegisTest/1.0", entry["userAgent"]!.GetValue<string>());
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
    public async Task CellHelpDocumentsAccessLogAndTailscaleServe()
    {
        var workMapStore = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var help = await RunCli(workMapStore, "help", "cell");
            var sessionRunHelp = await RunCli(workMapStore, "cell", "session", "run", "--help");

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
            AssertNextActionHint(help, missionId: null);
            AssertNextActionHint(sessionRunHelp, missionId: null);
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

    private static JsonNode AssertNextActionHint(CliResult result, string? missionId = null, string? streamId = null, string? sessionId = null)
    {
        var hint = ReadNextActionHint(result.Stderr);
        Assert.Equal("cell-next-action", hint["kind"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(hint["suggestedNextAction"]!.GetValue<string>()));
        if (missionId is not null) Assert.Equal(missionId, hint["missionID"]!.GetValue<string>());
        if (streamId is not null) Assert.Equal(streamId, hint["streamID"]!.GetValue<string>());
        if (sessionId is not null) Assert.Equal(sessionId, hint["sessionID"]!.GetValue<string>());
        Assert.NotEmpty(hint["nextCommands"]!.AsArray());
        var serialized = hint.ToJsonString();
        Assert.Contains("aegis cell", serialized);
        Assert.Contains("github-copilot/gpt-5.5", serialized);
        Assert.Contains("standalone Copilot CLI backend", serialized);
        Assert.DoesNotContain("opencode run", serialized, StringComparison.OrdinalIgnoreCase);
        return hint;
    }

    private static JsonNode ReadNextActionHint(string stderr)
    {
        foreach (var line in stderr.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            var parsed = JsonNode.Parse(line);
            if (parsed?["kind"]?.GetValue<string>() == "cell-next-action")
            {
                return parsed;
            }
        }

        throw new InvalidOperationException("No cell-next-action payload was written to stderr.");
    }

    private static void AssertInlineNextCommandHints(string text, string missionId, string streamId)
    {
        Assert.Contains("Next useful commands:", text);
        Assert.Contains("aegis cell session run", text);
        Assert.Contains($"--cell {missionId} --stream {streamId}", text);
        Assert.Contains("--model github-copilot/gpt-5.5 --variant high --agent build", text);
        Assert.Contains("aegis ask --model github-copilot/gpt-5.5 --variant high --agent build", text);
        Assert.Contains("--prompt-file \"<brief.md>\" --timeout 900", text);
        Assert.Contains("Use --backend copilot without a github-copilot provider model only for the standalone Copilot CLI backend.", text);
        Assert.Contains("Use --model github-copilot/gpt-5.5 with cell session run or aegis ask for OpenCode sessions using the GitHub Copilot provider.", text);
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

        startInfo.Environment["AEGIS_CELL_DIR"] = workMapStore;
        startInfo.Environment["AEGIS_SESSION_DIR"] = Path.Combine(workMapStore, "session-registry");
        startInfo.Environment["AEGIS_BACKEND_STATE_DIR"] = Path.Combine(workMapStore, "backend-state");
        configureStartInfo?.Invoke(startInfo);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start aegis test process.");
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
            throw new TimeoutException("Timed out waiting for aegis test process.");
        }

        var result = new CliResult(process.ExitCode, await stdout, await stderr);
        if (assertSuccess)
        {
            Assert.True(result.ExitCode == 0, $"aegis failed with exit {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
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

        startInfo.Environment["AEGIS_CELL_DIR"] = workMapStore;
        startInfo.Environment["AEGIS_SESSION_DIR"] = Path.Combine(workMapStore, "session-registry");
        startInfo.Environment["AEGIS_BACKEND_STATE_DIR"] = Path.Combine(workMapStore, "backend-state");
        configureStartInfo?.Invoke(startInfo);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start aegis test process.");
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

    private static void CreateFakeCommand(string fakeBin, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(fakeBin, command + ".cmd"), "@echo off\r\nexit /b 0\r\n");
            return;
        }

        var executablePath = Path.Combine(fakeBin, command);
        File.WriteAllText(executablePath, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
                throw new InvalidOperationException($"cell session run exited before a session record appeared with {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
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

        throw new TimeoutException($"Timed out waiting for a cell session record for mission {missionId}.");
    }

    private static async Task<JsonNode> ReadCellSession(string workMapStore, string sessionId)
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
            throw new TimeoutException("Timed out waiting for aegis test process.");
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
                throw new InvalidOperationException($"cell serve exited early with {process.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
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

        throw new TimeoutException($"Timed out waiting for cell serve on port {port}: {lastException?.Message}");
    }

    private sealed class FakeOpenCodeServer : IAsyncDisposable
    {
        private const string SessionId = "ses_fake_opencode";
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;
        private readonly TaskCompletionSource<JsonNode> _promptBody = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeOpenCodeServer(TcpListener listener, string messagesJson)
        {
            _listener = listener;
            MessagesJson = messagesJson;
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptLoop);
        }

        private string MessagesJson { get; }

        public string Url { get; }

        public static Task<FakeOpenCodeServer> StartAsync(string messagesJson = "[]")
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeOpenCodeServer(listener, messagesJson));
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
                    await WriteResponseAsync(stream, "200 OK", MessagesJson);
                    return;
                }

                if (method == "POST" && path.StartsWith($"/session/{SessionId}/prompt_async", StringComparison.Ordinal))
                {
                    _promptBody.TrySetResult(JsonNode.Parse(body)!);
                    await WriteResponseAsync(stream, "204 No Content", string.Empty);
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
        var cliPath = Path.Combine(AppContext.BaseDirectory, "aegis.dll");
        if (!File.Exists(cliPath))
        {
            cliPath = Path.Combine(LocateRepoRoot(Directory.GetCurrentDirectory()), "src", "Aegis", "bin", "Debug", "net10.0", "aegis.dll");
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
