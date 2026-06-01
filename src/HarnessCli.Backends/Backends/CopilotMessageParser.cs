using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarnessCli.Backends;

internal static class CopilotMessageParser
{
    public static List<CopilotStoredMessage> Parse(string stdout)
    {
        var messages = new List<CopilotStoredMessage>();
        if (string.IsNullOrWhiteSpace(stdout)) return messages;

        var trimmed = stdout.Trim();
        if (LooksLikeJson(trimmed) && TryAppendJsonMessages(trimmed, messages)) return messages;

        var index = 0;
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            if (!TryParseJson(line, out var node) || node is null) continue;
            if (!TryAppendJsonMessage(node, index, messages)) continue;
            index++;
        }

        return messages;
    }

    private static bool TryAppendJsonMessages(string json, List<CopilotStoredMessage> messages)
    {
        return TryParseJson(json, out var node) && node is not null && TryAppendJsonMessages(node, messages);
    }

    private static bool TryAppendJsonMessages(JsonNode node, List<CopilotStoredMessage> messages)
    {
        var before = messages.Count;
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null) TryAppendJsonMessage(item, messages.Count, messages);
            }
        }
        else
        {
            TryAppendJsonMessage(node, messages.Count, messages);
        }

        return messages.Count > before;
    }

    private static bool TryAppendJsonMessage(JsonNode node, int index, List<CopilotStoredMessage> messages)
    {
        var role = NormalizeRole(StringValue(node["role"])
                                 ?? StringValue(node["sender"])
                                 ?? StringValue(node["message"]?["role"])
                                 ?? InferRole(StringValue(node["type"])));
        var text = ExtractText(node);
        if (string.IsNullOrWhiteSpace(text)) return false;

        messages.Add(new CopilotStoredMessage(
            StringValue(node["id"]) ?? StringValue(node["message_id"]) ?? StringValue(node["message"]?["id"]) ?? $"copilot_msg_{index:D6}",
            role,
            text,
            StringValue(node["part_id"]) ?? StringValue(node["part"]?["id"]) ?? $"copilot_part_{index:D6}",
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
                     ?? StringValue(node["delta"]);
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

    private static DateTimeOffset? ParseTimestamp(JsonNode node)
    {
        var timestampText = StringValue(node["timestamp"])
                            ?? StringValue(node["time"])
                            ?? StringValue(node["created_at"])
                            ?? StringValue(node["createdAt"])
                            ?? StringValue(node["ts"]);
        return DateTimeOffset.TryParse(timestampText, out var parsed) ? parsed : DateTimeOffset.UtcNow;
    }
}
