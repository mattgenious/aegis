using Aegis.Core;

namespace Aegis.Backends;

public sealed class AgentHarness : IAgentHarness
{
    private readonly BackendCommandService _commands;

    public AgentHarness(BackendCommandService commands)
    {
        _commands = commands;
    }

    public async Task<AgentSession> CreateSessionAsync(
        CreateAgentSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await _commands.CreateSessionAsync(new CreateBackendSessionRequest(
            request.Title,
            request.ParentSessionId,
            request.Directory));
        return AgentSession.FromSessionRecord(session);
    }

    public async Task<AgentRunResult> AskAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = new PromptRequest(
            Text: request.Prompt,
            SourceKind: request.SourceKind,
            SourceLocation: request.SourceLocation,
            ModelProvider: request.ModelProvider,
            Model: request.Model,
            Variant: request.Variant,
            SummaryMarker: request.SummaryMarker,
            Directory: request.Directory,
            Agent: request.Agent,
            System: request.System,
            NoReply: request.NoReply,
            Raw: request.Raw);

        var result = await _commands.AskAsync(new BackendAskRequest(
            request.SessionId,
            request.Title,
            request.ParentSessionId,
            request.Directory,
            prompt,
            request.Async,
            request.Wait,
            request.Timeout));

        return new AgentRunResult(
            AgentSession.FromSessionRecord(result.Session),
            result.PostResult.IsSuccess,
            result.PostResult.ExitCode,
            result.PostResult.Message,
            result.PostResult.Error,
            result.Summary is null ? null : AgentSummary.FromSummaryResult(result.Summary));
    }

    public async Task<IReadOnlyList<AgentSessionState>> GetStatusAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var states = await _commands.GetStatusAsync(sessionId);
        return states.Select(AgentSessionState.FromSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
        string sessionId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var messages = await _commands.GetMessagesAsync(sessionId, limit);
        return messages.Select(AgentMessage.FromBackendMessage).ToArray();
    }

    public async Task<AgentSummary?> GetLastSummaryAsync(
        string sessionId,
        string marker = "FINAL HANDOFF",
        CancellationToken cancellationToken = default)
    {
        var summary = await _commands.GetLastSummaryAsync(sessionId, marker);
        return summary is null ? null : AgentSummary.FromSummaryResult(summary);
    }

    public async Task<AgentAbortResult> AbortAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.AbortAsync(sessionId);
        return new AgentAbortResult(
            AgentSession.FromSessionRecord(result.Session),
            result.Result.IsSuccess,
            result.Result.ExitCode,
            result.Result.Message,
            result.Result.Error);
    }
}
