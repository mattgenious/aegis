using System.Collections.Immutable;
using Xunit;
using HarnessCli.Core;
using HarnessCli.Infrastructure;

namespace HarnessCli.UnitTests;

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
        Assert.False(BackendKindExtensions.TryParse("unknown", out _));
        Assert.False(BackendKindExtensions.TryParse(" ", out _));
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

    private sealed class TempRegistryPathProvider(string directoryPath) : ISessionRegistryPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }
}
