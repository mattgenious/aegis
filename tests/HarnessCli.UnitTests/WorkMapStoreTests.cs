using System.Text.Json;
using HarnessCli.Core;
using HarnessCli.Infrastructure;
using Xunit;

namespace HarnessCli.UnitTests;

public sealed class WorkMapStoreTests
{
    [Fact]
    public async Task FileWorkMapStorePersistsMissionGraphOutsideTargetClone()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cloneRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(cloneRoot);
            var store = new FileWorkMapStore(new TempWorkMapPathProvider(tempRoot));

            var mission = new WorkMapMissionRecord
            {
                Id = "mission-test",
                Title = "Coordinate agent work",
                Intent = "Keep parallel work inspectable."
            };
            var stream = new WorkMapWorkstreamRecord
            {
                Id = "stream-skill",
                MissionId = mission.Id,
                Name = "Skill rewrite",
                ClonePath = cloneRoot,
                SessionIds = ["codex-1"]
            };
            var session = new WorkMapAgentSessionRecord
            {
                Id = "codex-1",
                MissionId = mission.Id,
                WorkstreamId = stream.Id,
                Backend = "codex",
                Status = "handoff",
                FinalHandoff = new WorkMapHandoffRecord { Text = "Done." }
            };

            await store.SaveMissionAsync(mission);
            await store.SaveWorkstreamAsync(stream);
            await store.SaveAgentSessionAsync(session);

            Assert.Equal("Coordinate agent work", (await store.GetMissionAsync(mission.Id))!.Title);
            Assert.Single(await store.GetWorkstreamsAsync(mission.Id));
            Assert.Single(await store.GetAgentSessionsAsync(mission.Id));
            Assert.False(Directory.Exists(Path.Combine(cloneRoot, ".harness-cli")));
            Assert.False(File.Exists(Path.Combine(cloneRoot, "mission-test.json")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            if (Directory.Exists(cloneRoot)) Directory.Delete(cloneRoot, true);
        }
    }

    [Fact]
    public async Task FileWorkMapStoreToleratesUnknownJsonFieldsAndMissingOptionalValues()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var missions = Path.Combine(tempRoot, "missions");
            Directory.CreateDirectory(missions);
            await File.WriteAllTextAsync(
                Path.Combine(missions, "mission-loose.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    kind = "mission",
                    id = "mission-loose",
                    title = "Loose schema",
                    futureField = "ignored"
                }));

            var store = new FileWorkMapStore(new TempWorkMapPathProvider(tempRoot));
            var loaded = await store.GetMissionAsync("mission-loose");

            Assert.NotNull(loaded);
            Assert.Equal("Loose schema", loaded!.Title);
            Assert.Empty(loaded.WorkstreamIds);
            Assert.Equal("planned", loaded.Status);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task FileWorkMapStoreSerializesConcurrentMissionMutationsAcrossStoreInstances()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var store = new FileWorkMapStore(new TempWorkMapPathProvider(tempRoot));
            await store.SaveMissionAsync(new WorkMapMissionRecord
            {
                Id = "mission-concurrent",
                Title = "Concurrent mission"
            });

            var tasks = Enumerable.Range(0, 24).Select(index =>
            {
                var workerStore = new FileWorkMapStore(new TempWorkMapPathProvider(tempRoot));
                return workerStore.UpdateMissionAsync(
                    "mission-concurrent",
                    mission =>
                    {
                        var evidence = mission.Evidence.ToList();
                        evidence.Add(new WorkMapEvidenceRecord
                        {
                            Id = $"evidence-{index:D2}",
                            Kind = "note",
                            Summary = $"evidence {index}"
                        });

                        var streams = mission.WorkstreamIds.ToList();
                        streams.Add($"stream-{index:D2}");

                        return mission with
                        {
                            Evidence = evidence,
                            WorkstreamIds = streams,
                            UpdatedAtUtc = DateTimeOffset.UtcNow
                        };
                    });
            });

            await Task.WhenAll(tasks);

            var loaded = await store.GetMissionAsync("mission-concurrent");
            Assert.NotNull(loaded);
            Assert.Equal(24, loaded!.Evidence.Count);
            Assert.Equal(24, loaded.WorkstreamIds.Count);
            Assert.Equal(24, loaded.Evidence.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task FileWorkMapStorePersistsProviderNeutralSessionHistory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var store = new FileWorkMapStore(new TempWorkMapPathProvider(tempRoot));
            var session = new WorkMapAgentSessionRecord
            {
                Id = "codex-history",
                MissionId = "mission-history",
                WorkstreamId = "stream-history",
                Backend = "codex",
                Messages =
                [
                    new WorkMapMessageRecord
                    {
                        Id = "assistant-1",
                        Role = "assistant",
                        Text = "FINAL HANDOFF\nDone.",
                        PartId = "part-1",
                        Sequence = 0
                    }
                ],
                StatusObservations =
                [
                    new WorkMapStatusObservationRecord
                    {
                        EffectiveStatus = "idle",
                        DerivedStatus = "idle",
                        MessageCount = 1,
                        LatestAssistantMessageId = "assistant-1",
                        HasFreshSummary = true
                    }
                ]
            };

            await store.SaveAgentSessionAsync(session);
            var loaded = await store.GetAgentSessionAsync(session.Id);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Messages);
            Assert.Single(loaded.StatusObservations);
            Assert.True(loaded.StatusObservations[0].HasFreshSummary);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private sealed class TempWorkMapPathProvider(string directoryPath) : IWorkMapPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }
}
