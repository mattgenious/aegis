using System.Diagnostics;
using System.Text;

namespace Aegis.Backends;

internal static class CopilotProcess
{
    public static ProcessStartInfo CreateStartInfo(string copilotBinary, IReadOnlyList<string> args)
    {
        copilotBinary = ResolveBinary(copilotBinary);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (OperatingSystem.IsWindows() && IsWindowsCommandScript(copilotBinary))
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("& $env:AEGIS_COPILOT_COMMAND_SCRIPT @args");
            startInfo.Environment["AEGIS_COPILOT_COMMAND_SCRIPT"] = copilotBinary;
            AddArguments(startInfo, args);
            return startInfo;
        }

        if (OperatingSystem.IsWindows() && copilotBinary.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return PowerShellStartInfo(copilotBinary, args, startInfo);
        }

        if (!OperatingSystem.IsWindows() && copilotBinary.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add(copilotBinary);
        }
        else
        {
            startInfo.FileName = copilotBinary;
        }

        AddArguments(startInfo, args);
        return startInfo;
    }

    private static ProcessStartInfo PowerShellStartInfo(
        string copilotBinary,
        IReadOnlyList<string> args,
        ProcessStartInfo startInfo)
    {
        startInfo.FileName = "powershell.exe";
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(copilotBinary);
        AddArguments(startInfo, args);
        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
    }

    private static bool IsWindowsCommandScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string ResolveBinary(string binary)
    {
        if (!OperatingSystem.IsWindows()
            || IsWindowsCommandScript(binary)
            || binary.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || binary.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            || binary.Contains(Path.DirectorySeparatorChar)
            || binary.Contains(Path.AltDirectorySeparatorChar))
        {
            return binary;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, binary + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return binary;
    }
}
