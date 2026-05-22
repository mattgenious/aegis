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
