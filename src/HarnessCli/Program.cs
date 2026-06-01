using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessCli.Backends;
using HarnessCli.Core;
using HarnessCli.Infrastructure;

namespace HarnessCli;

internal static partial class Program
{
    private const string DefaultServer = "http://127.0.0.1:4096";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly AgentHarnessConfiguration DefaultAgentConfiguration = new()
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
            },
            ["cheap"] = new AgentModelProfile
            {
                Backend = BackendKind.Opencode,
                ModelProvider = "opencode",
                Model = "deepseek-v4-flash-free",
                Timeout = TimeSpan.FromMinutes(5)
            },
            ["deep"] = new AgentModelProfile
            {
                Backend = BackendKind.Opencode,
                ModelProvider = "github-copilot",
                Model = "gpt-5.5",
                Variant = "high",
                Timeout = TimeSpan.FromMinutes(20)
            }
        }
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        if (args[0] == "help")
        {
            if (args.Length == 1) PrintHelp();
            else return PrintCommandHelp(args[1]);
            return 0;
        }

        try
        {
            var command = args[0];
            if (command == "work-map")
            {
                return await RunWorkMapCommand(args.Skip(1).ToArray());
            }

            if (args.Skip(1).Any(IsHelpFlag))
            {
                return PrintCommandHelp(command);
            }

            var options = Options.Parse(args.Skip(1));
            var resolvedProfile = ResolveAgentProfile(options);
            options.ApplyResolvedProfile(resolvedProfile);
            using var http = CreateHttpClient(options.Server);
            var client = new OpenCodeClient(http);
            var backend = resolvedProfile.Backend;

            return await RouteCommandToBackend(backend, command, client, http, options);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"HTTP request failed: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            return Fail($"Timed out: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            return Fail($"Cancelled: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"Invalid JSON response: {ex.Message}");
        }
        catch (FormatException ex)
        {
            return Fail($"Invalid option value: {ex.Message}");
        }
        catch (IOException ex)
        {
            return Fail($"File or process I/O failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail($"Access denied: {ex.Message}");
        }
    }

    private static Task<int> RouteCommandToBackend(
        BackendKind backend,
        string command,
        OpenCodeClient client,
        HttpClient http,
        Options options)
    {
        if (backend == BackendKind.Opencode)
        {
            return RouteOpenCodeCommand(command, client, http, options);
        }

        return RouteBackendAgnosticCommand(backend, command, client, options);
    }

    private static Task<int> RouteBackendAgnosticCommand(
        BackendKind backend,
        string command,
        OpenCodeClient client,
        Options options)
    {
        var backendService = CreateBackend(backend, client);
        var registry = new SessionRegistryService(new FileSessionRegistry());
        var commands = new BackendCommandService(backendService, registry);

        return command switch
        {
            "new" => NewViaBackend(commands, options),
            "latest" => LatestViaBackend(commands, backendService, options),
            "messages" => MessagesViaBackend(commands, options),
            "wait" => WaitViaBackend(commands, options),
            "abort" => AbortViaBackend(commands, backendService, options),
            "ask" => AskViaBackend(commands, backendService, options),
            "status" => StatusViaBackend(commands, backendService, options),
            "last-summary" => LastSummaryViaBackend(commands, backendService, options),
            _ => Task.FromResult(Fail($"Backend '{backend.ToOptionValue()}' has not yet wired command '{command}'."))
        };
    }

    private static ISessionBackend CreateBackend(BackendKind backend, OpenCodeClient client) =>
        backend switch
        {
            BackendKind.Codex => new CodexBackend(),
            BackendKind.Pi => new PiBackend(),
            BackendKind.Copilot => new CopilotBackend(),
            _ => new OpencodeBackend(client)
        };

    private static ResolvedAgentProfile ResolveAgentProfile(Options options)
    {
        BackendKind? backendOverride = null;
        var backendValue = options.Backend ?? options.Engine;
        if (!string.IsNullOrWhiteSpace(backendValue))
        {
            if (!BackendKindExtensions.TryParse(backendValue, out var backendKind))
            {
                throw new ArgumentException($"Unsupported backend '{backendValue}'. Use --backend opencode, codex, pi, or copilot.");
            }

            backendOverride = backendKind;
        }

        var resolved = new AgentProfileResolver(DefaultAgentConfiguration).Resolve(new AgentProfileSelection
        {
            Profile = options.Profile,
            Backend = backendOverride,
            Model = options.Model,
            Variant = options.Variant,
            Agent = options.Agent,
            System = options.System,
            Timeout = options.TimeoutWasProvided ? TimeSpan.FromSeconds(options.TimeoutSeconds) : null
        });

        if (resolved.Backend == BackendKind.Opencode
            && !string.IsNullOrWhiteSpace(resolved.Model)
            && string.IsNullOrWhiteSpace(resolved.ModelProvider))
        {
            throw new ArgumentException("OpenCode model selection must use provider/model, for example github-copilot/gpt-5.5.");
        }

        return resolved;
    }

    private static Task<int> RouteOpenCodeCommand(
        string command,
        OpenCodeClient client,
        HttpClient http,
        Options options)
    {
        return command switch
        {
            "health" => Health(client),
            "ensure-server" => EnsureServer(options),
            "self-test" => Task.FromResult(SelfTest()),
            "new" => NewSession(client, options),
            "latest" => Latest(client, options),
            "spawn" => Spawn(client, options),
            "ask" => Ask(client, options),
            "status" => Status(client, options),
            "messages" => Messages(client, options),
            "last-summary" => LastSummary(client, options),
            "wait" => Wait(client, options),
            "abort" => Abort(client, options),
            "events" => Events(http, options),
            "watch" => Watch(client, options),
            "watch-many" => WatchMany(client, options),
            "tail" => Tail(client, options),
            "export" => Export(client, options),
            _ => Task.FromResult(Fail($"Unknown command '{command}'. Run with --help for usage."))
        };
    }

    private static HttpClient CreateHttpClient(string server)
    {
        var http = new HttpClient { BaseAddress = new Uri(NormalizeServer(server)) };
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }

    private static string NormalizeServer(string server)
    {
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            server = "http://" + server;
        }

        return server.EndsWith('/') ? server : server + "/";
    }

    private static string EffectiveServer(Options options)
    {
        var normalized = NormalizeServer(options.Server);
        if (options.Port is null) return normalized;

        var builder = new UriBuilder(normalized) { Port = options.Port.Value };
        return builder.Uri.ToString();
    }

    private static string WithDirectory(string path, Options options)
    {
        if (string.IsNullOrWhiteSpace(options.Directory)) return path;

        var separator = path.Contains('?') ? "&" : "?";
        return path + separator + "directory=" + Uri.EscapeDataString(Path.GetFullPath(options.Directory));
    }

    private static async Task<int> Health(OpenCodeClient client)
    {
        var health = await client.GetJson("global/health");
        WriteJson(health);
        return 0;
    }

    private static async Task<int> EnsureServer(Options options)
    {
        var effectiveServer = EffectiveServer(options);
        using var healthClient = CreateHttpClient(effectiveServer);
        healthClient.Timeout = TimeSpan.FromSeconds(2);

        try
        {
            var response = await healthClient.GetAsync("global/health");
            if (response.IsSuccessStatusCode)
            {
                var health = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
                WriteJson(new JsonObject
                {
                    ["status"] = "already-running",
                    ["server"] = NormalizeServer(effectiveServer).TrimEnd('/'),
                    ["health"] = health
                });
                return 0;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    $"An OpenCode server is already listening at {NormalizeServer(effectiveServer).TrimEnd('/')} but requires auth. " +
                    "Stop that server or choose another port; ensure-server only removes auth when it starts the child process itself.");
            }
        }
        catch (HttpRequestException)
        {
            // Start below.
        }
        catch (TaskCanceledException)
        {
            // Start below.
        }

        var uri = new Uri(NormalizeServer(effectiveServer));
        var hostname = options.Hostname ?? (uri.Host is "localhost" ? "127.0.0.1" : uri.Host);
        var port = options.Port ?? uri.Port;
        if (await CanConnectTcp(uri.Host, port, TimeSpan.FromSeconds(2)))
        {
            var health = await WaitForHealthyServer(healthClient, DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds));
            if (health is not null)
            {
                WriteJson(new JsonObject
                {
                    ["status"] = "already-running",
                    ["server"] = NormalizeServer(effectiveServer).TrimEnd('/'),
                    ["health"] = health
                });
                return 0;
            }

            throw new TimeoutException($"Port {port} is already listening, but /global/health did not become healthy within {options.TimeoutSeconds}s. Stop that process or choose another port.");
        }

        var logDir = options.LogDir ?? Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(logDir);

        var logStamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        var stdout = Path.Combine(logDir, $"opencode-serve-{port}-{logStamp}.out.log");
        var stderr = Path.Combine(logDir, $"opencode-serve-{port}-{logStamp}.err.log");
        var processId = StartOpenCodeServer(options, hostname, port, stdout, stderr);

        using var startedClient = CreateHttpClient($"http://127.0.0.1:{port}");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.TimeoutSeconds);
        var listeningText = $"opencode server listening on http://{hostname}:{port}";
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var response = await startedClient.GetAsync("global/health", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var health = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, cts.Token);
                    WriteJson(new JsonObject
                    {
                        ["status"] = "started",
                        ["server"] = $"http://127.0.0.1:{port}",
                        ["hostname"] = hostname,
                        ["port"] = port,
                        ["pid"] = processId,
                        ["stdout"] = stdout,
                        ["stderr"] = stderr,
                        ["health"] = health
                    });
                    return 0;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            if (await FileContains(stdout, listeningText))
            {
                WriteJson(new JsonObject
                {
                    ["status"] = "started-listening",
                    ["server"] = $"http://127.0.0.1:{port}",
                    ["hostname"] = hostname,
                    ["port"] = port,
                    ["pid"] = processId,
                    ["stdout"] = stdout,
                    ["stderr"] = stderr,
                    ["health"] = "not-yet-verified"
                });
                return 0;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Timed out waiting for opencode server on port {port}. See {stderr}");
    }

    private static int? StartOpenCodeServer(Options options, string hostname, int port, string stdout, string stderr)
    {
        var workingDirectory = options.Directory ?? Environment.CurrentDirectory;
        var arguments = new List<string>
        {
            "serve",
            "--hostname",
            hostname,
            "--port",
            port.ToString()
        };
        if (options.PrintLogs)
        {
            arguments.Add("--print-logs");
            arguments.Add("--log-level");
            arguments.Add(options.LogLevel ?? "DEBUG");
        }

        if (OperatingSystem.IsWindows())
        {
            var command = string.Join(Environment.NewLine,
                "@echo off",
                "set OPENCODE_SERVER_PASSWORD=",
                "set OPENCODE_SERVER_USERNAME=",
                "cd /d " + WindowsCmdQuote(workingDirectory),
                "opencode " + string.Join(' ', arguments.Select(WindowsCmdQuote)) +
                " 1>>" + WindowsCmdQuote(stdout) + " 2>>" + WindowsCmdQuote(stderr));
            var launcher = Path.Combine(Path.GetDirectoryName(stdout)!, Path.GetFileNameWithoutExtension(stdout) + ".cmd");
            File.WriteAllText(launcher, command, Encoding.ASCII);
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/c start \"opencode-serve-{port}\" /min {WindowsCmdQuote(launcher)}"
            };
            using var starter = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start opencode.");
            starter.WaitForExit(5000);
            return null;
        }

        var unixCommand =
            "cd " + PosixShellQuote(workingDirectory) +
            " && (tail -f /dev/null | env -u OPENCODE_SERVER_PASSWORD -u OPENCODE_SERVER_USERNAME opencode " +
            string.Join(' ', arguments.Select(PosixShellQuote)) +
            " >>" + PosixShellQuote(stdout) +
            " 2>>" + PosixShellQuote(stderr) +
            ") & echo $!";

        var unixStartInfo = new ProcessStartInfo
        {
            FileName = "sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        unixStartInfo.ArgumentList.Add("-c");
        unixStartInfo.ArgumentList.Add(unixCommand);

        using var unixLauncher = Process.Start(unixStartInfo) ?? throw new InvalidOperationException("Failed to start opencode.");
        var pidText = (unixLauncher.StandardOutput.ReadLine() ?? string.Empty).Trim();
        var errorText = unixLauncher.StandardError.ReadToEnd().Trim();
        unixLauncher.WaitForExit(5000);
        if (unixLauncher.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to launch opencode: {errorText}");
        }

        return int.TryParse(pidText, out var pid) ? pid : null;
    }

    private static string WindowsCmdQuote(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string PosixShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static async Task<bool> FileContains(string path, string text)
    {
        if (!File.Exists(path)) return false;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = await reader.ReadToEndAsync();
            return content.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<JsonObject?> WaitForHealthyServer(HttpClient healthClient, DateTimeOffset deadline)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var response = await healthClient.GetAsync("global/health", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions, cts.Token);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(
                        $"An OpenCode server is already listening at {healthClient.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? "the target server"} but requires auth. " +
                        "Stop that server or choose another port; ensure-server only removes auth when it starts the child process itself.");
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(500);
        }

        return null;
    }

    private static async Task<bool> CanConnectTcp(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeout));
            if (completed != connectTask) return false;

            await connectTask;
            return client.Connected;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task PumpToFile(StreamReader reader, string path)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
        }
    }

    private static async Task<int> NewSession(OpenCodeClient client, Options options)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(options.Title)) body["title"] = options.Title;
        if (!string.IsNullOrWhiteSpace(options.Parent)) body["parentID"] = options.Parent;
        var session = await client.PostJson(WithDirectory("session", options), body);
        WriteJson(session);
        return 0;
    }

    private static async Task<int> Latest(OpenCodeClient client, Options options)
    {
        var path = "session?limit=" + (options.Limit > 0 ? options.Limit : 20);
        if (!string.IsNullOrWhiteSpace(options.Search)) path += "&search=" + Uri.EscapeDataString(options.Search);
        var sessions = await client.GetJson(path);
        if (sessions is not JsonArray array || array.Count == 0)
        {
            return Fail("No sessions found.");
        }

        if (options.All)
        {
            WriteJson(array);
        }
        else
        {
            WriteJson(array[0]);
        }
        return 0;
    }

    private static async Task<int> Spawn(OpenCodeClient client, Options options)
    {
        if (options.Targets.Count == 0) throw new ArgumentException("At least one --target is required.");

        var results = new JsonArray();
        foreach (var target in options.Targets)
        {
            if (options.ResumeSessions.TryGetValue(target, out var resumedSession))
            {
                Summary? summary = null;
                if (options.Wait)
                {
                    await WaitForCompletion(client, resumedSession, options, TimeSpan.FromSeconds(options.TimeoutSeconds));
                    summary = await FindLastAssistantSummary(client, resumedSession, options);
                }

                results.Add(new JsonObject
                {
                    ["target"] = target,
                    ["sessionID"] = resumedSession,
                    ["status"] = summary is null ? "resumed" : "completed",
                    ["summary"] = summary?.Text,
                    ["messageID"] = summary?.MessageId,
                    ["partID"] = summary?.PartId
                });
                continue;
            }

            var prompt = BuildShipPrompt(target, options);
            var createBody = new JsonObject { ["title"] = "Ship: " + target };
            var created = await client.PostJson(WithDirectory("session", options), createBody);
            var session = created?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Session create response did not include id.");

            await client.PostNoContent(WithDirectory($"session/{session}/prompt_async", options), PromptBody(prompt, options));
            Summary? launchedSummary = null;
            if (options.Wait)
            {
                await WaitForCompletion(client, session, options, TimeSpan.FromSeconds(options.TimeoutSeconds), -1);
                launchedSummary = await FindLastAssistantSummary(client, session, options, -1);
            }

            results.Add(new JsonObject
            {
                ["target"] = target,
                ["sessionID"] = session,
                ["status"] = launchedSummary is null ? "launched" : "completed",
                ["summary"] = launchedSummary?.Text,
                ["messageID"] = launchedSummary?.MessageId,
                ["partID"] = launchedSummary?.PartId
            });
        }

        WriteJson(results);
        return 0;
    }

    private static string BuildShipPrompt(string target, Options options)
    {
        var directory = string.IsNullOrWhiteSpace(options.Directory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(options.Directory);

        return PromptTemplates.Render("spawn/ship-target.md", new Dictionary<string, string>
        {
            ["target"] = target,
            ["directory"] = directory
        });
    }

    private static async Task<int> NewViaBackend(
        BackendCommandService commands,
        Options options)
    {
        var stored = await commands.CreateSessionAsync(new CreateBackendSessionRequest(
            options.Title,
            options.Parent,
            options.Directory));

        WriteJson(new JsonObject
        {
            ["sessionID"] = stored.SessionId,
            ["backend"] = stored.Backend.ToOptionValue(),
            ["backendSessionID"] = stored.BackendSessionId,
            ["directory"] = stored.Directory,
            ["metadata"] = JsonSerializer.SerializeToNode(stored.Metadata)
        });

        return 0;
    }

    private static async Task<int> LatestViaBackend(
        BackendCommandService commands,
        ISessionBackend backend,
        Options options)
    {
        var latest = await commands.GetLatestSessionsAsync(new BackendLatestSessionsRequest(
            options.Search,
            options.Limit > 0 ? options.Limit : 20));
        var sessions = latest.ToArray();
        if (sessions.Length == 0)
        {
            return string.IsNullOrWhiteSpace(options.Search)
                ? Fail($"No sessions found for backend '{backend.Kind.ToOptionValue()}'")
                : Fail($"No sessions found for backend '{backend.Kind.ToOptionValue()}' with search '{options.Search}'.");
        }

        var payload = new JsonArray();
        foreach (var session in latest)
        {
            payload.Add(new JsonObject
            {
                ["sessionID"] = session.SessionId,
                ["backend"] = session.Backend.ToOptionValue(),
                ["backendSessionID"] = session.BackendSessionId,
                ["directory"] = session.Directory
            });
        }

        if (!options.All)
        {
            WriteJson(payload[0]);
            return 0;
        }

        WriteJson(payload);
        return 0;
    }

    private static async Task<int> MessagesViaBackend(
        BackendCommandService commands,
        Options options)
    {
        var limit = options.Limit > 0 ? options.Limit : 20;
        var messages = await commands.GetMessagesAsync(Require(options.Session, "--session"), limit);
        var output = new JsonArray();
        foreach (var message in messages)
        {
            output.Add(BuildBackendMessageJson(message));
        }

        WriteJson(output);
        return 0;
    }

    private static async Task<int> WaitViaBackend(
        BackendCommandService commands,
        Options options)
    {
        if (options.TimeoutWasProvided)
        {
            return Fail("wait does not accept --timeout. It passively waits until the backend reports an idle status.");
        }

        var state = await commands.WaitUntilIdleAsync(Require(options.Session, "--session"));
        WriteJson(BuildSessionStateJson(state));
        return 0;
    }

    private static async Task<int> AbortViaBackend(
        BackendCommandService commands,
        ISessionBackend backend,
        Options options)
    {
        var abort = await commands.AbortAsync(Require(options.Session, "--session"));
        var result = abort.Result;
        if (!result.IsSuccess)
        {
            return Fail(result.Error ?? result.Message ?? "Abort command failed.");
        }

        WriteJson(new JsonObject
        {
            ["sessionID"] = abort.Session.SessionId,
            ["backend"] = backend.Kind.ToOptionValue(),
            ["status"] = result.Message ?? "aborted"
        });
        return 0;
    }

    private static async Task<int> Ask(OpenCodeClient client, Options options)
    {
        var prompt = await ReadPrompt(options);
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required. Use --prompt or --prompt-file.");

        var session = options.Session;
        if (string.IsNullOrWhiteSpace(session))
        {
            var title = options.Title ?? ShortTitle(prompt);
            var createBody = new JsonObject { ["title"] = title };
            var created = await client.PostJson(WithDirectory("session", options), createBody);
            session = created?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Session create response did not include id.");
        }

        var anchorIndex = string.IsNullOrWhiteSpace(options.Session)
            ? -1
            : await LatestMessageIndex(client, session, options);
        var body = PromptBody(prompt, options);
        if (options.NoReply)
        {
            _ = await client.PostJson(WithDirectory($"session/{session}/message", options), body);
        }
        else
        {
            await client.PostNoContent(WithDirectory($"session/{session}/prompt_async", options), body);
        }

        if (!options.NoReply && (!options.Async || options.Wait))
        {
            await WaitForCompletion(client, session, options, TimeSpan.FromSeconds(options.TimeoutSeconds), anchorIndex);
        }

        var summary = options.Async && !options.Wait
            ? null
            : await FindLastAssistantSummary(client, session, options, anchorIndex);
        if (!options.NoReply && !options.Async && summary is null)
        {
            return Fail($"No assistant summary found for session {session} using marker '{options.SummaryMarker}'.");
        }

        var output = new JsonObject
        {
            ["sessionID"] = session,
            ["summaryFreshAfterLatestPrompt"] = summary is not null,
            ["summary"] = summary?.Text,
            ["messageID"] = summary?.MessageId,
            ["partID"] = summary?.PartId
        };
        WriteJson(output);
        return 0;
    }

    private static async Task<int> AskViaBackend(
        BackendCommandService commands,
        ISessionBackend backend,
        Options options)
    {
        var prompt = await ReadPrompt(options);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Fail("Prompt is required. Use --prompt or --prompt-file.");
        }

        var request = BuildBackendPromptRequest(options, prompt);
        var result = await commands.AskAsync(new BackendAskRequest(
            options.Session,
            options.Title,
            options.Parent,
            options.Directory,
            request,
            options.Async,
            options.Wait,
            TimeSpan.FromSeconds(options.TimeoutSeconds)));
        var postResult = result.PostResult;
        if (!postResult.IsSuccess)
        {
            var error = postResult.Error is null ? postResult.Message : $"{postResult.Message}: {postResult.Error}";
            return Fail(error ?? "Prompt failed without details.");
        }

        var summary = result.Summary;
        if (!options.NoReply && !options.Async && summary is null)
        {
            return Fail($"No assistant summary found for session {result.Session.SessionId} using marker '{options.SummaryMarker}'.");
        }

        var output = new JsonObject
        {
            ["sessionID"] = result.Session.SessionId,
            ["summaryFreshAfterLatestPrompt"] = summary is not null,
            ["summary"] = summary?.Text,
            ["messageID"] = summary?.MessageId,
            ["partID"] = summary?.PartId,
            ["backend"] = backend.Kind.ToOptionValue()
        };
        WriteJson(output);
        return 0;
    }

    private static async Task<int> StatusViaBackend(
        BackendCommandService commands,
        ISessionBackend backend,
        Options options)
    {
        var states = await commands.GetStatusAsync(options.Session);
        if (states.Count == 0)
        {
            return Fail($"No sessions found for backend '{backend.Kind.ToOptionValue()}'.");
        }

        if (string.IsNullOrWhiteSpace(options.Session))
        {
            var payload = new JsonArray();
            foreach (var state in states)
            {
                payload.Add(BuildSessionStateJson(state));
            }

            WriteJson(payload);
            return 0;
        }

        var details = BuildSessionStateJson(states[0]);
        details["hasFreshSummary"] = states[0].HasFreshSummary;
        WriteJson(details);
        return 0;
    }

    private static async Task<int> LastSummaryViaBackend(
        BackendCommandService commands,
        ISessionBackend backend,
        Options options)
    {
        var sessionId = Require(options.Session, "--session");
        var summary = await commands.GetLastSummaryAsync(sessionId, options.SummaryMarker);
        if (summary is null)
        {
            return Fail($"No fresh assistant summary found for session {sessionId} after the latest user prompt using marker '{options.SummaryMarker}'. Older historical handoffs are ignored.");
        }

        if (options.Plain)
        {
            Console.WriteLine(summary.Text);
        }
        else
        {
            WriteJson(new JsonObject
            {
                ["sessionID"] = summary.SessionId,
                ["backend"] = backend.Kind.ToOptionValue(),
                ["summaryFreshAfterLatestPrompt"] = true,
                ["messageID"] = summary.MessageId,
                ["partID"] = summary.PartId,
                ["summary"] = summary.Text
            });
        }

        return 0;
    }

    private static JsonObject BuildBackendMessageJson(BackendMessage message)
    {
        return new JsonObject
        {
            ["id"] = message.Id,
            ["role"] = message.Role,
            ["text"] = message.Text,
            ["partId"] = message.PartId,
            ["timestamp"] = message.Timestamp?.ToString("O")
        };
    }

    private static PromptRequest BuildBackendPromptRequest(Options options, string prompt)
    {
        var source = !string.IsNullOrWhiteSpace(options.Prompt)
            ? PromptSourceKind.Inline
            : !string.IsNullOrWhiteSpace(options.PromptFile)
                ? PromptSourceKind.File
                : Console.IsInputRedirected
                    ? PromptSourceKind.Stdin
                    : PromptSourceKind.Inline;

        return new PromptRequest(
            Text: prompt,
            SourceKind: source,
            SourceLocation: source == PromptSourceKind.File ? options.PromptFile : null,
            ModelProvider: options.ResolvedModelProvider,
            Model: options.ResolvedModel,
            Variant: options.ResolvedVariant,
            SummaryMarker: options.SummaryMarker,
            Directory: options.Directory,
            Agent: options.ResolvedAgent,
            System: options.ResolvedSystem,
            NoReply: options.NoReply,
            Raw: options.Raw,
            Options: BuildBackendPromptOptions(options));
    }

    private static System.Collections.Immutable.ImmutableDictionary<string, string> BuildBackendPromptOptions(Options options)
    {
        var builder = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.CopilotAllowTools.Count > 0)
        {
            builder["copilot.allowTool"] = string.Join(';', options.CopilotAllowTools);
        }

        if (options.CopilotAllowUrls.Count > 0)
        {
            builder["copilot.allowUrl"] = string.Join(';', options.CopilotAllowUrls);
        }

        if (options.CopilotAllowAll)
        {
            builder["copilot.allowAll"] = "true";
        }

        return builder.ToImmutable();
    }

    private static JsonObject BuildSessionStateJson(SessionStateSnapshot state)
    {
        var payload = new JsonObject
        {
            ["sessionID"] = state.SessionId,
            ["sessionBackendID"] = state.BackendSessionId,
            ["status"] = state.EffectiveStatus,
            ["apiStatus"] = state.ApiStatus,
            ["derivedStatus"] = state.DerivedStatus,
            ["messageCount"] = state.MessageCount,
            ["latestUserMessageID"] = state.LatestUserMessageId,
            ["latestAssistantMessageID"] = state.LatestAssistantMessageId,
            ["hasFreshSummary"] = state.HasFreshSummary
        };

        return payload;
    }

    private static JsonObject PromptBody(string prompt, Options options)
    {
        var fullPrompt = options.Raw ? prompt : BuildHarnessPrompt(prompt, options);
        var body = new JsonObject
        {
            ["parts"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = fullPrompt
            })
        };

        AddPromptMetadata(body, options);
        return body;
    }

    private static string BuildHarnessPrompt(string prompt, Options options)
    {
        return PromptTemplates.Render("delegation/opencode.md", new Dictionary<string, string>
        {
            ["task"] = prompt,
            ["summary_marker"] = options.SummaryMarker
        });
    }

    private static async Task<int> Status(OpenCodeClient client, Options options)
    {
        if (string.IsNullOrWhiteSpace(options.Session))
        {
            WriteJson(await client.GetJson("session/status"));
            return 0;
        }

        var state = await GetSessionState(client, options.Session, options);
        WriteJson(new JsonObject
        {
            ["sessionID"] = options.Session,
            ["status"] = state.EffectiveStatus,
            ["apiStatus"] = state.ApiStatus,
            ["derivedStatus"] = state.DerivedStatus,
            ["messageCount"] = state.MessageCount,
            ["latestUserMessageID"] = state.LatestUserMessageId,
            ["latestAssistantMessageID"] = state.LatestAssistantMessageId,
            ["hasFreshSummary"] = state.FreshSummary is not null
        });
        return 0;
    }

    private static async Task<int> Wait(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        if (options.TimeoutWasProvided) throw new ArgumentException("wait does not accept --timeout. It passively waits until OpenCode reports the session is idle; press Ctrl+C to stop.");

        var state = await WaitPassivelyUntilIdle(client, session, options);
        WriteJson(new JsonObject
        {
            ["sessionID"] = session,
            ["status"] = state.EffectiveStatus,
            ["apiStatus"] = state.ApiStatus,
            ["derivedStatus"] = state.DerivedStatus,
            ["idle"] = true,
            ["messageCount"] = state.MessageCount,
            ["latestUserMessageID"] = state.LatestUserMessageId,
            ["latestAssistantMessageID"] = state.LatestAssistantMessageId,
            ["hasFreshSummary"] = state.FreshSummary is not null
        });
        return 0;
    }

    private static async Task<int> Messages(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        var limit = options.Limit > 0 ? options.Limit : 20;
        WriteJson(await GetMessages(client, session, options, limit));
        return 0;
    }

    private static async Task<int> LastSummary(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        var summary = await FindLastAssistantSummary(client, session, options);
        if (summary is null)
        {
            return Fail($"No fresh assistant summary found for session {session} after the latest user prompt using marker '{options.SummaryMarker}'. Older historical handoffs are ignored.");
        }

        if (options.Plain)
        {
            Console.WriteLine(summary.Text);
        }
        else
        {
            WriteJson(new JsonObject
            {
                ["sessionID"] = session,
                ["summaryFreshAfterLatestPrompt"] = true,
                ["messageID"] = summary.MessageId,
                ["partID"] = summary.PartId,
                ["summary"] = summary.Text
            });
        }
        return 0;
    }

    private static async Task<int> Abort(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        var result = await client.PostEmpty($"session/{session}/abort");
        WriteJson(result);
        return 0;
    }

    private static async Task<int> Events(HttpClient http, Options options)
    {
        var timeout = options.TimeoutWasProvided ? options.TimeoutSeconds : 30;
        var limit = options.Limit > 0 ? options.Limit : 10;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var request = new HttpRequestMessage(HttpMethod.Get, "event");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var count = 0;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                Console.WriteLine(line[6..]);
                count++;
                if (count >= limit) break;
            }
        }
        catch (OperationCanceledException) when (count > 0)
        {
            return 0;
        }
        return 0;
    }

    private static async Task<int> Tail(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        var limit = options.Limit > 0 ? options.Limit : 20;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var messages = await GetMessages(client, session, options, limit);
            foreach (var line in FormatMessageLines(messages))
            {
                if (!seen.Add(line.Id)) continue;
                Console.WriteLine(line.Text);
            }

            if (options.Once) return 0;
            await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds));
        }
    }

    private static async Task<int> Export(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        var limit = options.Limit > 0 ? options.Limit : 0;
        var messages = await GetMessages(client, session, options, limit);
        var status = await GetSessionStatus(client, session);
        var summary = await FindLastAssistantSummary(client, session, options);
        var exportedAt = DateTimeOffset.Now.ToString("O");

        if (options.Format.Equals("md", StringComparison.OrdinalIgnoreCase) ||
            options.Format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            var markdown = BuildMarkdownExport(session, status, exportedAt, summary, messages);
            await WriteOrPrint(markdown, options);
            return 0;
        }

        if (!options.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--format must be json or md.");
        }

        var output = new JsonObject
        {
            ["sessionID"] = session,
            ["status"] = status,
            ["exportedAt"] = exportedAt,
            ["summary"] = summary is null
                ? null
                : new JsonObject
                {
                    ["messageID"] = summary.MessageId,
                    ["partID"] = summary.PartId,
                    ["text"] = summary.Text
                },
            ["messages"] = messages?.DeepClone()
        };
        await WriteOrPrint(output.ToJsonString(JsonOptions), options);
        return 0;
    }

    private static async Task<int> Watch(OpenCodeClient client, Options options)
    {
        var session = Require(options.Session, "--session");
        return await WatchSessions(client, [session], options);
    }

    private static async Task<int> WatchMany(OpenCodeClient client, Options options)
    {
        if (options.Sessions.Count == 0) throw new ArgumentException("At least one --session is required.");
        return await WatchSessions(client, options.Sessions, options);
    }

    private static async Task<int> WatchSessions(OpenCodeClient client, IReadOnlyList<string> sessions, Options options)
    {
        var interval = TimeSpan.FromMinutes(options.IntervalMinutes);
        var maxRuns = options.MaxRuns ?? (options.Once ? 1 : null);
        var deadline = options.MaxDurationMinutes is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.UtcNow.AddMinutes(options.MaxDurationMinutes.Value);
        var prompt = await ReadPrompt(options);
        if (string.IsNullOrWhiteSpace(prompt)) prompt = DefaultWatchPrompt();

        Console.WriteLine($"Sending supervision prompts to {sessions.Count} OpenCode session(s) every {interval.TotalMinutes:N0} minute(s). Press Ctrl+C to stop.");
        Console.WriteLine($"Server: {NormalizeServer(options.Server).TrimEnd('/')}");
        if (!string.IsNullOrWhiteSpace(options.Directory)) Console.WriteLine($"Directory: {Path.GetFullPath(options.Directory)}");
        if (options.UntilIdle) Console.WriteLine("Note: --until-idle waits after sending a prompt; it is not a passive wait for existing work.");

        var run = 0;
        while (deadline is null || DateTimeOffset.UtcNow < deadline.Value)
        {
            run++;
            var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                anchors[session] = await SendWatchPrompt(client, session, options, prompt);
            }

            if (options.UntilIdle)
            {
                foreach (var session in sessions)
                {
                    await PollUntilIdleAfterPrompt(client, session, options, RemainingTimeout(deadline, options), anchors[session]);
                }
                return 0;
            }

            if (maxRuns is not null && run >= maxRuns.Value) return 0;

            var nextDelay = RemainingDelay(interval, deadline);
            if (nextDelay <= TimeSpan.Zero) return 0;
            await WaitWithCountdown(nextDelay);
        }

        return 0;
    }

    private static async Task<int> SendWatchPrompt(OpenCodeClient client, string session, Options options, string prompt)
    {
        var body = RawPromptBody(prompt, options);
        var anchorIndex = options.DryRun ? -1 : await LatestMessageIndex(client, session, options);

        if (options.DryRun)
        {
            Console.WriteLine($"[{Timestamp()}] DRY RUN: would send watch prompt to {session}.");
            Console.WriteLine(prompt);
            return anchorIndex;
        }

        Console.WriteLine($"[{Timestamp()}] Sending watch prompt to {session}...");
        await client.PostNoContent(WithDirectory($"session/{session}/prompt_async", options), body);
        return anchorIndex;
    }

    private static TimeSpan RemainingDelay(TimeSpan interval, DateTimeOffset? deadline)
    {
        if (deadline is null) return interval;
        var remaining = deadline.Value - DateTimeOffset.UtcNow;
        return remaining < interval ? remaining : interval;
    }

    private static TimeSpan RemainingTimeout(DateTimeOffset? deadline, Options options)
    {
        if (deadline is null) return TimeSpan.FromSeconds(options.TimeoutSeconds);
        var remaining = deadline.Value - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
    }

    private static JsonObject RawPromptBody(string prompt, Options options)
    {
        var body = new JsonObject
        {
            ["parts"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = prompt
                }
            }
        };

        AddPromptMetadata(body, options);
        return body;
    }

    private static void AddPromptMetadata(JsonObject body, Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.ResolvedAgent)) body["agent"] = options.ResolvedAgent;
        if (!string.IsNullOrWhiteSpace(options.ResolvedSystem)) body["system"] = options.ResolvedSystem;
        if (options.NoReply) body["noReply"] = true;
        if (!string.IsNullOrWhiteSpace(options.ResolvedModel))
        {
            body["model"] = new JsonObject
            {
                ["providerID"] = options.ResolvedModelProvider,
                ["modelID"] = options.ResolvedModel
            };
            if (!string.IsNullOrWhiteSpace(options.ResolvedVariant)) body["variant"] = options.ResolvedVariant;
        }
        else if (!string.IsNullOrWhiteSpace(options.ResolvedVariant))
        {
            body["variant"] = options.ResolvedVariant;
        }
    }

    private static async Task WaitWithCountdown(TimeSpan delay)
    {
        var deadline = DateTimeOffset.Now.Add(delay);
        while (DateTimeOffset.Now < deadline)
        {
            var remaining = deadline - DateTimeOffset.Now;
            var text = $"Next watch prompt in {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}. Press Ctrl+C to stop.";
            Console.Write('\r');
            Console.Write(text.PadRight(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : text.Length));
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine();
    }

    private static string DefaultWatchPrompt()
    {
        return PromptTemplates.Render("watch/default.md", new Dictionary<string, string>());
    }

    private static async Task<SessionState> PollUntilIdleAfterPrompt(OpenCodeClient client, string session, Options options, TimeSpan timeout, int anchorIndex)
    {
        _ = await client.GetJson(WithDirectory($"session/{session}", options));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var observedNonIdle = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await GetSessionState(client, session, options, anchorIndex);
            if (state.ApiStatus is not null && state.ApiStatus.StartsWith("error:", StringComparison.Ordinal)) throw new InvalidOperationException(state.ApiStatus);
            if (state.ApiStatus is not null and not "idle") observedNonIdle = true;
            if (state.ApiStatus is null or "idle")
            {
                var promptWasHandled = observedNonIdle || state.HasAssistantAfterAnchor;
                if (promptWasHandled) return state;
            }
            await Task.Delay(1000);
        }

        throw new TimeoutException($"Session {session} did not become idle within {timeout.TotalSeconds:N0}s. Inspect `tail`, `messages`, or `status` for in-progress work.");
    }

    private static async Task<SessionState> WaitPassivelyUntilIdle(OpenCodeClient client, string session, Options options)
    {
        _ = await client.GetJson(WithDirectory($"session/{session}", options));
        while (true)
        {
            var state = await GetSessionState(client, session, options);
            if (state.ApiStatus is not null && state.ApiStatus.StartsWith("error:", StringComparison.Ordinal)) throw new InvalidOperationException(state.ApiStatus);
            if (state.ApiStatus is null or "idle") return state;
            await Task.Delay(1000);
        }
    }

    private static async Task WaitForCompletion(OpenCodeClient client, string session, Options options, TimeSpan timeout, int? anchorIndex = null)
    {
        _ = await client.GetJson(WithDirectory($"session/{session}", options));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await GetSessionState(client, session, options, anchorIndex);
            if (state.ApiStatus is not null && state.ApiStatus.StartsWith("error:", StringComparison.Ordinal)) throw new InvalidOperationException(state.ApiStatus);
            if (state.FreshSummary is not null) return;

            await Task.Delay(1000);
        }

        throw new TimeoutException($"Session {session} did not produce a fresh final handoff after the latest prompt within {timeout.TotalSeconds:N0}s. Older historical handoffs were ignored; inspect `tail` or `messages` for in-progress work.");
    }

    private static async Task<string?> GetSessionStatus(OpenCodeClient client, string session)
    {
        var map = await client.GetJson("session/status");
        var node = map?[session];
        if (node is null) return null;
        var type = node["type"]?.GetValue<string>();
        if (type == "retry") return $"retry:{node["message"]?.GetValue<string>()}";
        return type;
    }

    private static async Task<SessionState> GetSessionState(OpenCodeClient client, string session, Options options, int? anchorIndex = null)
    {
        var messages = await GetMessages(client, session, options);
        var apiStatus = await GetSessionStatus(client, session);
        return BuildSessionState(messages, apiStatus, options.SummaryMarker, anchorIndex ?? LatestUserMessageIndex(messages));
    }

    private static SessionState BuildSessionState(JsonNode? messages, string? apiStatus, string summaryMarker, int anchorIndex)
    {
        var freshSummary = FindLastAssistantSummary(messages, summaryMarker, anchorIndex);
        var latestUser = LatestMessageId(messages, "user");
        var latestAssistant = LatestMessageId(messages, "assistant");
        var messageCount = MessageCount(messages);
        var hasAssistantAfterAnchor = HasAssistantAfter(messages, anchorIndex);
        var derivedStatus = freshSummary is not null
            ? "fresh-summary"
            : hasAssistantAfterAnchor
                ? "assistant-after-latest-user-without-handoff"
                : "awaiting-assistant-after-latest-user";
        var effectiveStatus = apiStatus ?? "idle";

        return new SessionState(apiStatus, derivedStatus, effectiveStatus, messageCount, latestUser, latestAssistant, hasAssistantAfterAnchor, freshSummary);
    }

    private static async Task<Summary?> FindLastAssistantSummary(OpenCodeClient client, string session, Options options)
    {
        var messages = await GetMessages(client, session, options);
        return FindLastAssistantSummary(messages, options.SummaryMarker, LatestUserMessageIndex(messages));
    }

    private static async Task<Summary?> FindLastAssistantSummary(OpenCodeClient client, string session, Options options, int anchorIndex)
    {
        var messages = await GetMessages(client, session, options);
        return FindLastAssistantSummary(messages, options.SummaryMarker, anchorIndex);
    }

    private static Task<JsonNode?> GetMessages(OpenCodeClient client, string session, Options options, int limit = 0)
    {
        var path = $"session/{session}/message";
        if (limit > 0) path += $"?limit={limit}";
        return client.GetJson(WithDirectory(path, options));
    }

    private static async Task<int> LatestMessageIndex(OpenCodeClient client, string session, Options options)
    {
        var messages = await GetMessages(client, session, options);
        return MessageCount(messages) - 1;
    }

    private static int MessageCount(JsonNode? messages) => messages is JsonArray array ? array.Count : 0;

    private static int LatestUserMessageIndex(JsonNode? messages)
    {
        if (messages is not JsonArray array) return -1;
        for (var index = array.Count - 1; index >= 0; index--)
        {
            if (array[index]?["info"]?["role"]?.GetValue<string>() == "user") return index;
        }
        return -1;
    }

    private static string? LatestMessageId(JsonNode? messages, string role)
    {
        if (messages is not JsonArray array) return null;
        for (var index = array.Count - 1; index >= 0; index--)
        {
            var info = array[index]?["info"]?.AsObject();
            if (info?["role"]?.GetValue<string>() == role) return info["id"]?.GetValue<string>();
        }
        return null;
    }

    private static bool HasAssistantAfter(JsonNode? messages, int anchorIndex)
    {
        if (messages is not JsonArray array) return false;
        for (var index = Math.Max(0, anchorIndex + 1); index < array.Count; index++)
        {
            if (array[index]?["info"]?["role"]?.GetValue<string>() == "assistant") return true;
        }
        return false;
    }

    private static Summary? FindLastAssistantSummary(JsonNode? messages, string marker, int anchorIndex = -1)
    {
        if (messages is not JsonArray array) return null;

        for (var messageIndex = array.Count - 1; messageIndex >= 0; messageIndex--)
        {
            if (messageIndex <= anchorIndex) break;
            var item = array[messageIndex]?.AsObject();
            if (item is null) continue;
            var info = item["info"]?.AsObject();
            if (info?["role"]?.GetValue<string>() != "assistant") continue;

            var parts = item["parts"]?.AsArray();
            if (parts is null) continue;
            for (var partIndex = parts.Count - 1; partIndex >= 0; partIndex--)
            {
                var part = parts[partIndex]?.AsObject();
                if (part?["type"]?.GetValue<string>() != "text") continue;
                var text = part["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var markerIndex = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0) continue;
                var summary = text[(markerIndex + marker.Length)..].Trim();
                if (string.IsNullOrWhiteSpace(summary)) continue;
                return new Summary(
                    info["id"]?.GetValue<string>() ?? string.Empty,
                    part["id"]?.GetValue<string>() ?? string.Empty,
                    summary);
            }
        }

        return null;
    }

    private static async Task<string> ReadPrompt(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.Prompt)) return options.Prompt;
        if (!string.IsNullOrWhiteSpace(options.PromptFile))
        {
            if (!File.Exists(options.PromptFile)) throw new ArgumentException($"--prompt-file not found: {options.PromptFile}");
            return await File.ReadAllTextAsync(options.PromptFile);
        }
        if (Console.IsInputRedirected) return await Console.In.ReadToEndAsync();
        return string.Empty;
    }

    private static IEnumerable<MessageLine> FormatMessageLines(JsonNode? messages)
    {
        if (messages is not JsonArray array) yield break;

        foreach (var item in array)
        {
            var info = item?["info"]?.AsObject();
            if (info is null) continue;
            var role = info["role"]?.GetValue<string>() ?? "unknown";
            var messageId = info["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var parts = item?["parts"]?.AsArray();
            if (parts is null) continue;

            foreach (var part in parts)
            {
                if (part?["type"]?.GetValue<string>() != "text") continue;
                var partId = part["id"]?.GetValue<string>() ?? string.Empty;
                var text = part["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text)) continue;
                yield return new MessageLine(
                    messageId + ":" + partId,
                    $"[{role}] {CompactText(text)}");
            }
        }
    }

    private static string BuildMarkdownExport(string session, string? status, string exportedAt, Summary? summary, JsonNode? messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# OpenCode Session Export: {session}");
        builder.AppendLine();
        builder.AppendLine($"- Status: {status ?? "unknown"}");
        builder.AppendLine($"- Exported: {exportedAt}");
        builder.AppendLine();

        if (summary is not null)
        {
            builder.AppendLine("## Final Summary");
            builder.AppendLine();
            builder.AppendLine(summary.Text.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("## Messages");
        builder.AppendLine();
        foreach (var line in FormatMessageLines(messages))
        {
            builder.AppendLine(line.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static async Task WriteOrPrint(string content, Options options)
    {
        if (string.IsNullOrWhiteSpace(options.Output))
        {
            Console.WriteLine(content);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Output));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(options.Output, content, Encoding.UTF8);
        Console.WriteLine(options.Output);
    }

    private static string CompactText(string text)
    {
        var compact = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 500 ? compact : compact[..500] + "...";
    }

    private static string Timestamp() => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static int SelfTest()
    {
        var messages = JsonNode.Parse("""
[
  {
    "info": { "id": "msg_user", "role": "user" },
    "parts": [{ "id": "part_user", "type": "text", "text": "FINAL HANDOFF\nwrong" }]
  },
  {
    "info": { "id": "msg_old", "role": "assistant" },
    "parts": [{ "id": "part_old", "type": "text", "text": "FINAL HANDOFF\nold" }]
  },
  {
    "info": { "id": "msg_new", "role": "assistant" },
    "parts": [
      { "id": "part_meta", "type": "reasoning", "text": "FINAL HANDOFF\nignore" },
      { "id": "part_new", "type": "text", "text": "Notes\nfinal handoff\nnew summary" }
    ]
  }
]
""");

        var summary = FindLastAssistantSummary(messages, "FINAL HANDOFF", -1);
        if (summary is null) return Fail("self-test failed: expected a summary.");
        if (summary.MessageId != "msg_new") return Fail($"self-test failed: expected msg_new, got {summary.MessageId}.");
        if (summary.PartId != "part_new") return Fail($"self-test failed: expected part_new, got {summary.PartId}.");
        if (summary.Text != "new summary") return Fail($"self-test failed: expected extracted summary, got '{summary.Text}'.");

        var noMarker = FindLastAssistantSummary(JsonNode.Parse("""
[
  {
    "info": { "id": "msg", "role": "assistant" },
    "parts": [{ "id": "part", "type": "text", "text": "No marker here" }]
  }
]
"""), "FINAL HANDOFF", -1);
        if (noMarker is not null) return Fail("self-test failed: no-marker payload should not produce a summary.");

        var resumedMessages = JsonNode.Parse("""
[
  {
    "info": { "id": "msg_user_wrong", "role": "user" },
    "parts": [{ "id": "part_user_wrong", "type": "text", "text": "Wrong repo context" }]
  },
  {
    "info": { "id": "msg_old_handoff", "role": "assistant" },
    "parts": [{ "id": "part_old_handoff", "type": "text", "text": "FINAL HANDOFF\nwrong repo" }]
  },
  {
    "info": { "id": "msg_user_corrected", "role": "user" },
    "parts": [{ "id": "part_user_corrected", "type": "text", "text": "Use corrected repo" }]
  },
  {
    "info": { "id": "msg_progress", "role": "assistant" },
    "parts": [{ "id": "part_progress", "type": "text", "text": "Working in corrected repo" }]
  }
]
""");
        var correctedAnchor = LatestUserMessageIndex(resumedMessages);
        var staleSummary = FindLastAssistantSummary(resumedMessages, "FINAL HANDOFF", correctedAnchor);
        if (staleSummary is not null) return Fail("self-test failed: stale historical summary should be ignored after newer user prompt.");
        if (!HasAssistantAfter(resumedMessages, correctedAnchor)) return Fail("self-test failed: expected assistant progress after corrected prompt.");

        var idleStateWithoutSummary = BuildSessionState(
            resumedMessages,
            apiStatus: null,
            "FINAL HANDOFF",
            correctedAnchor);
        if (idleStateWithoutSummary.ApiStatus is not null) return Fail("self-test failed: missing API status should remain null.");
        if (idleStateWithoutSummary.EffectiveStatus != "idle") return Fail($"self-test failed: missing API status should mean idle, got '{idleStateWithoutSummary.EffectiveStatus}'.");
        if (idleStateWithoutSummary.DerivedStatus != "assistant-after-latest-user-without-handoff") return Fail($"self-test failed: expected derived progress without handoff, got '{idleStateWithoutSummary.DerivedStatus}'.");
        if (idleStateWithoutSummary.FreshSummary is not null) return Fail("self-test failed: idle state should not invent a fresh summary.");

        var freshMessages = JsonNode.Parse("""
[
  {
    "info": { "id": "msg_user_wrong", "role": "user" },
    "parts": [{ "id": "part_user_wrong", "type": "text", "text": "Wrong repo context" }]
  },
  {
    "info": { "id": "msg_old_handoff", "role": "assistant" },
    "parts": [{ "id": "part_old_handoff", "type": "text", "text": "FINAL HANDOFF\nwrong repo" }]
  },
  {
    "info": { "id": "msg_user_corrected", "role": "user" },
    "parts": [{ "id": "part_user_corrected", "type": "text", "text": "Use corrected repo" }]
  },
  {
    "info": { "id": "msg_new_handoff", "role": "assistant" },
    "parts": [{ "id": "part_new_handoff", "type": "text", "text": "FINAL HANDOFF\nfresh repo" }]
  }
]
""");
        var freshAnchor = LatestUserMessageIndex(freshMessages);
        var freshSummary = FindLastAssistantSummary(freshMessages, "FINAL HANDOFF", freshAnchor);
        if (freshSummary?.Text != "fresh repo") return Fail($"self-test failed: expected fresh repo summary, got '{freshSummary?.Text}'.");

        var metadataOptions = new Options();
        metadataOptions.ApplyForSelfTest(new Dictionary<string, string?>
        {
            ["Agent"] = "build",
            ["Model"] = "github-copilot/gpt-5.5/high",
            ["System"] = "extra system",
            ["NoReply"] = "true"
        });
        metadataOptions.ApplyResolvedProfile(ResolveAgentProfile(metadataOptions));
        var rawBody = RawPromptBody("watch prompt", metadataOptions);
        if (rawBody["agent"]?.GetValue<string>() != "build") return Fail("self-test failed: raw watch prompt lost agent metadata.");
        if (rawBody["system"]?.GetValue<string>() != "extra system") return Fail("self-test failed: raw watch prompt lost system metadata.");
        if (rawBody["noReply"]?.GetValue<bool>() != true) return Fail("self-test failed: raw watch prompt lost noReply metadata.");
        if (rawBody["variant"]?.GetValue<string>() != "high") return Fail("self-test failed: raw watch prompt lost model variant metadata.");
        var rawModel = rawBody["model"]?.AsObject();
        if (rawModel?["providerID"]?.GetValue<string>() != "github-copilot") return Fail("self-test failed: raw watch prompt lost provider metadata.");
        if (rawModel?["modelID"]?.GetValue<string>() != "gpt-5.5") return Fail("self-test failed: raw watch prompt lost model metadata.");

        WriteJson(new JsonObject { ["status"] = "passed" });
        return 0;
    }

    private static string ShortTitle(string prompt)
    {
        var firstLine = prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Delegated task";
        return firstLine.Length <= 80 ? firstLine : firstLine[..80];
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
        return value;
    }

    private static void WriteJson(JsonNode? node)
    {
        Console.WriteLine(node?.ToJsonString(JsonOptions) ?? "null");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var target = response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "the target OpenCode server";
            throw new HttpRequestException(
                $"401 Unauthorized from {target}. This usually means the target OpenCode server requires HTTP Basic auth. " +
                "If OPENCODE_SERVER_USERNAME or OPENCODE_SERVER_PASSWORD are set in your shell, prefer `ensure-server` so the child `opencode serve` process starts without inherited auth, or start `opencode serve` separately and attach to it.",
                null,
                response.StatusCode);
        }
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
harness-cli - deterministic helper for delegated agent sessions

Goal:
  Start delegated backend sessions, force a final handoff summary, fetch only
  that summary when needed, and keep lightweight work-map records for complex
  multi-agent coordination.

Golden path:
  harness-cli ensure-server --hostname 0.0.0.0 --port 4096 --print-logs
  harness-cli ask --timeout 900 --model github-copilot/gpt-5.4-mini --variant low --prompt-file task.md
  harness-cli last-summary --session ses_... --plain

Command help:
  harness-cli help watch
  harness-cli watch --help
  harness-cli watch -h

Compatibility:
  opencode-harness-cli is a migration alias for the same command when installed
  by the workspace plugin or emitted by dotnet publish.

Usage:
  harness-cli ensure-server [--server http://127.0.0.1:4096] [--hostname 0.0.0.0] [--port 4096]
  harness-cli health [--server URL]
  harness-cli self-test
  harness-cli new --title TITLE [--parent ses_...]
  harness-cli spawn --target TARGET [--target TARGET...] [--model provider/model] [--directory PATH]
  harness-cli latest [--search TEXT] [--limit 20]
  harness-cli ask --prompt TEXT [--profile fast|cheap|deep] [--model provider/model] [--variant low] [--agent build] [--title TITLE]
  harness-cli ask --prompt-file task.md --timeout 600 --model github-copilot/gpt-5.4-mini
  harness-cli ask --prompt-file task.md --async --model github-copilot/gpt-5.4-mini
  harness-cli status [--session ses_...]
  harness-cli wait --session ses_...
  harness-cli last-summary --session ses_... [--plain]
  harness-cli messages --session ses_... [--limit 20]
  harness-cli tail --session ses_... [--limit 20] [--interval-seconds 5] [--once]
  harness-cli events [--limit 10] [--timeout 30]
  harness-cli abort --session ses_...
  harness-cli watch --session ses_... [--interval-minutes 60] [--until-idle]
  harness-cli watch-many --session ses_... --session ses_... [--until-idle]
  harness-cli export --session ses_... [--format json|md] [--output FILE]
  harness-cli work-map create --title TITLE [--intent TEXT]
  harness-cli work-map stream add --mission ID --name NAME [--clone PATH]
  harness-cli work-map launch --mission ID [--dry-run]
  harness-cli work-map supervise --mission ID [--launch-missing] [--until-idle]
  harness-cli work-map session run --mission ID --stream ID --backend codex --prompt-file task.md [--async] [--wait]
  harness-cli work-map session sync --mission ID --all
  harness-cli work-map session handoff --session ID --summary TEXT
  harness-cli work-map show --mission ID --format json|md|html

Model examples:
  --model github-copilot/gpt-5.4-mini        Fast delegated work; prefer this for most pseudo-subagents.
  --model github-copilot/gpt-5.5             Stronger delegated work when quality matters more than speed.

Variant/reasoning note:
  OpenCode's API calls this field variant. For GitHub Copilot GPT-5-family models,
  variants are reasoning levels. For other providers/models, variant can mean a
  different provider-specific mode. Prefer --variant for API accuracy; --reasoning
  is accepted as a GPT-5-friendly alias.

GPT-5-family variant examples verified from the live GitHub Copilot provider:
  --variant none      cheapest/simplest GPT-5.4-mini or GPT-5.5 calls; formatting, direct lookup, cheap probes
  --variant low       default recommendation for focused delegated research or summaries
  --variant medium    moderate debugging or cross-file synthesis
  --variant high      difficult debugging, architecture review, adversarial checks
  --variant xhigh     highest GPT-5.4/GPT-5.5 level; rare, for small but genuinely hard delegated tasks

GPT-5-family variant availability seen locally:
  github-copilot/gpt-5.4-mini: none, low, medium, high, xhigh
  github-copilot/gpt-5.5:      none, low, medium, high, xhigh

Common options:
  --backend VALUE      Backend selector. One of: opencode, codex, pi, copilot. Default: opencode.
  --engine VALUE       Alias for --backend.
  --server URL          OpenCode server URL. Default: http://127.0.0.1:4096
  --model provider/id   Model in provider/model format. Preferred: github-copilot/gpt-5.4-mini or github-copilot/gpt-5.5.
  --variant NAME        OpenCode provider-specific model variant. GPT-5 examples: none, low, medium, high, xhigh.
  --reasoning LEVEL     Alias for --variant, useful when choosing GPT-5 reasoning effort.
  --target TEXT         Target to launch with the spawn command. Repeat for multiple sessions.
  --session ID          Session id. Repeat for watch-many.
  --resume-session X=Y  For spawn, reuse existing session Y for target X instead of launching a duplicate.
  --resume-session-json JSON  For spawn, JSON object mapping target text to existing session id.
  --agent NAME          OpenCode agent name. Default is server default.
  --summary-marker TXT  Marker used to extract final handoff. Default: FINAL HANDOFF
  --format json|md      Export format. Default: json.
  --output FILE         Write export output to a file instead of stdout.
  --raw                 Send the prompt exactly as provided; disables the FINAL HANDOFF wrapper.
  --no-reply            Safe probe: add a user message without asking a model.
  --copilot-allow-tool VALUE  With --backend copilot, repeat to pass explicit --allow-tool values.
  --copilot-allow-url VALUE   With --backend copilot, repeat to pass explicit --allow-url values.
  --copilot-allow-all         With --backend copilot, pass --allow-all. Use sparingly.
  --async               Return immediately after queuing the prompt; use wait/status/last-summary later.
  --wait                With --async, wait for completion before extracting summary.
  --all                 With latest, print every returned matching session instead of only the first.
  --dry-run             Print what watch would send without calling OpenCode.
  --once                With watch, send one prompt and exit.
  --until-idle          With watch/watch-many, stop once prompted sessions are idle; not passive waiting.
  --interval-minutes N  With watch, wait N minutes between prompts. Default: 60.
  --interval-seconds N  With tail, wait N seconds between polls. Default: 5.
  --max-runs N          With watch/watch-many, stop after N prompt rounds.
  --max-duration-minutes N  With watch/watch-many, stop after N minutes.

Output contract:
  ask prints JSON: { sessionID, summary, messageID, partID }.
  Without --async, ask queues via /prompt_async, polls status/messages, and never waits
  on the model response stream itself.
  With --async, summary is usually null until you run last-summary.
  wait is a passive idle wait and does not send prompts or accept --timeout.
  last-summary prints either that JSON or only the summary with --plain.
  If --raw is not set, ask wraps the prompt and instructs the subagent to put
  its final answer under a line containing exactly FINAL HANDOFF.

Prompt input:
  --prompt TEXT         Inline prompt.
  --prompt-file FILE    Read prompt from a file.
  stdin                 If stdin is redirected, ask reads the prompt from stdin.

Bounded output defaults:
  messages              Defaults to --limit 20.
  tail                  Defaults to --limit 20 and --interval-seconds 5.
  events                Defaults to --limit 10 and --timeout 30.
  export                Defaults to full message history in JSON, or Markdown with --format md.

Server note:
  ensure-server strips OPENCODE_SERVER_PASSWORD and OPENCODE_SERVER_USERNAME
  only from newly started child server processes. If an already-running server
  returns 401, stop it or choose another port.

Run note:
  Raw `opencode run` can still fail in some environments when those same auth
  variables are exported in the parent shell, because its self-start/self-attach
  path may not reuse them correctly. Prefer `ensure-server` plus this CLI, or
  start `opencode serve` yourself and attach other clients to that server.
""");
    }

    private static bool IsHelpFlag(string value) => value is "-h" or "--help" or "help";

    private static int PrintCommandHelp(string command)
    {
        switch (command)
        {
            case "health": PrintHealthHelp(); return 0;
            case "ensure-server": PrintEnsureServerHelp(); return 0;
            case "self-test": PrintSelfTestHelp(); return 0;
            case "new": PrintNewHelp(); return 0;
            case "latest": PrintLatestHelp(); return 0;
            case "spawn": PrintSpawnHelp(); return 0;
            case "ask": PrintAskHelp(); return 0;
            case "status": PrintStatusHelp(); return 0;
            case "messages": PrintMessagesHelp(); return 0;
            case "last-summary": PrintLastSummaryHelp(); return 0;
            case "wait": PrintWaitHelp(); return 0;
            case "abort": PrintAbortHelp(); return 0;
            case "events": PrintEventsHelp(); return 0;
            case "watch": PrintWatchHelp(); return 0;
            case "watch-many": PrintWatchManyHelp(); return 0;
            case "tail": PrintTailHelp(); return 0;
            case "export": PrintExportHelp(); return 0;
            case "work-map": PrintWorkMapHelp(); return 0;
            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Run `harness-cli --help` for the command list.");
                return 1;
        }
    }

    private static void PrintWorkMapHelp() => Console.WriteLine("""
work-map - keep a lightweight mission graph for delegated agent work.

Usage:
  harness-cli work-map create --title TITLE [--intent TEXT] [--next-action TEXT]
  harness-cli work-map list [--format json|md]
  harness-cli work-map show --mission ID [--format json|md|html] [--output FILE]
  harness-cli work-map brief --mission ID --stream ID [--output FILE]
  harness-cli work-map launch --mission ID [--dry-run] [--force] [--include-complete] [--wait]
  harness-cli work-map supervise --mission ID [--launch-missing] [--until-idle] [--max-runs N]
  harness-cli work-map serve [--host HOST] [--port PORT] [--access-log FILE]
  harness-cli work-map store info
  harness-cli work-map store export [--output FILE]
  harness-cli work-map store import --file FILE [--force]
  harness-cli work-map mission update --mission ID [--status STATUS] [--next-action TEXT]
  harness-cli work-map stream add --mission ID --name NAME [--role TEXT] [--target TEXT] [--clone PATH]
  harness-cli work-map stream update --mission ID --stream ID [--status STATUS] [--integration-action TEXT]
  harness-cli work-map stream delete --mission ID --stream ID [--force]
  harness-cli work-map session link --mission ID --stream ID --session ID [--backend codex|copilot|manual|external] [--role TEXT]
  harness-cli work-map session run --mission ID --stream ID (--prompt TEXT | --prompt-file FILE) [--backend codex|copilot] [--async] [--wait]
  harness-cli work-map session sync --session ID [--message-limit N]
  harness-cli work-map session sync --mission ID --all [--message-limit N]
  harness-cli work-map session update --session ID [--status STATUS] [--display-name NAME]
  harness-cli work-map session archive --session ID [--summary TEXT]
  harness-cli work-map session handoff --session ID (--summary TEXT | --file FILE)
  harness-cli work-map session blocker set --session ID --summary TEXT [--evidence TEXT]
  harness-cli work-map session verify --session ID --kind KIND --result pass|fail|skip [--summary TEXT]
  harness-cli work-map evidence add --mission ID [--stream ID] [--session ID] --summary TEXT
  harness-cli work-map evidence remove --mission ID [--stream ID] [--session ID] --evidence-id ID

Notes:
  Work-map records are stored outside target repos by default under HARNESS_CLI_WORK_MAP_DIR
  or the platform app-data harness-cli/work-map directory.
  launch fans out from an existing map and uses Codex by default unless --backend overrides it.
  supervise syncs mission sessions and reports quiet, active, blocked, and handoff counts.
  store export/import writes portable JSON snapshots; the runtime store remains a JSON directory.
  session run links the work-map session before posting the backend prompt, so long-running
  workers are visible to show, supervise, and the observer UI while they are still active.
  session link/update accept manual external backend labels for non-harness workers such as
  shipper, background, human, or external coordinator sessions; sync skips those records.
  serve starts an optional read-only React observer UI over the same records and logs each
  request to stderr. Pass --access-log FILE to append durable JSONL access records.
  For Tailscale Serve without firewall changes, keep the default loopback bind and run
  `tailscale serve --bg http://127.0.0.1:4896/`. Use --host 0.0.0.0 only for direct
  access from another device on the same trusted network or Tailscale IP.
  The records describe missions, workstreams, clones, sessions, evidence, handoffs, blockers,
  and verification observations. They are not a workflow engine.
  Use clone/clone-path language for isolated agent work; this command does not create git worktrees.
  The html format is a static optional observer view over the same records.
""");

    private static void PrintHealthHelp() => Console.WriteLine("""
health - check whether the OpenCode HTTP server is reachable.

Usage:
  harness-cli health [--server URL]

Options:
  --server URL  OpenCode server URL. Default: http://127.0.0.1:4096

Examples:
  harness-cli health
  harness-cli health --server http://127.0.0.1:4096
""");

    private static void PrintEnsureServerHelp() => Console.WriteLine("""
ensure-server - start or reuse a local unauthenticated OpenCode server.

Usage:
  harness-cli ensure-server [--server URL] [--hostname HOST] [--port N] [--directory PATH] [--timeout SECONDS] [--print-logs] [--log-dir PATH] [--log-level LEVEL]

Options:
  --server URL       Server URL to check first. Default: http://127.0.0.1:4096
  --hostname HOST    Hostname for a newly started server. Common: 0.0.0.0 or 127.0.0.1
  --port N           Port for a newly started server. Default comes from --server or 4096.
  --directory PATH   Working directory for the child opencode serve process.
  --timeout SECONDS  Startup wait timeout. Default: 300.
  --print-logs       Ask opencode serve to print logs.
  --log-dir PATH     Directory for child server stdout/stderr logs. Default: temp opencode dir.
  --log-level LEVEL  Log level when --print-logs is set. Default: DEBUG.

Notes:
  Removes OPENCODE_SERVER_USERNAME and OPENCODE_SERVER_PASSWORD from the child server process so inherited Basic auth settings do not break local automation.

Examples:
  harness-cli ensure-server --hostname 0.0.0.0 --port 4096 --print-logs
  harness-cli ensure-server --directory E:\ --timeout 60
""");

    private static void PrintSelfTestHelp() => Console.WriteLine("""
self-test - run local parser tests without contacting OpenCode.

Usage:
  harness-cli self-test
""");

    private static void PrintNewHelp() => Console.WriteLine("""
new - create a new OpenCode session.

Usage:
  harness-cli new --title TITLE [--parent ses_...] [--directory PATH] [--server URL]

Options:
  --title TITLE     Session title.
  --parent ses_...  Optional parent session id.
  --directory PATH  Project directory associated with the session.

Examples:
  harness-cli new --title "scratch"
  harness-cli new --title "Ship: issue #5" --directory E:\work\baton
""");

    private static void PrintLatestHelp() => Console.WriteLine("""
latest - find recent sessions, optionally by title/search text.

Usage:
  harness-cli latest [--search TEXT] [--limit N] [--all] [--server URL]

Options:
  --search TEXT  Filter sessions by search text.
  --limit N      Maximum sessions to fetch. Default: 20.
  --all          Print all returned sessions instead of only the first.

Examples:
  harness-cli latest --search "Ship:"
  harness-cli latest --search "Ship:" --all --limit 20
""");

    private static void PrintAskHelp() => Console.WriteLine("""
ask - send a task prompt to a new or existing OpenCode session.

Usage:
  harness-cli ask (--prompt TEXT | --prompt-file FILE | stdin) [options]

Options:
  --session ses_...       Existing session. If omitted, a new session is created.
  --title TITLE           Title for a newly created session.
  --prompt TEXT           Inline prompt.
  --prompt-file FILE      Read prompt from a file.
  --profile NAME          Built-in backend/model profile: fast, cheap, or deep.
  --model provider/model  Model metadata for OpenCode, for example github-copilot/gpt-5.5.
  --variant NAME          Provider model variant/reasoning level.
  --reasoning LEVEL       Alias for --variant.
  --agent NAME            OpenCode agent name.
  --system TEXT           Optional system text.
  --summary-marker TEXT   Final handoff marker. Default: FINAL HANDOFF.
  --raw                   Send prompt exactly as provided; disables handoff wrapper.
  --no-reply              Add a user message without calling a model.
  --async                 Queue and return immediately.
  --wait                  With --async, wait before extracting summary.
  --timeout SECONDS       Wait timeout. Default: 300.
  --directory PATH        Project directory associated with the request.

Examples:
  harness-cli ask --profile fast --prompt-file task.md
  harness-cli ask --prompt-file task.md --timeout 900 --model github-copilot/gpt-5.5
  harness-cli ask --async --prompt-file task.md --model github-copilot/gpt-5.4-mini --variant low
  harness-cli ask --session ses_... --no-reply --prompt "Context only."
""");

    private static void PrintSpawnHelp() => Console.WriteLine("""
spawn - launch one delegated worker session per target.

Usage:
  harness-cli spawn --target TARGET [--target TARGET...] [options]

Options:
  --target TEXT              Target to launch. Repeat for multiple sessions.
  --resume-session X=Y       Reuse existing session Y for target X.
  --resume-session-json JSON JSON object mapping target text to session id.
  --directory PATH           Repository/workspace path for workers.
  --profile NAME             Built-in backend/model profile: fast, cheap, or deep.
  --model provider/model     Model metadata for workers.
  --variant NAME             Provider model variant/reasoning level.
  --wait                     Wait for each worker's final handoff.
  --timeout SECONDS          Wait timeout when --wait is set. Default: 300.

Examples:
  harness-cli spawn --target "issue #5" --target "issue #4" --directory E:\work\baton --model github-copilot/gpt-5.5
  harness-cli spawn --target "issue #5" --resume-session "issue #5=ses_..."
""");

    private static void PrintStatusHelp() => Console.WriteLine("""
status - show current OpenCode session activity state.

Usage:
  harness-cli status [--session ses_...] [--server URL]

Examples:
  harness-cli status
  harness-cli status --session ses_...
""");

    private static void PrintMessagesHelp() => Console.WriteLine("""
messages - print recent session messages as raw JSON.

Usage:
  harness-cli messages --session ses_... [--limit N] [--directory PATH]

Options:
  --session ses_...  Required session id.
  --limit N          Maximum messages. Default: 20.

Example:
  harness-cli messages --session ses_... --limit 20
""");

    private static void PrintLastSummaryHelp() => Console.WriteLine("""
last-summary - extract the fresh assistant final handoff after the latest user prompt.

Usage:
  harness-cli last-summary --session ses_... [--summary-marker TEXT] [--plain]

Options:
  --session ses_...      Required session id.
  --summary-marker TEXT  Marker to search for. Default: FINAL HANDOFF.
  --plain                Print only the summary text instead of JSON.

Freshness:
  Historical handoffs before the latest user prompt are ignored. If the latest prompt has no fresh final handoff yet, this command exits non-zero and says so instead of returning a stale summary.

Example:
  harness-cli last-summary --session ses_... --plain
""");

    private static void PrintWaitHelp() => Console.WriteLine("""
wait - passively wait until OpenCode reports a session is idle.

Usage:
  harness-cli wait --session ses_...

Options:
  --session ses_...  Required session id.

Behavior:
  Does not send a prompt.
  Does not accept --timeout; press Ctrl+C to stop waiting.
  OpenCode omits idle sessions from /session/status, so a missing status entry counts as idle.
  Does not require a fresh FINAL HANDOFF. Use last-summary when you need handoff text.

Example:
  harness-cli wait --session ses_...
""");

    private static void PrintAbortHelp() => Console.WriteLine("""
abort - abort an OpenCode session deliberately.

Usage:
  harness-cli abort --session ses_...

Example:
  harness-cli abort --session ses_...
""");

    private static void PrintEventsHelp() => Console.WriteLine("""
events - sample OpenCode server event stream briefly.

Usage:
  harness-cli events [--limit N] [--timeout SECONDS] [--server URL]

Options:
  --limit N          Maximum events. Default: 10.
  --timeout SECONDS  Stream timeout. Default: 30.

Example:
  harness-cli events --limit 10 --timeout 30
""");

    private static void PrintWatchHelp() => Console.WriteLine("""
watch - send a raw supervision prompt to one session, optionally recurring.

Usage:
  harness-cli watch --session ses_... [--prompt TEXT | --prompt-file FILE | stdin] [options]

Options:
  --session ses_...          Required session id.
  --prompt TEXT              Inline supervision prompt. Uses a generic progress prompt if omitted.
  --prompt-file FILE         Read supervision prompt from a file.
  --directory PATH           Project directory associated with the prompt.
  --interval-minutes N       Minutes between prompt rounds. Default: 60.
  --once                     Send one prompt and exit.
  --until-idle               After sending the prompt, stop once OpenCode reports the session is idle. Not a passive wait.
  --max-runs N               Stop after N prompt rounds.
  --max-duration-minutes N   Stop after N minutes.
  --dry-run                  Print what would be sent without calling OpenCode.

Safety:
  watch always sends a prompt first. Do not use it as a replacement for a passive wait; use status/tail/last-summary for inspection.
  Prefer --until-idle, --max-runs, or --max-duration-minutes for bounded supervision unless you intentionally want an indefinite loop.

Examples:
  harness-cli watch --session ses_... --directory E:\ --interval-minutes 15 --prompt-file watch.md
  harness-cli watch --session ses_... --until-idle --max-runs 12 --interval-minutes 10
  harness-cli watch --session ses_... --dry-run --once --prompt "Check progress."
""");

    private static void PrintWatchManyHelp() => Console.WriteLine("""
watch-many - send raw supervision prompts to multiple sessions, optionally recurring.

Usage:
  harness-cli watch-many --session ses_a --session ses_b [--prompt TEXT | --prompt-file FILE | stdin] [options]

Options:
  --session ses_...          Session id. Repeat for each session to supervise.
  --prompt TEXT              Inline supervision prompt. Uses a generic progress prompt if omitted.
  --prompt-file FILE         Read supervision prompt from a file.
  --directory PATH           Project directory associated with the prompt.
  --interval-minutes N       Minutes between prompt rounds. Default: 60.
  --until-idle               After sending prompts, stop once OpenCode reports all sessions are idle. Not passive waiting.
  --max-runs N               Stop after N prompt rounds.
  --max-duration-minutes N   Stop after N minutes.
  --dry-run                  Print what would be sent without calling OpenCode.

Examples:
  harness-cli watch-many --session ses_a --session ses_b --until-idle --max-duration-minutes 120 --prompt-file watch.md
  harness-cli watch-many --session ses_a --session ses_b --dry-run --max-runs 1 --prompt "Check progress."
""");

    private static void PrintTailHelp() => Console.WriteLine("""
tail - poll compact recent text messages from a session.

Usage:
  harness-cli tail --session ses_... [--limit N] [--interval-seconds N] [--once]

Options:
  --session ses_...       Required session id.
  --limit N               Recent messages to fetch per poll. Default: 20.
  --interval-seconds N    Seconds between polls. Default: 5.
  --once                  Print one snapshot and exit.

Examples:
  harness-cli tail --session ses_... --limit 20 --once
  harness-cli tail --session ses_... --interval-seconds 5
""");

    private static void PrintExportHelp() => Console.WriteLine("""
export - save session status, final summary, and messages as JSON or Markdown.

Usage:
  harness-cli export --session ses_... [--format json|md] [--output FILE] [--limit N]

Options:
  --session ses_...  Required session id.
  --format json|md   Output format. Default: json.
  --output FILE      Write to file instead of stdout.
  --limit N          Limit exported messages. Default: full message history.

Examples:
  harness-cli export --session ses_... --format json --output session.json
  harness-cli export --session ses_... --format md --output session.md
""");

    private sealed record Summary(string MessageId, string PartId, string Text);

    private sealed record SessionState(
        string? ApiStatus,
        string DerivedStatus,
        string EffectiveStatus,
        int MessageCount,
        string? LatestUserMessageId,
        string? LatestAssistantMessageId,
        bool HasAssistantAfterAnchor,
        Summary? FreshSummary);

    private sealed record MessageLine(string Id, string Text);

    private sealed class Options
    {
        public string Server { get; private set; } = DefaultServer;
        public string? Hostname { get; private set; }
        public int? Port { get; private set; }
        public string? Directory { get; private set; }
        public string? LogDir { get; private set; }
        public bool PrintLogs { get; private set; }
        public string? LogLevel { get; private set; }
        public string? Title { get; private set; }
        public string? Parent { get; private set; }
        public string? Agent { get; private set; }
        public string? Model { get; private set; }
        public string? Profile { get; private set; }
        public string? Session { get; private set; }
        public string? Prompt { get; private set; }
        public string? PromptFile { get; private set; }
        public string? System { get; private set; }
        public string? Search { get; private set; }
        public string? Variant { get; private set; }
        public string Format { get; private set; } = "json";
        public string? Output { get; private set; }
        public List<string> Targets { get; } = [];
        public List<string> Sessions { get; } = [];
        public List<string> CopilotAllowTools { get; } = [];
        public List<string> CopilotAllowUrls { get; } = [];
        public Dictionary<string, string> ResumeSessions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string SummaryMarker { get; private set; } = "FINAL HANDOFF";
        public string? Backend { get; private set; }
        public string? Engine { get; private set; }
        public bool Raw { get; private set; }
        public bool NoReply { get; private set; }
        public bool Async { get; private set; }
        public bool Wait { get; private set; }
        public bool All { get; private set; }
        public bool Plain { get; private set; }
        public bool DryRun { get; private set; }
        public bool Once { get; private set; }
        public bool UntilIdle { get; private set; }
        public bool CopilotAllowAll { get; private set; }
        public int Limit { get; private set; }
        public int IntervalMinutes { get; private set; } = 60;
        public int IntervalSeconds { get; private set; } = 5;
        public int? MaxRuns { get; private set; }
        public int? MaxDurationMinutes { get; private set; }
        public int TimeoutSeconds { get; private set; } = 300;
        public bool TimeoutWasProvided { get; private set; }
        public string? ResolvedModelProvider { get; private set; }
        public string? ResolvedModel { get; private set; }
        public string? ResolvedVariant { get; private set; }
        public string? ResolvedAgent { get; private set; }
        public string? ResolvedSystem { get; private set; }

        public void ApplyResolvedProfile(ResolvedAgentProfile profile)
        {
            ResolvedModelProvider = profile.ModelProvider;
            ResolvedModel = profile.Model;
            ResolvedVariant = profile.Variant;
            ResolvedAgent = profile.Agent;
            ResolvedSystem = profile.System;
            if (!TimeoutWasProvided && profile.Timeout is not null)
            {
                TimeoutSeconds = Math.Max(1, (int)Math.Ceiling(profile.Timeout.Value.TotalSeconds));
            }
        }

        public void ApplyForSelfTest(IReadOnlyDictionary<string, string?> values)
        {
            if (values.TryGetValue(nameof(Agent), out var agent)) Agent = agent;
            if (values.TryGetValue(nameof(Model), out var model)) Model = model;
            if (values.TryGetValue(nameof(System), out var system)) System = system;
            if (values.TryGetValue(nameof(NoReply), out var noReply)) NoReply = string.Equals(noReply, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            var queue = new Queue<string>(args);
            while (queue.Count > 0)
            {
                var arg = queue.Dequeue();
                switch (arg)
                {
                    case "--server": options.Server = Value(queue, arg); break;
                    case "--hostname": options.Hostname = Value(queue, arg); break;
                    case "--port": options.Port = PositiveInt(Value(queue, arg), arg); break;
                    case "--directory": options.Directory = Value(queue, arg); break;
                    case "--log-dir": options.LogDir = Value(queue, arg); break;
                    case "--print-logs": options.PrintLogs = true; break;
                    case "--log-level": options.LogLevel = Value(queue, arg); break;
                    case "--title": options.Title = Value(queue, arg); break;
                    case "--parent": options.Parent = Value(queue, arg); break;
                    case "--agent": options.Agent = Value(queue, arg); break;
                    case "--model": options.Model = Value(queue, arg); break;
                    case "--profile": options.Profile = Value(queue, arg); break;
                    case "--session": options.Session = Value(queue, arg); options.Sessions.Add(options.Session); break;
                    case "--prompt": options.Prompt = Value(queue, arg); break;
                    case "--prompt-file": options.PromptFile = Value(queue, arg); break;
                    case "--system": options.System = Value(queue, arg); break;
                    case "--search": options.Search = Value(queue, arg); break;
                    case "--variant": options.Variant = Value(queue, arg); break;
                    case "--reasoning": options.Variant = Value(queue, arg); break;
                    case "--format": options.Format = Value(queue, arg); break;
                    case "--output": options.Output = Value(queue, arg); break;
                    case "--target": options.Targets.Add(Value(queue, arg)); break;
                    case "--copilot-allow-tool": options.CopilotAllowTools.Add(Value(queue, arg)); break;
                    case "--copilot-allow-url": options.CopilotAllowUrls.Add(Value(queue, arg)); break;
                    case "--backend": options.Backend = Value(queue, arg); break;
                    case "--engine": options.Engine = Value(queue, arg); break;
                    case "--resume-session": AddResumeSession(options.ResumeSessions, Value(queue, arg), arg); break;
                    case "--resume-session-json": AddResumeSessions(options.ResumeSessions, Value(queue, arg)); break;
                    case "--summary-marker": options.SummaryMarker = Value(queue, arg); break;
                    case "--raw": options.Raw = true; break;
                    case "--no-reply": options.NoReply = true; break;
                    case "--async": options.Async = true; break;
                    case "--wait": options.Wait = true; break;
                    case "--all": options.All = true; break;
                    case "--plain": options.Plain = true; break;
                    case "--dry-run": options.DryRun = true; break;
                    case "--once": options.Once = true; break;
                    case "--until-idle": options.UntilIdle = true; break;
                    case "--copilot-allow-all": options.CopilotAllowAll = true; break;
                    case "--limit": options.Limit = PositiveInt(Value(queue, arg), arg); break;
                    case "--interval-minutes": options.IntervalMinutes = PositiveInt(Value(queue, arg), arg); break;
                    case "--interval-seconds": options.IntervalSeconds = PositiveInt(Value(queue, arg), arg); break;
                    case "--max-runs": options.MaxRuns = PositiveInt(Value(queue, arg), arg); break;
                    case "--max-duration-minutes": options.MaxDurationMinutes = PositiveInt(Value(queue, arg), arg); break;
                    case "--timeout": options.TimeoutSeconds = PositiveInt(Value(queue, arg), arg); options.TimeoutWasProvided = true; break;
                    default: throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            return options;
        }

        private static void AddResumeSession(Dictionary<string, string> sessions, string value, string option)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
            {
                throw new ArgumentException($"{option} must use target=session-id format.");
            }

            sessions[value[..separator]] = value[(separator + 1)..];
        }

        private static void AddResumeSessions(Dictionary<string, string> sessions, string json)
        {
            var node = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("--resume-session-json must be a JSON object.");
            foreach (var item in node)
            {
                var session = item.Value?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(session)) throw new ArgumentException("--resume-session-json values must be session ids.");
                sessions[item.Key] = session;
            }
        }

        private static string Value(Queue<string> queue, string option)
        {
            if (queue.Count == 0) throw new ArgumentException($"{option} requires a value.");
            return queue.Dequeue();
        }

        private static int PositiveInt(string value, string option)
        {
            if (!int.TryParse(value, out var parsed) || parsed <= 0)
            {
                throw new ArgumentException($"{option} must be a positive integer, got '{value}'.");
            }

            return parsed;
        }
    }
}
