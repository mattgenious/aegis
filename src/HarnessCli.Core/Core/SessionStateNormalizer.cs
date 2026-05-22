namespace HarnessCli.Core;

public static class SessionStateNormalizer
{
    public static SessionStateSnapshot Normalize(
        string sessionId,
        string backendSessionId,
        string? apiStatus,
        int messageCount,
        string? latestUserMessageId,
        string? latestAssistantMessageId,
        bool hasAssistantAfterAnchor,
        bool hasFreshSummary)
    {
        var effectiveStatus = string.IsNullOrWhiteSpace(apiStatus) ? "idle" : apiStatus;
        var derivedStatus = hasFreshSummary
            ? "fresh-summary"
            : hasAssistantAfterAnchor
                ? "assistant-after-latest-user-without-handoff"
                : "awaiting-assistant-after-latest-user";

        return new SessionStateSnapshot(
            sessionId,
            backendSessionId,
            apiStatus,
            effectiveStatus,
            derivedStatus,
            messageCount,
            latestUserMessageId,
            latestAssistantMessageId,
            hasFreshSummary);
    }
}

