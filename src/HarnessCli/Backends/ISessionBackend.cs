using System.Collections.Generic;
using HarnessCli.Core;

namespace HarnessCli.Backends;

public interface ISessionBackend
{
    BackendKind Kind { get; }

    Task<SessionRecord> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);

    Task<CommandResult> PostPromptAsync(
        SessionRecord session,
        PromptRequest request,
        CancellationToken cancellationToken = default);

    Task<SessionStateSnapshot> GetSessionStateAsync(
        SessionRecord session,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendMessage>> GetMessagesAsync(
        SessionRecord session,
        int limit = 0,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<BackendMessage> WatchMessagesAsync(
        SessionRecord session,
        int limit = 0,
        CancellationToken cancellationToken = default);

    Task<SummaryResult?> ExtractSummaryAsync(
        SessionRecord session,
        string marker,
        int anchorMessageIndex = -1,
        CancellationToken cancellationToken = default);

    Task<CommandResult> AbortAsync(SessionRecord session, CancellationToken cancellationToken = default);

    Task<CommandResult> TeardownAsync(SessionRecord session, CancellationToken cancellationToken = default);
}

