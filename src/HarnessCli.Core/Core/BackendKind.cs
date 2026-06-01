namespace HarnessCli.Core;

public enum BackendKind
{
    Opencode,
    Codex,
    Pi,
    Copilot
}

public static class BackendKindExtensions
{
    public static bool TryParse(string? value, out BackendKind backendKind)
    {
        backendKind = BackendKind.Opencode;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        backendKind = normalized switch
        {
            "opencode" => BackendKind.Opencode,
            "open-code" => BackendKind.Opencode,
            "codex" => BackendKind.Codex,
            "pi" => BackendKind.Pi,
            "pi.dev" => BackendKind.Pi,
            "pidev" => BackendKind.Pi,
            "copilot" => BackendKind.Copilot,
            "github-copilot" => BackendKind.Copilot,
            "githubcopilot" => BackendKind.Copilot,
            _ => BackendKind.Opencode
        };

        return normalized is "opencode" or "open-code" or "codex" or "pi" or "pi.dev" or "pidev" or "copilot" or "github-copilot" or "githubcopilot";
    }

    public static string ToOptionValue(this BackendKind backendKind) => backendKind switch
    {
        BackendKind.Codex => "codex",
        BackendKind.Pi => "pi",
        BackendKind.Copilot => "copilot",
        _ => "opencode"
    };
}
