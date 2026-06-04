using System.Collections.Immutable;
using System.Text.Json;
using Aegis.Backends;
using Aegis.Core;
using Aegis.Infrastructure;
using Xunit;

namespace Aegis.UnitTests;

public class DomainContractsTests
{
    [Fact]
    public void BackendKindCanParseKnownValues()
    {
        Assert.True(BackendKindExtensions.TryParse("opencode", out var opencode));
        Assert.Equal(BackendKind.Opencode, opencode);
        Assert.True(BackendKindExtensions.TryParse("codex", out var codex));
        Assert.Equal(BackendKind.Codex, codex);
        Assert.True(BackendKindExtensions.TryParse("pi.dev", out var pi));
        Assert.Equal(BackendKind.Pi, pi);
        Assert.True(BackendKindExtensions.TryParse(" Copilot ", out var copilotWithWhitespace));
        Assert.Equal(BackendKind.Copilot, copilotWithWhitespace);
        Assert.True(BackendKindExtensions.TryParse("github-copilot", out var copilot));
        Assert.Equal(BackendKind.Copilot, copilot);
        Assert.False(BackendKindExtensions.TryParse("unknown", out _));
        Assert.False(BackendKindExtensions.TryParse(" ", out _));
    }

    [Fact]
    public void BackendKindCanRenderOptionValues()
    {
        Assert.Equal("opencode", BackendKind.Opencode.ToOptionValue());
        Assert.Equal("codex", BackendKind.Codex.ToOptionValue());
        Assert.Equal("pi", BackendKind.Pi.ToOptionValue());
        Assert.Equal("copilot", BackendKind.Copilot.ToOptionValue());
    }

    [Fact]
    public void PromptAndSessionContractsCaptureCanonicalMetadata()
    {
        var prompt = new PromptRequest(
            Text: "Ship this issue.",
            SourceKind: PromptSourceKind.Inline,
            SourceLocation: null,
            ModelProvider: "github-copilot",
            Model: "gpt-5.5",
            Variant: "high");

        Assert.Equal("Ship this issue.", prompt.Text);
        Assert.Equal(PromptSourceKind.Inline, prompt.SourceKind);
        Assert.Equal("github-copilot", prompt.ModelProvider);
        Assert.Equal("gpt-5.5", prompt.Model);

        var session = new SessionRecord(
            SessionId: "session-1",
            Backend: BackendKind.Opencode,
            BackendSessionId: "opencode-1",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Metadata: ImmutableDictionary<string, string>.Empty);

        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("opencode-1", session.BackendSessionId);
        Assert.NotNull(session.Metadata);
        Assert.Empty(session.Metadata);
    }

    [Fact]
    public void SessionStateNormalizerMapsApiAndDerivedStatus()
    {
        var state = SessionStateNormalizer.Normalize(
            sessionId: "ses-1",
            backendSessionId: "backend-ses",
            apiStatus: null,
            messageCount: 3,
            latestUserMessageId: "u-1",
            latestAssistantMessageId: "a-1",
            hasAssistantAfterAnchor: true,
            hasFreshSummary: false);

        Assert.Equal("ses-1", state.SessionId);
        Assert.Equal("backend-ses", state.BackendSessionId);
        Assert.Equal("idle", state.EffectiveStatus);
        Assert.Equal("assistant-after-latest-user-without-handoff", state.DerivedStatus);
        Assert.False(state.HasFreshSummary);
    }

    [Fact]
    public async Task FileSessionRegistryCanStoreAndReturnSessions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var provider = new TempRegistryPathProvider(tempDir);
            var registry = new FileSessionRegistry(provider);
            var session = new SessionRecord(
                SessionId: "ses-abcd",
                Backend: BackendKind.Codex,
                BackendSessionId: "codex-uuid",
                CreatedAtUtc: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Directory: @"C:\repo",
                Metadata: ImmutableDictionary.CreateRange(new[]
                {
                    KeyValuePair.Create("owner", "team")
                }));

            await registry.AddOrUpdateAsync(session);
            var loaded = await registry.GetAsync("ses-abcd");
            var all = await registry.GetAllAsync();

