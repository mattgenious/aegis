namespace Aegis.Backends;

public interface IAgentHarness
{
    Task<AgentSession> CreateSessionAsync(CreateAgentSessionRequest request, CancellationToken cancellationToken = default);

    Task<AgentRunResult> AskAsync(AgentRunRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentSessionState>> GetStatusAsync(string? sessionId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(string sessionId, int limit = 20, CancellationToken cancellationToken = default);

    Task<AgentSummary?> GetLastSummaryAsync(string sessionId, string marker = "FINAL HANDOFF", CancellationToken cancellationToken = default);

    Task<AgentAbortResult> AbortAsync(string sessionId, CancellationToken cancellationToken = default);
}
