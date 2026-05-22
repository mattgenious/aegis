using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessCli.Backends;

public sealed class OpenCodeClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<JsonNode?> GetJson(string path, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        await EnsureSuccess(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task<JsonNode?> PostJson(string path, JsonObject? body, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(body?.ToJsonString(JsonOptions) ?? string.Empty, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, cancellationToken);
        await EnsureSuccess(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text);
    }

    public async Task<JsonNode?> PostEmpty(string path, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(path, content: null, cancellationToken);
        await EnsureSuccess(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task PostNoContent(string path, JsonObject body, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent && !response.IsSuccessStatusCode)
        {
            await EnsureSuccess(response);
        }
    }

    public async Task<JsonNode?> GetJsonEvents(string path, int limit, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
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
}