            Assert.NotNull(loaded);
            Assert.Equal(session.SessionId, loaded!.SessionId);
            Assert.Equal(session.BackendSessionId, loaded.BackendSessionId);
            Assert.Equal("team", loaded.Metadata["owner"]);
            Assert.Single(all);
            Assert.Equal(session.SessionId, all[0].SessionId);

            var deleted = await registry.DeleteAsync("ses-abcd");
            Assert.True(deleted);
            Assert.Null(await registry.GetAsync("ses-abcd"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task FileSessionRegistrySupportsBackendFilteringAndCleanup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var provider = new TempRegistryPathProvider(tempDir);
            var registry = new FileSessionRegistry(provider);
            var oldSession = new SessionRecord(
                SessionId: "ses-old",
                Backend: BackendKind.Pi,
                BackendSessionId: "pi-old",
                CreatedAtUtc: DateTimeOffset.UtcNow.AddHours(-10),
                Metadata: ImmutableDictionary<string, string>.Empty)
            { LastUpdatedUtc = DateTimeOffset.UtcNow.AddHours(-10) };
            var recentSession = new SessionRecord(
                SessionId: "ses-recent",
                Backend: BackendKind.Pi,
                BackendSessionId: "pi-recent",
                CreatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
                Metadata: ImmutableDictionary<string, string>.Empty)
            { LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-10) };
            var codexSession = new SessionRecord(
                SessionId: "ses-codex",
                Backend: BackendKind.Codex,
                BackendSessionId: "c-1",
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Metadata: ImmutableDictionary<string, string>.Empty);

            await registry.AddOrUpdateAsync(oldSession);
            await registry.AddOrUpdateAsync(recentSession);
            await registry.AddOrUpdateAsync(codexSession);

            var piSessions = await registry.GetByBackendAsync(BackendKind.Pi);
            Assert.Equal(2, piSessions.Count);
            var codexSessions = await registry.GetByBackendAsync(BackendKind.Codex);
            Assert.Single(codexSessions);

            var removed = await registry.RemoveExpiredAsync(TimeSpan.FromHours(1));
            Assert.Equal(1, removed);
            Assert.Null(await registry.GetAsync("ses-old"));
            Assert.NotNull(await registry.GetAsync("ses-recent"));
            Assert.Equal(2, (await registry.GetAllAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SessionRegistryServiceCanResolveKnownSessionOrFailWithActionableError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var provider = new TempRegistryPathProvider(tempDir);
            var registry = new FileSessionRegistry(provider);
            var service = new SessionRegistryService(registry);
            var created = await service.CreateAndStoreAsync(
                backend: BackendKind.Opencode,
                backendSessionId: "op-123",
                metadata: ImmutableDictionary.CreateRange(new[]
                {
                    KeyValuePair.Create("request", "seed")
                }));

            var loaded = await service.RequireAsync(created.SessionId);
            Assert.Equal(created.SessionId, loaded.SessionId);

            var ex = await Assert.ThrowsAsync<UnknownSessionException>(() => service.RequireAsync("missing-session"));
            Assert.Equal("missing-session", ex.SessionId);
            Assert.Contains("Use a valid session", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task CodexBackendCanCreateSessionAndParseSummaryFromPersistedHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var backend = new CodexBackend(stateRoot);
            var request = new CreateSessionRequest(
                Title: "backend-session",
                ParentSessionId: null,
                Directory: tempDir);
            var session = await backend.CreateSessionAsync(request);

            var statusFile = session.BackendMetadataPath + ".status.json";
            var messagesFile = session.BackendMetadataPath + ".messages.jsonl";

            Assert.True(File.Exists(statusFile));
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".aegis")));
            Assert.Equal(BackendKind.Codex, session.Backend);

            var rawMessages = new[]
            {
                new
                {
                    Id = "msg_1",
                    Role = "assistant",
                    Text = "Task started\nFINAL HANDOFF\nImplemented requested logic.",
                    PartId = "part_1",
                    Timestamp = "2026-01-01T12:00:00+00:00"
                }
            };
            await File.WriteAllTextAsync(messagesFile, JsonSerializer.Serialize(rawMessages));

            var state = await backend.GetSessionStateAsync(session);
            Assert.Equal("idle", state.EffectiveStatus);
            Assert.True(state.HasFreshSummary);

            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF");
            Assert.NotNull(summary);
            Assert.Equal("msg_1", summary!.MessageId);
            Assert.Equal("part_1", summary.PartId);
            Assert.Equal("Implemented requested logic.", summary.Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, true);
            }
        }
    }

    [Fact]
    public async Task SummaryExtractionRespectsAnchorInPersistedHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var backend = new CodexBackend(stateRoot);
            var request = new CreateSessionRequest("anchor-test", null, tempDir);
            var session = await backend.CreateSessionAsync(request);

            var messagesFile = session.BackendMetadataPath + ".messages.jsonl";
            var rawMessages = new[]
            {
                new
                {
                    Id = "msg_1",
                    Role = "user",
                    Text = "first prompt",
                    PartId = "part_1",
                    Timestamp = "2026-01-01T12:00:00+00:00"
                },
                new
                {
                    Id = "msg_2",
                    Role = "assistant",
                    Text = "Task started\nFINAL HANDOFF\nold summary",
                    PartId = "part_2",
                    Timestamp = "2026-01-01T12:00:01+00:00"
                },
                new
                {
                    Id = "msg_3",
                    Role = "user",
                    Text = "new prompt after summary",
                    PartId = "part_3",
                    Timestamp = "2026-01-01T12:00:02+00:00"
                },
                new
                {
                    Id = "msg_4",
                    Role = "assistant",
                    Text = "Working on the new prompt without handoff yet",
                    PartId = "part_4",
                    Timestamp = "2026-01-01T12:00:03+00:00"
                }
            };
            await File.WriteAllTextAsync(messagesFile, JsonSerializer.Serialize(rawMessages));

            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF", anchorMessageIndex: 2);
            Assert.Null(summary);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, true);
            }
        }
    }

    [Fact]
    public async Task PiBackendCanCreateSessionAndParseSummaryFromPersistedHistory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var backend = new PiBackend(stateRoot: stateRoot);
            var request = new CreateSessionRequest(
                Title: "pi-backend-session",
                ParentSessionId: null,
                Directory: tempDir);
            var session = await backend.CreateSessionAsync(request);

            var statusFile = session.BackendMetadataPath + ".status.json";
            var messagesFile = session.BackendMetadataPath + ".messages.jsonl";

            Assert.True(File.Exists(statusFile));
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".aegis")));
            Assert.Equal(BackendKind.Pi, session.Backend);

            var rawMessages = new[]
            {
                new
                {
                    Id = "pi_msg_1",
                    Role = "assistant",
                    Text = "Task started\nFINAL HANDOFF\nImplemented requested logic.",
                    PartId = "part_pi_1",
                    Timestamp = "2026-01-01T12:00:00+00:00"
                }
            };
            await File.WriteAllTextAsync(messagesFile, JsonSerializer.Serialize(rawMessages));

            var state = await backend.GetSessionStateAsync(session);
            Assert.Equal("idle", state.EffectiveStatus);
            Assert.True(state.HasFreshSummary);

            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF");
            Assert.NotNull(summary);
            Assert.Equal("pi_msg_1", summary!.MessageId);
            Assert.Equal("part_pi_1", summary.PartId);
            Assert.Equal("Implemented requested logic.", summary.Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, true);
            }
        }
    }

    [Fact]
    public async Task PiBackendPostPromptWithMissingBinaryReturnsGuidance()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var backend = new PiBackend("/no/such/pi-binary", stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("pi-session", null, tempDir));
            Directory.Delete(Path.GetDirectoryName(session.BackendMetadataPath)!, true);
            var request = new PromptRequest(
                Text: "Run a quick check",
                SourceKind: PromptSourceKind.Inline,
                SourceLocation: null);

            var result = await backend.PostPromptAsync(session, request);
            Assert.False(result.IsSuccess);
            Assert.Contains("executable", result.Message);
            Assert.Equal(127, result.ExitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }

            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, true);
            }
        }
    }

    private sealed class TempRegistryPathProvider(string directoryPath) : ISessionRegistryPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }
}
