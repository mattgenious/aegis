using System.Net;
using System.Text.Json.Nodes;
using Aegis.Backends;
using Aegis.Core;
using Xunit;

namespace Aegis.UnitTests;

public sealed class OpenCodeBackendTests
{
    [Fact]
    public async Task PostPromptSendsModelObjectExpectedByOpenCode()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:4096/")
        };
        var backend = new OpencodeBackend(new OpenCodeClient(http));
        var session = new SessionRecord(
            SessionId: "opencode-local",
            Backend: BackendKind.Opencode,
            BackendSessionId: "ses_local",
            CreatedAtUtc: DateTimeOffset.UtcNow);
        var request = new PromptRequest(
            Text: "Use the selected model.",
            SourceKind: PromptSourceKind.Inline,
            SourceLocation: null,
            ModelProvider: "github-copilot",
            Model: "gpt-5.5",
            Variant: "high",
            Agent: "shipper",
            Raw: true);

        var result = await backend.PostPromptAsync(session, request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(handler.Body);
        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.Equal("shipper", body["agent"]!.GetValue<string>());
        Assert.False(body.ContainsKey("provider"));
        Assert.Equal("github-copilot", body["model"]!["providerID"]!.GetValue<string>());
        Assert.Equal("gpt-5.5", body["model"]!["modelID"]!.GetValue<string>());
        Assert.Equal("high", body["variant"]!.GetValue<string>());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
