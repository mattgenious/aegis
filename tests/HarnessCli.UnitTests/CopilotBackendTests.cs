using System.Collections.Immutable;
using System.Text.Json;
using HarnessCli.Backends;
using HarnessCli.Core;
using HarnessCli.Infrastructure;
using Xunit;

namespace HarnessCli.UnitTests;

public class CopilotBackendTests
{
    [Fact]
    public async Task CanCreateSessionAndParseSummaryFromPersistedHistory()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var backend = new CopilotBackend(stateRoot: stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-backend-session", null, tempDir));

            Assert.True(File.Exists(session.BackendMetadataPath + ".status.json"));
            Assert.False(Directory.Exists(Path.Combine(tempDir, ".harness-cli")));
            Assert.Equal(BackendKind.Copilot, session.Backend);

            await File.WriteAllTextAsync(session.BackendMetadataPath + ".messages.jsonl", JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = "copilot_msg_1",
                    Role = "assistant",
                    Text = "Task started\nFINAL HANDOFF\nImplemented requested logic.",
                    PartId = "part_copilot_1",
                    Timestamp = "2026-01-01T12:00:00+00:00"
                }
            }));

            var state = await backend.GetSessionStateAsync(session);
            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF");

            Assert.Equal("idle", state.EffectiveStatus);
            Assert.True(state.HasFreshSummary);
            Assert.NotNull(summary);
            Assert.Equal("copilot_msg_1", summary!.MessageId);
            Assert.Equal("part_copilot_1", summary.PartId);
            Assert.Equal("Implemented requested logic.", summary.Text);
        });
    }

    [Fact]
    public async Task PostPromptParsesJsonArrayOutput()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var output = "[{\"id\":\"assistant-json\",\"role\":\"assistant\",\"content\":[{\"text\":\"Task started\"},{\"text\":\"\\nFINAL HANDOFF\\nParsed JSON output.\"}],\"part_id\":\"part-json\",\"timestamp\":\"2026-01-01T12:00:00+00:00\"}]";
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, output), stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-json-session", null, tempDir));

            var result = await backend.PostPromptAsync(session, RawPrompt());
            var messages = await backend.GetMessagesAsync(session);
            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF");

            Assert.True(result.IsSuccess, result.Error ?? result.Message);
            Assert.Contains(messages, message => message.Id == "assistant-json" && message.Text.Contains("Parsed JSON output."));
            Assert.NotNull(summary);
            Assert.Equal("assistant-json", summary!.MessageId);
            Assert.Equal("part-json", summary.PartId);
            Assert.Equal("Parsed JSON output.", summary.Text);
        });
    }

    [Fact]
    public async Task PostPromptParsesJsonlOutput()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var output = "{\"id\":\"a1\",\"role\":\"assistant\",\"text\":\"first\"}\n{\"id\":\"a2\",\"role\":\"assistant\",\"text\":\"FINAL HANDOFF\\nParsed JSONL.\"}";
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, output), stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-jsonl-session", null, tempDir));

            var result = await backend.PostPromptAsync(session, RawPrompt());
            var summary = await backend.ExtractSummaryAsync(session, "FINAL HANDOFF");

            Assert.True(result.IsSuccess, result.Error ?? result.Message);
            Assert.NotNull(summary);
            Assert.Equal("a2", summary!.MessageId);
            Assert.Equal("Parsed JSONL.", summary.Text);
        });
    }

    [Fact]
    public async Task PostPromptFallsBackToPlainTextOutput()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, "Plain text response with FINAL HANDOFF fallback."), stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-text-session", null, tempDir));

            var result = await backend.PostPromptAsync(session, RawPrompt());
            var messages = await backend.GetMessagesAsync(session);

            Assert.True(result.IsSuccess, result.Error ?? result.Message);
            Assert.Contains(messages, message => message.Role == "assistant" && message.Text == "Plain text response with FINAL HANDOFF fallback.");
        });
    }

    [Fact]
    public async Task PostPromptPassesExplicitPermissionFlags()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var argsPath = Path.Combine(tempDir, "copilot-args.txt");
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, "FINAL HANDOFF\npermissions forwarded", argsPath), stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-permissions-session", null, tempDir));
            var request = RawPrompt() with
            {
                Options = ImmutableDictionary<string, string>.Empty
                    .Add("copilot.allowTool", "Edit;Bash")
                    .Add("copilot.allowUrl", "https://github.com;https://docs.github.com")
                    .Add("copilot.allowAll", "true")
            };

            var result = await backend.PostPromptAsync(session, request);
            var args = await File.ReadAllTextAsync(argsPath);

            Assert.True(result.IsSuccess, result.Error ?? result.Message);
            Assert.Contains("--prompt", args);
            Assert.Contains("Edit", args);
            Assert.Contains("Bash", args);
            Assert.Contains("--allow-url", args);
            Assert.Contains("https://github.com", args);
            Assert.Contains("https://docs.github.com", args);
            Assert.Contains("--allow-all", args);
        });
    }

    [Fact]
    public async Task PostPromptRejectsAsyncUntilDetachedCopilotRunsAreSupported()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var argsPath = Path.Combine(tempDir, "copilot-args.txt");
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, "should not run", argsPath), stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-async-session", null, tempDir));
            var request = RawPrompt() with
            {
                Options = ImmutableDictionary<string, string>.Empty.Add("harness.async", "true")
            };

            var result = await backend.PostPromptAsync(session, request);

            Assert.False(result.IsSuccess);
            Assert.Contains("does not support --async", result.Message);
            Assert.False(File.Exists(argsPath));
        });
    }

    [Fact]
    public async Task BackendCommandServicePreservesAsyncIntentWhenWaiting()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var argsPath = Path.Combine(tempDir, "copilot-args.txt");
            var backend = new CopilotBackend(await WriteFakeCopilotAsync(tempDir, "should not run", argsPath), stateRoot);
            var registryPath = new TempRegistryPathProvider(Path.Combine(tempDir, "registry"));
            var commands = new BackendCommandService(backend, new SessionRegistryService(new FileSessionRegistry(registryPath)));

            var result = await commands.AskAsync(new BackendAskRequest(
                SessionId: null,
                Title: "copilot async wait",
                ParentSessionId: null,
                Directory: tempDir,
                Prompt: RawPrompt(),
                Async: true,
                Wait: true,
                Timeout: TimeSpan.FromSeconds(5)));

            Assert.False(result.PostResult.IsSuccess);
            Assert.Contains("does not support --async", result.PostResult.Message);
            Assert.False(File.Exists(argsPath));
        });
    }

    [WindowsOnlyFact]
    public async Task CreateStartInfoResolvesWindowsCommandScriptFromPath()
    {
        await WithTempDirs(async (tempDir, _) =>
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var commandPath = Path.Combine(tempDir, "copilot.cmd");
            await File.WriteAllTextAsync(commandPath, "@echo off\r\n");
            try
            {
                Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);

                var startInfo = CopilotProcess.CreateStartInfo("copilot", ["--version"]);

                Assert.Equal("powershell.exe", startInfo.FileName);
                Assert.True(startInfo.Environment.TryGetValue("HARNESS_CLI_COPILOT_COMMAND_SCRIPT", out var resolved));
                Assert.Equal(commandPath, resolved, ignoreCase: true);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        });
    }

    [Fact]
    public async Task PostPromptWithMissingBinaryReturnsGuidance()
    {
        await WithTempDirs(async (tempDir, stateRoot) =>
        {
            var backend = new CopilotBackend("/no/such/copilot-binary", stateRoot);
            var session = await backend.CreateSessionAsync(new CreateSessionRequest("copilot-session", null, tempDir));
            Directory.Delete(Path.GetDirectoryName(session.BackendMetadataPath)!, true);

            var result = await backend.PostPromptAsync(session, RawPrompt());

            Assert.False(result.IsSuccess);
            Assert.Contains("copilot executable", result.Message);
            Assert.Contains("copilot login", result.Error);
            Assert.Equal(127, result.ExitCode);
        });
    }

    private static PromptRequest RawPrompt() => new(
        Text: "Run a quick check",
        SourceKind: PromptSourceKind.Inline,
        SourceLocation: null,
        Raw: true);

    private static async Task WithTempDirs(Func<string, string, Task> action)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var stateRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            await action(tempDir, stateRoot);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            if (Directory.Exists(stateRoot)) Directory.Delete(stateRoot, true);
        }
    }

    private static async Task<string> WriteFakeCopilotAsync(string directory, string stdout, string? argsPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(directory, "fake-copilot.cmd");
            await File.WriteAllTextAsync(path, "@echo off\r\n" + ToWindowsArgsCapture(argsPath) + ToWindowsEcho(stdout));
            return path;
        }

        var scriptPath = Path.Combine(directory, "fake-copilot.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\n" + ToShArgsCapture(argsPath) + "printf '%s\\n' '" + stdout.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n");
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }

    private static string ToWindowsArgsCapture(string? argsPath) =>
        string.IsNullOrWhiteSpace(argsPath) ? string.Empty : "echo %* > \"" + argsPath + "\"\r\n";

    private static string ToShArgsCapture(string? argsPath) =>
        string.IsNullOrWhiteSpace(argsPath) ? string.Empty : "printf '%s\\n' \"$*\" > '" + argsPath.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n";

    private static string ToWindowsEcho(string stdout) => string.Join("\r\n", stdout.Split('\n').Select(line => "echo " + line)) + "\r\n";

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows command script resolution is Windows-only.";
        }
    }

    private sealed class TempRegistryPathProvider(string directoryPath) : ISessionRegistryPathProvider
    {
        public string DirectoryPath { get; } = directoryPath;
    }
}
