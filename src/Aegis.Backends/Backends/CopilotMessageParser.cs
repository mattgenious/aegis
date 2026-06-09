using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aegis.Backends;

internal static class CopilotMessageParser
{
    public static List<CopilotStoredMessage> Parse(string stdout) => ParseTranscript(stdout).Messages;

    public static CopilotParseResult ParseTranscript(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return new CopilotParseResult([]);

        var nodes = new List<JsonNode>();
        var trimmed = stdout.Trim();
        if (LooksLikeJson(trimmed) && TryParseJson(trimmed, out var jsonNode) && jsonNode is not null)
        {
            AppendJsonNodes(jsonNode, nodes);
        }
        else
        {
            foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                if (TryParseJson(line, out var lineNode) && lineNode is not null) nodes.Add(lineNode);
            }
        }

        return ParseNodes(nodes);
    }

    private static void AppendJsonNodes(JsonNode node, List<JsonNode> nodes)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null) nodes.Add(item);
            }
        }
        else
        {
            nodes.Add(node);
        }
    }

    private static CopilotParseResult ParseNodes(IReadOnlyList<JsonNode> nodes)
    {
        var messages = new List<CopilotStoredMessage>();
        string? sessionId = null;
        int? exitCode = null;
        var hasFinalAssistantMessage = nodes.Any(IsFinalAssistantMessageEvent);
        var index = 0;

        foreach (var node in nodes)
        {
            if (TryReadResultMetadata(node, out var resultSessionId, out var resultExitCode))
            {
                sessionId = resultSessionId ?? sessionId;
                exitCode = resultExitCode ?? exitCode;
                continue;
            }

            if (hasFinalAssistantMessage && IsAssistantDeltaEvent(node))
            {
                continue;
            }

            if (!TryAppendJsonMessage(node, index, messages)) continue;
            index++;
        }

        return new CopilotParseResult(messages, sessionId, exitCode);
    }

    private static bool TryAppendJsonMessage(JsonNode node, int index, List<CopilotStoredMessage> messages)
    {
        var role = NormalizeRole(StringValue(node["role"])
                                 ?? StringValue(node["sender"])
                                 ?? StringValue(node["message"]?["role"])
                                 ?? StringValue(node["data"]?["role"])
                                 ?? InferRole(StringValue(node["type"])));
        var text = ExtractText(node);
        if (string.IsNullOrWhiteSpace(text)) return false;

        messages.Add(new CopilotStoredMessage(
            StringValue(node["id"]) ?? StringValue(node["message_id"]) ?? StringValue(node["message"]?["id"]) ?? StringValue(node["data"]?["id"]) ?? $"copilot_msg_{index:D6}",
            role,
            text,
            StringValue(node["part_id"]) ?? StringValue(node["part"]?["id"]) ?? StringValue(node["data"]?["part_id"]) ?? StringValue(node["data"]?["part"]?["id"]) ?? $"copilot_part_{index:D6}",
            ParseTimestamp(node)));
        return true;
    }

    private static string ExtractText(JsonNode node)
    {
        var direct = StringValue(node["text"])
                     ?? StringValue(node["content"])
                     ?? StringValue(node["message"])
                     ?? StringValue(node["output"])
                     ?? StringValue(node["response"])
                     ?? StringValue(node["delta"])
                     ?? StringValue(node["deltaContent"]);
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

        var contentText = ExtractContentArrayText(node["content"]);
        if (!string.IsNullOrWhiteSpace(contentText)) return contentText.Trim();

        foreach (var property in new[] { "message", "payload", "data", "event" })
        {
            var nested = node[property]?.AsObject();
            if (nested is null) continue;
            var nestedText = StringValue(nested["text"])
                             ?? StringValue(nested["content"])
                             ?? StringValue(nested["output"])
                             ?? StringValue(nested["response"])
                             ?? StringValue(nested["delta"])
                             ?? StringValue(nested["deltaContent"])
                             ?? ExtractContentArrayText(nested["content"]);
            if (!string.IsNullOrWhiteSpace(nestedText)) return nestedText.Trim();
        }

        return string.Empty;
    }

    private static string? ExtractContentArrayText(JsonNode? node)
    {
        if (node is not JsonArray content) return null;
        var parts = new List<string>();
        foreach (var item in content)
        {
            var itemText = StringValue(item?["text"]) ?? StringValue(item?["content"]);
            if (!string.IsNullOrWhiteSpace(itemText)) parts.Add(itemText);
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static bool TryReadResultMetadata(JsonNode node, out string? sessionId, out int? exitCode)
    {
        sessionId = null;
        exitCode = null;

        var type = StringValue(node["type"]);
        if (!string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var data = node["data"];
        sessionId = StringValue(data?["sessionId"])
                    ?? StringValue(data?["session_id"])
                    ?? StringValue(node["sessionId"])
                    ?? StringValue(node["session_id"]);
        exitCode = IntValue(data?["exitCode"])
                   ?? IntValue(data?["exit_code"])
                   ?? IntValue(node["exitCode"])
                   ?? IntValue(node["exit_code"]);
        return true;
    }

    private static bool IsAssistantDeltaEvent(JsonNode node)
    {
        var type = StringValue(node["type"]);
        return type is not null
               && type.Contains("assistant", StringComparison.OrdinalIgnoreCase)
               && type.Contains("delta", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinalAssistantMessageEvent(JsonNode node) =>
        string.Equals(StringValue(node["type"]), "assistant.message", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ExtractText(node));

    private static bool TryParseJson(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static bool LooksLikeJson(string text) => text.Length > 0 && text[0] is '{' or '[';

    private static string InferRole(string? type) =>
        type?.Contains("user", StringComparison.OrdinalIgnoreCase) == true ? "user" : "assistant";

    private static string NormalizeRole(string role) => role.ToLowerInvariant() switch
    {
        "user" or "human" => "user",
        "assistant" or "agent" or "copilot" => "assistant",
        "system" => "system",
        _ => "assistant"
    };

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? IntValue(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonNode node)
    {
        var timestampText = StringValue(node["timestamp"])
                            ?? StringValue(node["time"])
                            ?? StringValue(node["created_at"])
                            ?? StringValue(node["createdAt"])
                            ?? StringValue(node["ts"])
                            ?? StringValue(node["data"]?["timestamp"])
                            ?? StringValue(node["data"]?["created_at"])
                            ?? StringValue(node["data"]?["createdAt"]);
        return DateTimeOffset.TryParse(timestampText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }
}
