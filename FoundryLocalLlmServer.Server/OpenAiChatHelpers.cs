using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoundryLocalLlmServer.Server;

public static class OpenAiChatHelpers
{
    public static string ExtractLatestUserPrompt(JsonNode? payload)
    {
        var messages = payload?["messages"]?.AsArray();

        if (messages is null)
        {
            return string.Empty;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var role = messages[i]?["role"]?.GetValue<string>();

            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contentNode = messages[i]?["content"];
            if (contentNode is null)
            {
                return string.Empty;
            }

            if (contentNode is JsonValue)
            {
                return contentNode.GetValue<string>();
            }

            var textParts = contentNode.AsArray()
                .Select(part => part?["text"]?.GetValue<string>())
                .Where(text => !string.IsNullOrWhiteSpace(text));

            return string.Join(" ", textParts!);
        }

        return string.Empty;
    }

    public static JsonObject CreateStubResponse(string model, string prompt)
    {
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var responseText = string.IsNullOrWhiteSpace(prompt)
            ? "Foundry Local test mode is active. Please provide a prompt."
            : $"[stub:{model}] {prompt}";

        return new JsonObject
        {
            ["id"] = completionId,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = responseText,
                    },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = Math.Max(1, prompt.Length / 4),
                ["completion_tokens"] = Math.Max(1, responseText.Length / 4),
                ["total_tokens"] = Math.Max(2, (prompt.Length + responseText.Length) / 4),
            },
        };
    }
}
