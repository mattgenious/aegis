namespace Aegis.Core;

public static class PromptTemplates
{
    public static string Render(string relativePath, IReadOnlyDictionary<string, string> values)
    {
        var template = File.ReadAllText(ResolvePath(relativePath));
        foreach (var item in values)
        {
            template = template.Replace("{{" + item.Key + "}}", item.Value, StringComparison.Ordinal);
        }

        return template.TrimEnd() + Environment.NewLine;
    }

    private static string ResolvePath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in CandidateRoots())
        {
            var path = Path.Combine(root, "prompts", normalized);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"Prompt template not found: prompts/{relativePath}");
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return AppContext.BaseDirectory;
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }
}
