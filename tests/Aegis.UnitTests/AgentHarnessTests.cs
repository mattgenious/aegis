using Aegis.Backends;
using Aegis.Core;
using Aegis.Infrastructure;
using Xunit;

namespace Aegis.UnitTests;

public sealed class AgentHarnessTests
{
    [Fact]
    public async Task AskAsyncExposesHostFriendlyResult()
    {
        var harness = CreateHarness(out var tempDir);
        try
        {
            var result = await harness.AskAsync(new AgentRunRequest
            {
                Prompt = "Do work.",
                Title = "baton-worker"
            });

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Summary);
            Assert.Equal("baton-friendly summary", result.Summary!.Text);
            Assert.Equal(BackendKind.Pi, result.Session.Backend);

            var messages = await harness.GetMessagesAsync(result.Session.SessionId);
            Assert.Equal(2, messages.Count);

            var summary = await harness.GetLastSummaryAsync(result.Session.SessionId);
            Assert.NotNull(summary);
            Assert.Equal("baton-friendly summary", summary!.Text);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AskAsyncExposesBackendFailureWithoutTransportSpecificTypes()
    {
        var harness = CreateHarness(out var tempDir, failPrompt: true);
        try
        {
            var result = await harness.AskAsync(new AgentRunRequest { Prompt = "Fail work." });

            Assert.False(result.IsSuccess);
            Assert.Equal(9, result.ExitCode);
            Assert.Equal("worker failed", result.Message);
            Assert.Null(result.Summary);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static IAgentHarness CreateHarness(out string tempDir, bool failPrompt = false)
    {
        tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var registry = new SessionRegistryService(new FileSessionRegistry(new TempRegistryPathProvider(tempDir)));
        var commands = new BackendCommandService(new FakeSessionBackend(failPrompt), registry);
        return new AgentHarness(commands);
    }

    private sealed class TempRegistryPathProvider(string directoryPath) : ISessionRegistryPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }

    private sealed class FakeSessionBackend : ISessionBackend
    {
        private readonly bool _failPrompt;
        private readonly List<BackendMessage> _messages = [];

        public FakeSessionBackend(bool failPrompt)
        {
            _failPrompt = failPrompt;
        }

        public BackendKind Kind => BackendKind.Pi;

        public Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            var id = "fake-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(new SessionRecord(
                id,
                Kind,
                id,
                DateTimeOffset.UtcNow,
                request.Directory));
        }

        public Task<CommandResult> PostPromptAsync(SessionRecord session, PromptRequest request, CancellationToken cancellationToken = default)
        {
            if (_failPrompt)
            {
                return Task.FromResult(CommandResult.Failure(9, "worker failed"));
            }

            _messages.Add(new BackendMessage("u1", "user", request.Text));
            _messages.Add(new BackendMessage("a1", "assistant", "FINAL HANDOFF\nbaton-friendly summary", "p1"));
            return Task.FromResult(CommandResult.Success());
        }

        public Task<SessionStateSnapshot> GetSessionStateAsync(SessionRecord session, int anchorMessageIndex = -1, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionStateNormalizer.Normalize(
                session.SessionId,
                session.BackendSessionId,
                "idle",
                _messages.Count,
                "u1",
                "a1",
                hasAssistantAfterAnchor: true,
                hasFreshSummary: true));
        }

        public Task<IReadOnlyList<BackendMessage>> GetMessagesAsync(SessionRecord session, int limit = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackendMessage>>(_messages);

        public async IAsyncEnumerable<BackendMessage> WatchMessagesAsync(
            SessionRecord session,
            int limit = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in _messages)
            {
                yield return message;
            }

            await Task.CompletedTask;
        }

        public Task<SummaryResult?> ExtractSummaryAsync(SessionRecord session, string marker, int anchorMessageIndex = -1, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SummaryResult?>(new SummaryResult(
                session.SessionId,
                "a1",
                "p1",
                "baton-friendly summary"));
        }

        public Task<CommandResult> AbortAsync(SessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Success("aborted"));

        public Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Success("removed"));
    }
}
