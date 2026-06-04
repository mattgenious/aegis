using Aegis.Core;
using Aegis.Infrastructure;

namespace Aegis.Backends;

public sealed class BackendCommandService
{
    private readonly ISessionBackend _backend;
    private readonly SessionRegistryService _registry;

    public BackendCommandService(ISessionBackend backend, SessionRegistryService registry)
    {
        _backend = backend;
        _registry = registry;
    }

    public async Task<SessionRecord> CreateSessionAsync(CreateBackendSessionRequest request)
    {
        var created = await _backend.CreateSessionAsync(new CreateSessionRequest(
            request.Title,
            request.ParentSessionId,
            request.Directory));

        var metadata = created.Metadata;
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            metadata = metadata.SetItem("title", request.Title);
        }

        if (!string.IsNullOrWhiteSpace(request.ParentSessionId))
        {
            metadata = metadata.SetItem("parent", request.ParentSessionId);
        }

        return await _registry.CreateAndStoreAsync(
            _backend.Kind,
            created.BackendSessionId,
            created.CreatedAtUtc,
            created.Directory,
            created.BackendMetadataPath,
            metadata);
    }

    public async Task<IReadOnlyList<SessionRecord>> GetLatestSessionsAsync(BackendLatestSessionsRequest request)
    {
        var sessions = await _registry.GetForBackendAsync(_backend.Kind);
        var filtered = sessions.Where(session =>
                string.IsNullOrWhiteSpace(request.Search)
                || session.SessionId.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
                || session.Metadata.TryGetValue("title", out var title)
                    && !string.IsNullOrWhiteSpace(title)
                    && title.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.CreatedAtUtc)
            .Take(request.Limit > 0 ? request.Limit : 20)
            .ToArray();

        return filtered;
    }

    public async Task<IReadOnlyList<BackendMessage>> GetMessagesAsync(string sessionId, int limit)
    {
        var session = await ResolveSessionAsync(sessionId);
        return await _backend.GetMessagesAsync(session, limit);
    }

    public async Task<SessionStateSnapshot> WaitUntilIdleAsync(string sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        while (true)
        {
            var state = await _backend.GetSessionStateAsync(session);
            if (state.ApiStatus is null || state.ApiStatus.StartsWith("idle", StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }

            if (state.ApiStatus.StartsWith("error:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(state.ApiStatus);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    public async Task<BackendAbortResult> AbortAsync(string sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        var result = await _backend.AbortAsync(session);
        return new BackendAbortResult(session, result);
    }

    public async Task<BackendAskResult> AskAsync(BackendAskRequest request)
    {
        var (session, created) = await GetOrCreateSessionAsync(request);
        if (created && request.SessionCreated is not null)
        {
            await request.SessionCreated(session);
        }

        var anchorIndex = await GetLatestUserMessageIndexAsync(session);
        var prompt = request.Async
            ? request.Prompt with
            {
                Options = request.Prompt.Options.SetItem("harness.async", "true")
            }
            : request.Prompt;
        var postResult = await _backend.PostPromptAsync(session, prompt);
        if (!postResult.IsSuccess)
        {
            return new BackendAskResult(session, null, postResult);
        }

        await _registry.TouchAsync(session.SessionId);

        SummaryResult? summary = null;
        if (!request.Prompt.NoReply && (!request.Async || request.Wait))
        {
            await WaitForCompletionAsync(session, anchorIndex, request.Timeout);
            summary = await _backend.ExtractSummaryAsync(session, request.Prompt.SummaryMarker, anchorIndex);
        }

        return new BackendAskResult(session, summary, postResult);
    }

    public async Task<IReadOnlyList<SessionStateSnapshot>> GetStatusAsync(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await ResolveSessionAsync(sessionId);
            return
            [
                await _backend.GetSessionStateAsync(
                    session,
                    await GetLatestUserMessageIndexAsync(session))
            ];
        }

        var sessions = await _registry.GetForBackendAsync(_backend.Kind);
        var states = new List<SessionStateSnapshot>();
        foreach (var session in sessions)
        {
            states.Add(await _backend.GetSessionStateAsync(session, anchorMessageIndex: -1));
        }

        return states;
    }

    public async Task<SummaryResult?> GetLastSummaryAsync(string sessionId, string marker)
    {
        var session = await ResolveSessionAsync(sessionId);
        var anchorIndex = await GetLatestUserMessageIndexAsync(session);
        return await _backend.ExtractSummaryAsync(session, marker, anchorIndex);
    }

    private async Task<(SessionRecord Session, bool Created)> GetOrCreateSessionAsync(BackendAskRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            return (await ResolveSessionAsync(request.SessionId), false);
        }

        var created = await CreateSessionAsync(new CreateBackendSessionRequest(
            request.Title,
            request.ParentSessionId,
            request.Directory));
        return (created, true);
    }

    private async Task<SessionRecord> ResolveSessionAsync(string sessionId)
    {
        var session = await _registry.RequireAsync(sessionId);
        if (session.Backend != _backend.Kind)
        {
            throw new InvalidOperationException($"Session {session.SessionId} belongs to '{session.Backend.ToOptionValue()}' but backend '{_backend.Kind.ToOptionValue()}' was requested.");
        }

        return session;
    }

    private async Task<int> GetLatestUserMessageIndexAsync(SessionRecord session)
    {
        var messages = await _backend.GetMessagesAsync(session);
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private async Task WaitForCompletionAsync(SessionRecord session, int anchorMessageIndex, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var checkDelay = TimeSpan.FromMilliseconds(500);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await _backend.GetSessionStateAsync(session, anchorMessageIndex);
            if (snapshot.ApiStatus is not null && snapshot.ApiStatus.StartsWith("error:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(snapshot.ApiStatus);
            }

            if (snapshot.HasFreshSummary)
            {
                return;
            }

            await Task.Delay(checkDelay);
        }

        throw new TimeoutException($"Session {session.SessionId} did not produce a fresh final handoff within {timeout.TotalSeconds:N0}s.");
    }
}

public sealed record CreateBackendSessionRequest(
    string? Title,
    string? ParentSessionId,
    string? Directory);

public sealed record BackendLatestSessionsRequest(string? Search, int Limit);

public sealed record BackendAskRequest(
    string? SessionId,
    string? Title,
    string? ParentSessionId,
    string? Directory,
    PromptRequest Prompt,
    bool Async,
    bool Wait,
    TimeSpan Timeout,
    Func<SessionRecord, Task>? SessionCreated = null);

public sealed record BackendAskResult(SessionRecord Session, SummaryResult? Summary, CommandResult PostResult);

public sealed record BackendAbortResult(SessionRecord Session, CommandResult Result);
