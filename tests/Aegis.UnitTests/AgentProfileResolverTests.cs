using Aegis.Backends;
using Aegis.Core;
using Xunit;

namespace Aegis.UnitTests;

public sealed class AgentProfileResolverTests
{
    [Fact]
    public void ResolveUsesDefaultBackendWhenNoProfileOrOverridesExist()
    {
        var resolver = new AgentProfileResolver(new AgentHarnessConfiguration
        {
            DefaultBackend = BackendKind.Codex
        });

        var resolved = resolver.Resolve(new AgentProfileSelection());

        Assert.Equal(BackendKind.Codex, resolved.Backend);
        Assert.Null(resolved.Model);
    }

    [Fact]
    public void ResolveUsesNamedProfileSettings()
    {
        var resolver = new AgentProfileResolver(new AgentHarnessConfiguration
        {
            DefaultBackend = BackendKind.Opencode,
            Profiles = new Dictionary<string, AgentModelProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["deep"] = new AgentModelProfile
                {
                    Backend = BackendKind.Opencode,
                    ModelProvider = "github-copilot",
                    Model = "gpt-5.5",
                    Variant = "high",
                    Agent = "build",
                    System = "focus",
                    Timeout = TimeSpan.FromMinutes(20)
                }
            }
        });

        var resolved = resolver.Resolve(new AgentProfileSelection { Profile = "DEEP" });

        Assert.Equal(BackendKind.Opencode, resolved.Backend);
        Assert.Equal("github-copilot", resolved.ModelProvider);
        Assert.Equal("gpt-5.5", resolved.Model);
        Assert.Equal("high", resolved.Variant);
        Assert.Equal("build", resolved.Agent);
        Assert.Equal("focus", resolved.System);
        Assert.Equal(TimeSpan.FromMinutes(20), resolved.Timeout);
    }

    [Fact]
    public void ResolveLetsExplicitOverridesWinOverProfile()
    {
        var resolver = new AgentProfileResolver(new AgentHarnessConfiguration
        {
            DefaultBackend = BackendKind.Opencode,
            Profiles = new Dictionary<string, AgentModelProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["fast"] = new AgentModelProfile
                {
                    Backend = BackendKind.Opencode,
                    ModelProvider = "github-copilot",
                    Model = "gpt-5.4-mini",
                    Variant = "low",
                    Timeout = TimeSpan.FromMinutes(5)
                }
            }
        });

        var resolved = resolver.Resolve(new AgentProfileSelection
        {
            Profile = "fast",
            Backend = BackendKind.Pi,
            Model = "pi-large",
            Variant = "medium",
            Timeout = TimeSpan.FromMinutes(12)
        });

        Assert.Equal(BackendKind.Pi, resolved.Backend);
        Assert.Null(resolved.ModelProvider);
        Assert.Equal("pi-large", resolved.Model);
        Assert.Equal("medium", resolved.Variant);
        Assert.Equal(TimeSpan.FromMinutes(12), resolved.Timeout);
    }

    [Fact]
    public void ResolveParsesProviderModelVariant()
    {
        var resolver = new AgentProfileResolver(new AgentHarnessConfiguration());

        var resolved = resolver.Resolve(new AgentProfileSelection
        {
            Model = "github-copilot/gpt-5.5/xhigh"
        });

        Assert.Equal("github-copilot", resolved.ModelProvider);
        Assert.Equal("gpt-5.5", resolved.Model);
        Assert.Equal("xhigh", resolved.Variant);
    }
}
