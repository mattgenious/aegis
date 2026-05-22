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
        var cliProject = Path.Combine(directory, "src", "HarnessCli", "OpencodeHarnessCli.csproj");
        var cliProjectText = File.ReadAllText(cliProject);

        Assert.True(File.Exists(coreProject), "HarnessCli.Core must exist as a reusable library project.");
        Assert.Contains("HarnessCli.Core.csproj", cliProjectText);
        Assert.True(File.Exists(Path.Combine(directory, "src", "HarnessCli.Core", "Core", "BackendContracts.cs")));
        Assert.False(Directory.Exists(Path.Combine(directory, "src", "HarnessCli", "Core")));
    }

    [Fact]
    public void BuiltInPromptsLiveAsMarkdownFiles()
    {
        var directory = LocateRepoRoot(Directory.GetCurrentDirectory());
        var promptFiles = Directory.GetFiles(Path.Combine(directory, "prompts"), "*.md", SearchOption.AllDirectories);

        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "opencode.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "codex.md"), StringComparison.Ordinal));
        Assert.Contains(promptFiles, path => path.EndsWith(Path.Combine("delegation", "pi.md"), StringComparison.Ordinal));
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
