using Aegis.Backends;
using Aegis.Core;
using Xunit;

namespace Aegis.UnitTests;

public sealed class BackendAvailabilityDetectorTests
{
    [Fact]
    public void DetectUsesRequiredBackendPriorityOrder()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("bin-a", "opencode"),
            Path.Combine("bin-b", "codex"),
            Path.Combine("bin-c", "copilot")
        };

        var report = BackendAvailabilityDetector.Detect(new BackendAvailabilityProbeOptions
        {
            Path = string.Join(Path.PathSeparator, "bin-a", "bin-b", "bin-c"),
            IsWindows = false,
            FileExists = existing.Contains
        });

        Assert.Equal(["codex", "opencode", "pi", "copilot"], report.SelectionOrder);
        Assert.Equal(BackendKind.Codex, report.PreferredBackendKind);
        Assert.Equal("codex", report.PreferredBackend);
        Assert.Equal(["codex", "opencode", "copilot"], report.AvailableBackends);
    }

    [Fact]
    public void DetectHonorsBackendBinaryOverrides()
    {
        var configuredCodex = Path.Combine("custom", "codex-custom");
        var configuredCopilot = Path.Combine("custom", "copilot-custom");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            configuredCodex,
            configuredCopilot
        };

        var report = BackendAvailabilityDetector.Detect(new BackendAvailabilityProbeOptions
        {
            Path = string.Empty,
            IsWindows = false,
            FileExists = existing.Contains,
            GetEnvironmentVariable = name => name switch
            {
                "AEGIS_CODEX_BINARY" => configuredCodex,
                "AEGIS_COPILOT_BINARY" => configuredCopilot,
                _ => null
            }
        });

        var codex = Assert.Single(report.Backends, backend => backend.Kind == BackendKind.Codex);
        var copilot = Assert.Single(report.Backends, backend => backend.Kind == BackendKind.Copilot);
        Assert.True(codex.Available);
        Assert.True(copilot.Available);
        Assert.Equal(configuredCodex, codex.CommandPath);
        Assert.Equal(configuredCopilot, copilot.CommandPath);
        Assert.Equal("env:AEGIS_CODEX_BINARY", codex.Probe);
        Assert.Equal("env:AEGIS_COPILOT_BINARY", copilot.Probe);
    }

    [Fact]
    public void DetectUsesWindowsPathExtForCommandScripts()
    {
        var fakeBin = @"C:\tools";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(fakeBin, "opencode.cmd")
        };

        var report = BackendAvailabilityDetector.Detect(new BackendAvailabilityProbeOptions
        {
            Path = fakeBin,
            PathExt = ".EXE;.CMD",
            IsWindows = true,
            FileExists = existing.Contains
        });

        var opencode = Assert.Single(report.Backends, backend => backend.Kind == BackendKind.Opencode);
        Assert.True(opencode.Available);
        Assert.Equal(Path.Combine(fakeBin, "opencode.cmd"), opencode.CommandPath, ignoreCase: true);
    }
}
