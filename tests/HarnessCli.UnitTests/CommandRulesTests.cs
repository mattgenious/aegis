using System;
using Xunit;

namespace HarnessCli.UnitTests;

public class CommandRulesTests
{
    [Fact]
    public void ConventionsDocumentExists()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var path = Path.Combine(directory, "CONTRIBUTING.md");

        Assert.True(File.Exists(path), "CONTRIBUTING.md must be present at the repo root.");
    }

    [Fact]
    public void CoreContractsLiveInReusableLibrary()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var coreProject = Path.Combine(directory, "src", "HarnessCli.Core", "HarnessCli.Core.csproj");
        var cliProject = Path.Combine(directory, "src", "HarnessCli", "HarnessCli.csproj");
        var cliProjectText = File.ReadAllText(cliProject);

        Assert.True(File.Exists(coreProject), "HarnessCli.Core must exist as a reusable library project.");
        Assert.Contains("HarnessCli.Core.csproj", cliProjectText);
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Core", "Core", "BackendContracts.cs")));
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Core", "Infrastructure", "SessionRegistryService.cs")));
        Assert.False(Directory.Exists(Path.Combine(directory, "src", "HarnessCli", "Core")));
        Assert.False(Directory.Exists(Path.Combine(directory, "src", "HarnessCli", "Infrastructure")));
    }

    [Fact]
    public void BackendAdaptersLiveInReusableLibrary()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var backendProject = Path.Combine(directory, "src", "HarnessCli.Backends", "HarnessCli.Backends.csproj");
        var cliProject = Path.Combine(directory, "src", "HarnessCli", "HarnessCli.csproj");
        var cliProjectText = File.ReadAllText(cliProject);

        Assert.True(File.Exists(backendProject), "HarnessCli.Backends must exist as a reusable backend adapter project.");
        Assert.Contains("HarnessCli.Backends.csproj", cliProjectText);
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Backends", "Backends", "CodexBackend.cs")));
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Backends", "Backends", "OpenCodeBackend.cs")));
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Backends", "Backends", "PiBackend.cs")));
        Assert.False(Directory.Exists(Path.Combine(directory, "src", "HarnessCli", "Backends")));
    }

    [Fact]
    public void CliProjectUsesBackendAgnosticCommandName()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var cliProjectPath = Path.Combine(directory, "src", "HarnessCli", "HarnessCli.csproj");
        var legacyProjectPath = Path.Combine(directory, "src", "HarnessCli", "OpencodeHarnessCli.csproj");
        var cliProject = File.ReadAllText(cliProjectPath);
        var solution = File.ReadAllText(Path.Combine(directory, "harness-cli.sln"));

        Assert.True(File.Exists(cliProjectPath), "CLI app project should use the backend-agnostic HarnessCli name.");
        Assert.False(File.Exists(legacyProjectPath), "The old OpenCode-specific project name should not be restored.");
        Assert.Contains("<AssemblyName>harness-cli</AssemblyName>", cliProject);
        Assert.Contains("opencode-harness-cli.cmd", cliProject);
        Assert.Contains("src\\HarnessCli\\HarnessCli.csproj", solution);
        Assert.DoesNotContain("OpencodeHarnessCli", solution);
    }

    [Fact]
    public void LibraryProjectsHavePackageMetadata()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var coreProject = File.ReadAllText(Path.Combine(directory, "src", "HarnessCli.Core", "HarnessCli.Core.csproj"));
        var backendProject = File.ReadAllText(Path.Combine(directory, "src", "HarnessCli.Backends", "HarnessCli.Backends.csproj"));

        Assert.Contains("<PackageId>HarnessCli.Core</PackageId>", coreProject);
        Assert.Contains("<PackageId>HarnessCli.Backends</PackageId>", backendProject);
        Assert.Contains("<Version>0.1.2</Version>", coreProject);
        Assert.Contains("<Version>0.1.2</Version>", backendProject);
        Assert.Contains("PackageCopyToOutput=\"true\"", coreProject);
        Assert.Contains("PackageCopyToOutput=\"true\"", backendProject);
    }

    [Fact]
    public void BuiltInPromptsLiveAsMarkdownFiles()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var promptFiles = Directory.GetFiles(Path.Combine(directory, "prompts"), "*.md", SearchOption.AllDirectories);

        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "opencode.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "codex.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "pi.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "copilot.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("spawn", "ship-target.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("watch", "default.md"), StringComparison.Ordinal));

        var sourceFiles = Directory.GetFiles(Path.Combine(directory, "src"), "*.cs", SearchOption.AllDirectories);
        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("Operating contract:", source);
            Assert.DoesNotContain("Operating boundaries:", source);
            Assert.DoesNotContain("Please check whether the delegated work", source);
        }
    }

    private static string LocateRepoRoot(string startPath)
    {
        var path = Path.GetFullPath(startPath);
        for (var depth = 0; depth < 12; depth++)
        {
            if (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")))
            {
                return path;
            }

            path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Unable to find repo root.");
        }

        throw new InvalidOperationException("Could not locate repository root from test working directory.");
    }
}
