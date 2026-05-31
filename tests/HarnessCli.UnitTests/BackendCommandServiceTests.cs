using HarnessCli.Backends;
using HarnessCli.Core;
using HarnessCli.Infrastructure;
using Xunit;

namespace HarnessCli.UnitTests;

public sealed class BackendCommandServiceTests
{
    [Fact]
    public async Task AskAsyncReturnsFreshSummaryFromBackend()
    {
        var service = CreateService(out var tempDir);
        try
        {
            var result = await service.AskAsync(new BackendAskRequest(
                SessionId: null,
                Title: "service-smoke",
                ParentSessionId: null,
                Directory: tempDir,
                Prompt: new PromptRequest(
                    Text: "Do work.",
                    SourceKind: PromptSourceKind.Inline,
                    SourceLocation: null,
                    SummaryMarker: "FINAL HANDOFF"),
                Async: false,
                Wait: false,
                Timeout: TimeSpan.FromSeconds(5)));

            Assert.True(result.PostResult.IsSuccess);
            Assert.NotNull(result.Summary);
            Assert.Equal("fake backend completed", result.Summary!.Text);
            Assert.StartsWith("codex-", result.Session.SessionId, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AskAsyncReturnsBackendFailureWithoutWaitingForSummary()
    {
        var service = CreateService(out var tempDir, failPrompt: true);
        try
        {
            var result = await service.AskAsync(new BackendAskRequest(
                SessionId: null,
                Title: "service-failure",
                ParentSessionId: null,
                Directory: tempDir,
                Prompt: new PromptRequest(
                    Text: "Fail work.",
                    SourceKind: PromptSourceKind.Inline,
                    SourceLocation: null),
                Async: false,
                Wait: false,
                Timeout: TimeSpan.FromSeconds(5)));

            Assert.False(result.PostResult.IsSuccess);
            Assert.Null(result.Summary);
            Assert.Equal("fake prompt failed", result.PostResult.Message);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AskAsyncMarksBackendPromptAsDetachedWhenAsyncWithoutWait()
    {
        var service = CreateService(out var tempDir, out var backend);
        try
        {
            var result = await service.AskAsync(new BackendAskRequest(
                SessionId: null,
                Title: "service-async",
                ParentSessionId: null,
                Directory: tempDir,
                Prompt: new PromptRequest(
                    Text: "Do work in the background.",
                    SourceKind: PromptSourceKind.Inline,
                    SourceLocation: null),
                Async: true,
                Wait: false,
                Timeout: TimeSpan.FromSeconds(5)));

            Assert.True(result.PostResult.IsSuccess);
            Assert.Null(result.Summary);
            Assert.NotNull(backend.LastPrompt);
            Assert.Equal("true", backend.LastPrompt!.Options["harness.async"]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static BackendCommandService CreateService(out string tempDir, bool failPrompt = false)
    {
        return CreateService(out tempDir, out _, failPrompt);
    }

    private static BackendCommandService CreateService(
        out string tempDir,
        out FakeSessionBackend backend,
        bool failPrompt = false)
    {
        tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var registry = new SessionRegistryService(new FileSessionRegistry(new TempRegistryPathProvider(tempDir)));
        backend = new FakeSessionBackend(failPrompt);
        return new BackendCommandService(backend, registry);
    }

    private sealed class TempRegistryPathProvider(string directoryPath) : ISessionRegistryPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }

    private sealed class FakeSessionBackend : ISessionBackend
    {
        private readonly bool _failPrompt;
        private readonly List<BackendMessage> _messages = [];

        public PromptRequest? LastPrompt { get; private set; }

        public FakeSessionBackend(bool failPrompt)
        {
            _failPrompt = failPrompt;
        }

        public BackendKind Kind => BackendKind.Codex;

        public Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            var id = "fake-" + Guid.NewGuid().ToString("N");
            return Task.FromResult(new SessionRecord(
                SessionId: id,
                Backend: Kind,
                BackendSessionId: id,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                Directory: request.Directory));
        }

        public Task<CommandResult> PostPromptAsync(SessionRecord session, PromptRequest request, CancellationToken cancellationToken = default)
        {
            LastPrompt = request;
            if (_failPrompt)
            {
                return Task.FromResult(CommandResult.Failure(2, "fake prompt failed"));
            }

            _messages.Add(new BackendMessage("user-1", "user", request.Text));
            if (request.Options.ContainsKey("harness.async"))
            {
                return Task.FromResult(CommandResult.Success());
            }

            _messages.Add(new BackendMessage("assistant-1", "assistant", "FINAL HANDOFF\nfake backend completed", "part-1"));
            return Task.FromResult(CommandResult.Success());
        }

        public Task<SessionStateSnapshot> GetSessionStateAsync(
            SessionRecord session,
            int anchorMessageIndex = -1,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionStateNormalizer.Normalize(
                session.SessionId,
                session.BackendSessionId,
                "idle",
                _messages.Count,
                "user-1",
                "assistant-1",
                hasAssistantAfterAnchor: true,
                hasFreshSummary: true));
        }

        public Task<IReadOnlyList<BackendMessage>> GetMessagesAsync(
            SessionRecord session,
            int limit = 0,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<BackendMessage>>(_messages);
        }

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

        public Task<SummaryResult?> ExtractSummaryAsync(
            SessionRecord session,
            string marker,
            int anchorMessageIndex = -1,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SummaryResult?>(new SummaryResult(
                session.SessionId,
                "assistant-1",
                "part-1",
                "fake backend completed"));
        }

        public Task<CommandResult> AbortAsync(SessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Success("aborted"));

        public Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Success("removed"));
    }
}
