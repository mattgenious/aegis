using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HarnessCli.Core;
using HarnessCli.Infrastructure;

namespace HarnessCli;

internal static partial class Program
{
    private const string WorkMapDefaultHost = "127.0.0.1";
    private const int WorkMapDefaultPort = 4896;
    private static readonly JsonSerializerOptions WorkMapAccessLogJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> WorkMapServe(IWorkMapStore store, WorkMapArgs options)
    {
        var host = string.IsNullOrWhiteSpace(options.Host) ? WorkMapDefaultHost : options.Host;
        var port = options.Port ?? WorkMapDefaultPort;
        var address = await ResolveWorkMapListenAddressAsync(host);
        var listener = new TcpListener(address, port);
        var accessLogger = new WorkMapAccessLogger(options.AccessLogPath);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            listener.Stop();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            listener.Start();
            var displayHost = FormatWorkMapUrlHost(host, address);
            Console.WriteLine($"cell observer listening on http://{displayHost}:{port}/");
            Console.WriteLine($"Reading records from {WorkMapDataDirectory(store)}");
            Console.WriteLine("Request access log is written to stderr.");
            if (accessLogger.FilePath is not null)
            {
                Console.WriteLine($"Writing request access log JSONL to {accessLogger.FilePath}");
            }

            if (IsLoopbackAddress(address))
            {
                Console.WriteLine($"For Tailscale Serve without changing firewall rules, run: tailscale serve --bg http://127.0.0.1:{port}/");
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                Console.WriteLine("For another device, open http://<this-device-ip>:" + port + "/ on the same trusted network or Tailscale.");
            }

            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await HandleWorkMapHttpClientAsync(store, client, accessLogger, cancellation.Token);
                        }
                        catch (Exception ex) when (!cancellation.IsCancellationRequested)
                        {
                            Console.Error.WriteLine($"cell serve request failed: {ex.Message}");
                        }
                    },
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            Console.CancelKeyPress -= cancelHandler;
        }

        return 0;
    }

    private static async Task HandleWorkMapHttpClientAsync(
        IWorkMapStore store,
        TcpClient client,
        WorkMapAccessLogger accessLogger,
        CancellationToken cancellationToken)
    {
        WorkMapHttpRequest? request = null;
        int? statusCode = null;
        var started = Stopwatch.GetTimestamp();
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString();

        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                request = await ReadWorkMapHttpRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                if (!isHead && !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = StatusCodes.MethodNotAllowed;
                    await WriteWorkMapJsonHttpResponseAsync(
                        stream,
                        statusCode.Value,
                        new { error = "Only GET and HEAD are supported." },
                        isHead,
                        cancellationToken);
                    return;
                }

                try
                {
                    statusCode = request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                        ? await HandleWorkMapApiRequestAsync(store, stream, request, isHead, cancellationToken)
                        : await HandleWorkMapStaticRequestAsync(stream, request.Path, isHead, cancellationToken);
                }
                catch (JsonException ex)
                {
                    statusCode = StatusCodes.InternalServerError;
                    await WriteWorkMapJsonHttpResponseAsync(
                        stream,
                        statusCode.Value,
                        new { error = "Failed to read cell JSON records.", detail = ex.Message },
                        isHead,
                        cancellationToken);
                }
                catch (IOException ex)
                {
                    statusCode = StatusCodes.InternalServerError;
                    await WriteWorkMapJsonHttpResponseAsync(
                        stream,
                        statusCode.Value,
                        new { error = "Failed to read cell records.", detail = ex.Message },
                        isHead,
                        cancellationToken);
                }
            }
        }
        finally
        {
            if (request is not null && statusCode is not null)
            {
                var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                await accessLogger.LogAsync(request, remoteEndpoint, statusCode.Value, durationMs, cancellationToken);
            }
        }
    }

    private static async Task<int> HandleWorkMapApiRequestAsync(
        IWorkMapStore store,
        NetworkStream stream,
        WorkMapHttpRequest request,
        bool isHead,
        CancellationToken cancellationToken)
    {
        if (request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteWorkMapJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                new WorkMapObserverHealth(DateTimeOffset.UtcNow, WorkMapDataDirectory(store), "ok"),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        if (request.Path.Equals("/api/cells", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/missions", StringComparison.OrdinalIgnoreCase))
        {
            await WriteWorkMapJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                await BuildWorkMapOverviewAsync(store, cancellationToken),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        var missionId = ReadCellApiId(request.Path);
        if (missionId is not null)
        {
            if (missionId.Contains('/', StringComparison.Ordinal))
            {
                await WriteWorkMapNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            var mission = await store.GetMissionAsync(missionId, cancellationToken);
            if (mission is null)
            {
                await WriteWorkMapNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            await WriteWorkMapJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                await BuildWorkMapMissionDetailAsync(store, mission, cancellationToken),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        const string sessionPrefix = "/api/sessions/";
        if (request.Path.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var sessionId = request.Path[sessionPrefix.Length..];
            if (sessionId.Contains('/', StringComparison.Ordinal))
            {
                await WriteWorkMapNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            var session = await store.GetAgentSessionAsync(sessionId, cancellationToken);
            if (session is null)
            {
                await WriteWorkMapNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            WorkMapMissionRecord? mission = string.IsNullOrWhiteSpace(session.MissionId)
                ? null
                : await store.GetMissionAsync(session.MissionId, cancellationToken);
            WorkMapWorkstreamRecord? workstream = string.IsNullOrWhiteSpace(session.WorkstreamId)
                ? null
                : await store.GetWorkstreamAsync(session.WorkstreamId, cancellationToken);

            await WriteWorkMapJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                new WorkMapSessionDetail(DateTimeOffset.UtcNow, WorkMapDataDirectory(store), mission, workstream, session),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        await WriteWorkMapNotFoundAsync(stream, isHead, cancellationToken);
        return StatusCodes.NotFound;
    }

    private static async Task<WorkMapOverview> BuildWorkMapOverviewAsync(
        IWorkMapStore store,
        CancellationToken cancellationToken)
    {
        var missions = await store.GetMissionsAsync(cancellationToken);
        var bundles = new List<WorkMapBundle>();
        foreach (var mission in missions.OrderByDescending(item => item.UpdatedAtUtc))
        {
            bundles.Add(await BuildWorkMapMissionDetailAsync(store, mission, cancellationToken));
        }

        return new WorkMapOverview(DateTimeOffset.UtcNow, WorkMapDataDirectory(store), bundles);
    }

    private static string? ReadCellApiId(string path)
    {
        const string cellPrefix = "/api/cells/";
        const string missionPrefix = "/api/missions/";
        if (path.StartsWith(cellPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[cellPrefix.Length..];
        }

        return path.StartsWith(missionPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[missionPrefix.Length..]
            : null;
    }

    private static async Task<WorkMapBundle> BuildWorkMapMissionDetailAsync(
        IWorkMapStore store,
        WorkMapMissionRecord mission,
        CancellationToken cancellationToken)
    {
        var workstreams = await store.GetWorkstreamsAsync(mission.Id, cancellationToken);
        var sessions = await store.GetAgentSessionsAsync(mission.Id, cancellationToken);
        return new WorkMapBundle(
            mission,
            workstreams.OrderBy(item => item.CreatedAtUtc).ToArray(),
            sessions.OrderByDescending(item => item.UpdatedAtUtc).ToArray());
    }

    private static async Task<int> HandleWorkMapStaticRequestAsync(
        NetworkStream stream,
        string path,
        bool isHead,
        CancellationToken cancellationToken)
    {
        var distPath = FindWorkMapUiDistPath();
        if (distPath is null)
        {
            await WriteWorkMapHttpResponseAsync(
                stream,
                StatusCodes.ServiceUnavailable,
                ContentTypes.Html,
                Encoding.UTF8.GetBytes(MissingWorkMapUiHtml()),
                isHead,
                CacheControl.NoStore,
                cancellationToken);
            return StatusCodes.ServiceUnavailable;
        }

        var relativePath = WorkMapStaticRelativePath(path);
        var fullDistPath = Path.GetFullPath(distPath);
        var staticPath = Path.GetFullPath(Path.Combine(fullDistPath, relativePath));
        if (!IsPathInDirectory(staticPath, fullDistPath) || !File.Exists(staticPath))
        {
            staticPath = Path.Combine(fullDistPath, "index.html");
        }

        if (!File.Exists(staticPath))
        {
            await WriteWorkMapHttpResponseAsync(
                stream,
                StatusCodes.ServiceUnavailable,
                ContentTypes.Html,
                Encoding.UTF8.GetBytes(MissingWorkMapUiHtml()),
                isHead,
                CacheControl.NoStore,
                cancellationToken);
            return StatusCodes.ServiceUnavailable;
        }

        var bytes = await File.ReadAllBytesAsync(staticPath, cancellationToken);
        var cacheControl = staticPath.Contains(
            $"{Path.DirectorySeparatorChar}assets{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? CacheControl.StaticAsset
            : CacheControl.NoCache;

        await WriteWorkMapHttpResponseAsync(
            stream,
            StatusCodes.Ok,
            ContentTypeFor(staticPath),
            bytes,
            isHead,
            cacheControl,
            cancellationToken);
        return StatusCodes.Ok;
    }

    private static async Task<WorkMapHttpRequest?> ReadWorkMapHttpRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } headerLine)
        {
            var separator = headerLine.IndexOf(':');
            if (separator > 0)
            {
                headers[headerLine[..separator].Trim()] = headerLine[(separator + 1)..].Trim();
            }
        }

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var method = parts[0].Trim();
        var rawTarget = parts[1].Trim();
        var uri = Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri
            : new Uri("http://localhost" + (rawTarget.StartsWith("/", StringComparison.Ordinal) ? rawTarget : "/" + rawTarget));
        return new WorkMapHttpRequest(method, Uri.UnescapeDataString(uri.AbsolutePath), uri.Query, headers);
    }

    private static async Task WriteWorkMapJsonHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        object value,
        bool isHead,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteWorkMapHttpResponseAsync(
            stream,
            statusCode,
            ContentTypes.Json,
            bytes,
            isHead,
            CacheControl.NoStore,
            cancellationToken);
    }

    private static async Task WriteWorkMapNotFoundAsync(
        NetworkStream stream,
        bool isHead,
        CancellationToken cancellationToken) =>
        await WriteWorkMapJsonHttpResponseAsync(
            stream,
            StatusCodes.NotFound,
            new { error = "Not found." },
            isHead,
            cancellationToken);

    private static async Task WriteWorkMapHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        byte[] body,
        bool isHead,
        string cacheControl,
        CancellationToken cancellationToken)
    {
        var headers =
            $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Cache-Control: {cacheControl}\r\n" +
            "Connection: close\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        if (!isHead)
        {
            await stream.WriteAsync(body, cancellationToken);
        }
    }

    private static async Task<IPAddress> ResolveWorkMapListenAddressAsync(string host)
    {
        if (host is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (host == "::")
        {
            return IPAddress.IPv6Any;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new ArgumentException($"Unable to resolve host '{host}'.");
    }

    private static string WorkMapStaticRelativePath(string requestPath)
    {
        var path = requestPath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || !Path.HasExtension(path))
        {
            return "index.html";
        }

        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? FindWorkMapUiDistPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WorkMapUi", "dist"),
            Path.Combine(Environment.CurrentDirectory, "src", "HarnessCli", "WorkMapUi", "dist"),
            Path.Combine(Environment.CurrentDirectory, "WorkMapUi", "dist")
        };

        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")));
    }

    private static bool IsPathInDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string ContentTypeFor(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".html" => ContentTypes.Html,
            ".js" => "application/javascript; charset=utf-8",
            ".json" => ContentTypes.Json,
            ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".txt" => "text/plain; charset=utf-8",
            ".webmanifest" => "application/manifest+json",
            _ => "application/octet-stream"
        };
    }

    private static string FormatWorkMapUrlHost(string host, IPAddress address)
    {
        if (address.Equals(IPAddress.Any))
        {
            return "127.0.0.1";
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return "[::1]";
        }

        return host.Contains(":", StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
            ? "[" + host + "]"
            : host;
    }

    private static bool IsLoopbackAddress(IPAddress address) =>
        address.Equals(IPAddress.Loopback)
        || address.Equals(IPAddress.IPv6Loopback);

    private static string WorkMapDataDirectory(IWorkMapStore store) =>
        store is FileWorkMapStore fileStore ? fileStore.DirectoryPath : "(custom store)";

    private static string ReasonPhrase(int statusCode) =>
        statusCode switch
        {
            StatusCodes.Ok => "OK",
            StatusCodes.NotFound => "Not Found",
            StatusCodes.MethodNotAllowed => "Method Not Allowed",
            StatusCodes.InternalServerError => "Internal Server Error",
            StatusCodes.ServiceUnavailable => "Service Unavailable",
            _ => "OK"
        };

    private static string MissingWorkMapUiHtml() => """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Aegis Cell UI Missing</title></head>
        <body style="font-family:system-ui,sans-serif;margin:2rem;line-height:1.5">
        <h1>cell observer UI is not built</h1>
        <p>Build the optional React bundle from <code>src/HarnessCli/WorkMapUi</code>, then run <code>aegis cell serve</code> again.</p>
        <pre>npm install
        npm run build</pre>
        </body>
        </html>
        """;

    private sealed record WorkMapHttpRequest(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string Target => Path + Query;

        public string? UserAgent => Headers.TryGetValue("User-Agent", out var userAgent) ? userAgent : null;
    }

    private sealed class WorkMapAccessLogger
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public WorkMapAccessLogger(string? filePath)
        {
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
            var directory = FilePath is null ? null : Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        public string? FilePath { get; }

        public async Task LogAsync(
            WorkMapHttpRequest request,
            string? remoteEndpoint,
            int statusCode,
            long durationMs,
            CancellationToken cancellationToken)
        {
            var timestamp = DateTimeOffset.UtcNow;
            Console.Error.WriteLine(
                $"{timestamp:O} {remoteEndpoint ?? "-"} {request.Method} {request.Target} {statusCode} {durationMs}ms");

            if (FilePath is null)
            {
                return;
            }

            var entry = new WorkMapAccessLogEntry(
                timestamp,
                remoteEndpoint,
                request.Method,
                request.Path,
                request.Query,
                statusCode,
                durationMs,
                request.UserAgent);
            var line = JsonSerializer.Serialize(entry, WorkMapAccessLogJsonOptions) + Environment.NewLine;

            try
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    await File.AppendAllTextAsync(FilePath, line, Encoding.UTF8, cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine($"cell serve access log failed: {ex.Message}");
            }
        }
    }

    private sealed record WorkMapAccessLogEntry(
        DateTimeOffset AtUtc,
        string? RemoteEndpoint,
        string Method,
        string Path,
        string Query,
        int StatusCode,
        long DurationMs,
        string? UserAgent);

    private sealed record WorkMapOverview(
        DateTimeOffset GeneratedAtUtc,
        string DataDirectory,
        IReadOnlyList<WorkMapBundle> Cells)
    {
        public IReadOnlyList<WorkMapBundle> Missions => Cells;
    }

    private sealed record WorkMapSessionDetail(
        DateTimeOffset GeneratedAtUtc,
        string DataDirectory,
        WorkMapMissionRecord? Cell,
        WorkMapWorkstreamRecord? Workstream,
        WorkMapAgentSessionRecord Session)
    {
        public WorkMapMissionRecord? Mission => Cell;
    }

    private sealed record WorkMapObserverHealth(DateTimeOffset GeneratedAtUtc, string DataDirectory, string Status);

    private static class StatusCodes
    {
        public const int Ok = 200;
        public const int NotFound = 404;
        public const int MethodNotAllowed = 405;
        public const int InternalServerError = 500;
        public const int ServiceUnavailable = 503;
    }

    private static class ContentTypes
    {
        public const string Html = "text/html; charset=utf-8";
        public const string Json = "application/json; charset=utf-8";
    }

    private static class CacheControl
    {
        public const string NoCache = "no-cache";
        public const string NoStore = "no-store";
        public const string StaticAsset = "public, max-age=31536000, immutable";
    }
}
