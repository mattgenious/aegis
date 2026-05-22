namespace HarnessCli.Core;

public enum BackendKind
{
    Opencode,
    Codex,
    Pi
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

        backendKind = value.Trim().ToLowerInvariant() switch
        {
            "opencode" => BackendKind.Opencode,
            "open-code" => BackendKind.Opencode,
            "codex" => BackendKind.Codex,
            "pi" => BackendKind.Pi,
            "pi.dev" => BackendKind.Pi,
            "pidev" => BackendKind.Pi,
            _ => BackendKind.Opencode
        };

        return value is "opencode" or "open-code" or "codex" or "pi" or "pi.dev" or "pidev";
    }

    public static string ToOptionValue(this BackendKind backendKind) => backendKind switch
    {
        BackendKind.Codex => "codex",
        BackendKind.Pi => "pi",
        _ => "opencode"
    };
}

