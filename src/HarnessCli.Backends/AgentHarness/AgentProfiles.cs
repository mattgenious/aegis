using HarnessCli.Core;

namespace HarnessCli.Backends;

public sealed record AgentHarnessConfiguration
{
    public BackendKind DefaultBackend { get; init; } = BackendKind.Opencode;

    public string? DefaultProfile { get; init; }

    public IReadOnlyDictionary<string, AgentModelProfile> Profiles { get; init; } =
        new Dictionary<string, AgentModelProfile>(StringComparer.OrdinalIgnoreCase);
}

public sealed record AgentModelProfile
{
    public BackendKind? Backend { get; init; }

    public string? ModelProvider { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public string? Agent { get; init; }

    public string? System { get; init; }

    public TimeSpan? Timeout { get; init; }
}

public sealed record AgentProfileSelection
{
    public string? Profile { get; init; }

    public BackendKind? Backend { get; init; }

    public string? Model { get; init; }

    public string? Variant { get; init; }

    public string? Agent { get; init; }

    public string? System { get; init; }

    public TimeSpan? Timeout { get; init; }
}

public sealed record ResolvedAgentProfile(
    BackendKind Backend,
    string? ModelProvider,
    string? Model,
    string? Variant,
    string? Agent,
    string? System,
    TimeSpan? Timeout);

public sealed class AgentProfileResolver
{
    private readonly AgentHarnessConfiguration _configuration;

    public AgentProfileResolver(AgentHarnessConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ResolvedAgentProfile Resolve(AgentProfileSelection selection)
    {
        var profile = ResolveProfile(selection.Profile ?? _configuration.DefaultProfile);
        var model = ResolveModel(selection.Model);

        return new ResolvedAgentProfile(
            selection.Backend ?? profile?.Backend ?? _configuration.DefaultBackend,
            model is null ? profile?.ModelProvider : model.Provider,
            model?.Model ?? profile?.Model,
            selection.Variant ?? model?.Variant ?? profile?.Variant,
            selection.Agent ?? profile?.Agent,
            selection.System ?? profile?.System,
            selection.Timeout ?? profile?.Timeout);
    }

    private AgentModelProfile? ResolveProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        if (_configuration.Profiles.TryGetValue(profileName, out var profile))
        {
            return profile;
        }

        throw new ArgumentException($"Unknown agent profile '{profileName}'.");
    }

    private static AgentModelReference? ResolveModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return AgentModelReference.Parse(model);
    }
}

public sealed record AgentModelReference(string? Provider, string Model, string? Variant)
{
    public static AgentModelReference Parse(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Model cannot be empty.");
        }

        var slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            return new AgentModelReference(null, trimmed, null);
        }

        if (slash == 0 || slash == trimmed.Length - 1)
        {
            throw new ArgumentException("Model must be either a model id or provider/model.");
        }

        var remainder = trimmed[(slash + 1)..];
        var variantSlash = remainder.IndexOf('/');
        if (variantSlash < 0)
        {
            return new AgentModelReference(trimmed[..slash], remainder, null);
        }

        if (variantSlash == 0 || variantSlash == remainder.Length - 1)
        {
            throw new ArgumentException("Model variant must use provider/model/variant.");
        }

        return new AgentModelReference(
            trimmed[..slash],
            remainder[..variantSlash],
            remainder[(variantSlash + 1)..]);
    }
}
