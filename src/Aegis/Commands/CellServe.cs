using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aegis.Core;
using Aegis.Infrastructure;

namespace Aegis;

internal static partial class Program
{
    private const string CellDefaultHost = "127.0.0.1";
    private const int CellDefaultPort = 4896;
    private static readonly JsonSerializerOptions CellAccessLogJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> CellServe(ICellStore store, CellArgs options)
    {
        var host = string.IsNullOrWhiteSpace(options.Host) ? CellDefaultHost : options.Host;
        var port = options.Port ?? CellDefaultPort;
        var address = await ResolveCellListenAddressAsync(host);
        var listener = new TcpListener(address, port);
        var accessLogger = new CellAccessLogger(options.AccessLogPath);

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
            var displayHost = FormatCellUrlHost(host, address);
            Console.WriteLine($"cell observer listening on http://{displayHost}:{port}/");
            Console.WriteLine($"Reading records from {CellDataDirectory(store)}");
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
                            await HandleCellHttpClientAsync(store, client, accessLogger, cancellation.Token);
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

    private static async Task HandleCellHttpClientAsync(
        ICellStore store,
        TcpClient client,
        CellAccessLogger accessLogger,
        CancellationToken cancellationToken)
    {
        CellHttpRequest? request = null;
        int? statusCode = null;
        var started = Stopwatch.GetTimestamp();
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString();

        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                request = await ReadCellHttpRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                var isHead = string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                if (!isHead && !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    statusCode = StatusCodes.MethodNotAllowed;
                    await WriteCellJsonHttpResponseAsync(
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
                        ? await HandleCellApiRequestAsync(store, stream, request, isHead, cancellationToken)
                        : await HandleCellStaticRequestAsync(stream, request.Path, isHead, cancellationToken);
                }
                catch (JsonException ex)
                {
                    statusCode = StatusCodes.InternalServerError;
                    await WriteCellJsonHttpResponseAsync(
                        stream,
                        statusCode.Value,
                        new { error = "Failed to read cell JSON records.", detail = ex.Message },
                        isHead,
                        cancellationToken);
                }
                catch (IOException ex)
                {
                    statusCode = StatusCodes.InternalServerError;
                    await WriteCellJsonHttpResponseAsync(
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

    private static async Task<int> HandleCellApiRequestAsync(
        ICellStore store,
        NetworkStream stream,
        CellHttpRequest request,
        bool isHead,
        CancellationToken cancellationToken)
    {
        if (request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteCellJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                new CellObserverHealth(DateTimeOffset.UtcNow, CellDataDirectory(store), "ok"),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        if (request.Path.Equals("/api/cells", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/api/missions", StringComparison.OrdinalIgnoreCase))
        {
            await WriteCellJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                await BuildCellOverviewAsync(store, cancellationToken),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        var missionId = ReadCellApiId(request.Path);
        if (missionId is not null)
        {
            if (missionId.Contains('/', StringComparison.Ordinal))
            {
                await WriteCellNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            var mission = await store.GetMissionAsync(missionId, cancellationToken);
            if (mission is null)
            {
                await WriteCellNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            await WriteCellJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                await BuildCellMissionDetailAsync(store, mission, cancellationToken),
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
                await WriteCellNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            var session = await store.GetAgentSessionAsync(sessionId, cancellationToken);
            if (session is null)
            {
                await WriteCellNotFoundAsync(stream, isHead, cancellationToken);
                return StatusCodes.NotFound;
            }

            CellMissionRecord? mission = string.IsNullOrWhiteSpace(session.MissionId)
                ? null
                : await store.GetMissionAsync(session.MissionId, cancellationToken);
            CellWorkstreamRecord? workstream = string.IsNullOrWhiteSpace(session.WorkstreamId)
                ? null
                : await store.GetWorkstreamAsync(session.WorkstreamId, cancellationToken);

            await WriteCellJsonHttpResponseAsync(
                stream,
                StatusCodes.Ok,
                new CellSessionDetail(DateTimeOffset.UtcNow, CellDataDirectory(store), mission, workstream, session),
                isHead,
                cancellationToken);
            return StatusCodes.Ok;
        }

        await WriteCellNotFoundAsync(stream, isHead, cancellationToken);
        return StatusCodes.NotFound;
    }

    private static async Task<CellOverview> BuildCellOverviewAsync(
        ICellStore store,
        CancellationToken cancellationToken)
    {
        var missions = await store.GetMissionsAsync(cancellationToken);
        var bundles = new List<CellBundle>();
        foreach (var mission in missions.OrderByDescending(item => item.UpdatedAtUtc))
        {
            bundles.Add(await BuildCellMissionDetailAsync(store, mission, cancellationToken));
        }

        return new CellOverview(DateTimeOffset.UtcNow, CellDataDirectory(store), bundles);
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

    private static async Task<CellBundle> BuildCellMissionDetailAsync(
        ICellStore store,
        CellMissionRecord mission,
        CancellationToken cancellationToken)
    {
        var workstreams = await store.GetWorkstreamsAsync(mission.Id, cancellationToken);
        var sessions = await store.GetAgentSessionsAsync(mission.Id, cancellationToken);
        return new CellBundle(
            mission,
            workstreams.OrderBy(item => item.CreatedAtUtc).ToArray(),
            sessions.OrderByDescending(item => item.UpdatedAtUtc).ToArray());
    }

    private static async Task<int> HandleCellStaticRequestAsync(
        NetworkStream stream,
        string path,
        bool isHead,
        CancellationToken cancellationToken)
    {
        var distPath = FindCellUiDistPath();
        if (distPath is null)
        {
            await WriteCellHttpResponseAsync(
                stream,
                StatusCodes.ServiceUnavailable,
                ContentTypes.Html,
                Encoding.UTF8.GetBytes(MissingCellUiHtml()),
                isHead,
                CacheControl.NoStore,
                cancellationToken);
            return StatusCodes.ServiceUnavailable;
        }

        var relativePath = CellStaticRelativePath(path);
        var fullDistPath = Path.GetFullPath(distPath);
        var staticPath = Path.GetFullPath(Path.Combine(fullDistPath, relativePath));
        if (!IsPathInDirectory(staticPath, fullDistPath) || !File.Exists(staticPath))
        {
            staticPath = Path.Combine(fullDistPath, "index.html");
        }

        if (!File.Exists(staticPath))
        {
            await WriteCellHttpResponseAsync(
                stream,
                StatusCodes.ServiceUnavailable,
                ContentTypes.Html,
                Encoding.UTF8.GetBytes(MissingCellUiHtml()),
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

        await WriteCellHttpResponseAsync(
            stream,
            StatusCodes.Ok,
            ContentTypeFor(staticPath),
            bytes,
            isHead,
            cacheControl,
            cancellationToken);
        return StatusCodes.Ok;
    }

    private static async Task<CellHttpRequest?> ReadCellHttpRequestAsync(
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
        return new CellHttpRequest(method, Uri.UnescapeDataString(uri.AbsolutePath), uri.Query, headers);
    }

    private static async Task WriteCellJsonHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        object value,
        bool isHead,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteCellHttpResponseAsync(
            stream,
            statusCode,
            ContentTypes.Json,
            bytes,
            isHead,
            CacheControl.NoStore,
            cancellationToken);
    }

    private static async Task WriteCellNotFoundAsync(
        NetworkStream stream,
        bool isHead,
        CancellationToken cancellationToken) =>
        await WriteCellJsonHttpResponseAsync(
            stream,
            StatusCodes.NotFound,
            new { error = "Not found." },
            isHead,
            cancellationToken);

    private static async Task WriteCellHttpResponseAsync(
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

    private static async Task<IPAddress> ResolveCellListenAddressAsync(string host)
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

    private static string CellStaticRelativePath(string requestPath)
    {
        var path = requestPath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || !Path.HasExtension(path))
        {
            return "index.html";
        }

        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? FindCellUiDistPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CellUi", "dist"),
            Path.Combine(Environment.CurrentDirectory, "src", "Aegis", "CellUi", "dist"),
            Path.Combine(Environment.CurrentDirectory, "CellUi", "dist")
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

    private static string FormatCellUrlHost(string host, IPAddress address)
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

    private static string CellDataDirectory(ICellStore store) =>
        store is FileCellStore fileStore ? fileStore.DirectoryPath : "(custom store)";

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

    private static string MissingCellUiHtml() => """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Aegis Cell UI Missing</title></head>
        <body style="font-family:system-ui,sans-serif;margin:2rem;line-height:1.5">
        <h1>cell observer UI is not built</h1>
        <p>Build the optional React bundle from <code>src/Aegis/CellUi</code>, then run <code>aegis cell serve</code> again.</p>
        <pre>npm install
        npm run build</pre>
        </body>
        </html>
        """;

    private sealed record CellHttpRequest(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string Target => Path + Query;

        public string? UserAgent => Headers.TryGetValue("User-Agent", out var userAgent) ? userAgent : null;
    }

    private sealed class CellAccessLogger
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public CellAccessLogger(string? filePath)
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
            CellHttpRequest request,
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

            var entry = new CellAccessLogEntry(
                timestamp,
                remoteEndpoint,
                request.Method,
                request.Path,
                request.Query,
                statusCode,
                durationMs,
                request.UserAgent);
            var line = JsonSerializer.Serialize(entry, CellAccessLogJsonOptions) + Environment.NewLine;

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

    private sealed record CellAccessLogEntry(
        DateTimeOffset AtUtc,
        string? RemoteEndpoint,
        string Method,
        string Path,
        string Query,
        int StatusCode,
        long DurationMs,
        string? UserAgent);

    private sealed record CellOverview(
        DateTimeOffset GeneratedAtUtc,
        string DataDirectory,
        IReadOnlyList<CellBundle> Cells)
    {
        public IReadOnlyList<CellBundle> Missions => Cells;
    }

    private sealed record CellSessionDetail(
        DateTimeOffset GeneratedAtUtc,
        string DataDirectory,
        CellMissionRecord? Cell,
        CellWorkstreamRecord? Workstream,
        CellAgentSessionRecord Session)
    {
        public CellMissionRecord? Mission => Cell;
    }

    private sealed record CellObserverHealth(DateTimeOffset GeneratedAtUtc, string DataDirectory, string Status);

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
