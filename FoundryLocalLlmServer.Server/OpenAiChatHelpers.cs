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

    /// <summary>
    /// Rough token estimate (~4 chars/token) used only to bound request size; it does not need to
    /// match the model tokenizer exactly, only to be monotonic so trimming converges.
    /// </summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    /// <summary>Extracts the plain text of a chat message's <c>content</c> (string or parts array).</summary>
    private static string MessageText(JsonNode? message)
    {
        var content = message?["content"];
        if (content is null) return string.Empty;
        if (content is JsonValue v) return v.GetValue<string>();
        return string.Join(" ", content.AsArray()
            .Select(p => p?["text"]?.GetValue<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))!);
    }

    /// <summary>
    /// Result of <see cref="ApplyContextBounds"/>: how many messages were dropped, the trimmed
    /// input token estimate, and the effective <c>max_tokens</c> applied.
    /// </summary>
    public readonly record struct ContextBounds(int DroppedMessages, int InputTokens, int MaxTokens);

    /// <summary>
    /// Bounds a chat-completions payload IN PLACE so a single request can never blow up the
    /// in-process Foundry runtime's (non-reclaimable) CUDA KV-cache arena:
    /// <list type="number">
    ///   <item>Caps <c>max_tokens</c> to <paramref name="maxResponseTokens"/> (and to the model's
    ///   total context window when known), injecting it when the client omitted it.</item>
    ///   <item>Trims the oldest non-system messages until the estimated input fits
    ///   <paramref name="maxPromptTokens"/>, always preserving leading system message(s) and the
    ///   final (latest) message. An oversized final message is head-truncated to the budget.</item>
    /// </list>
    /// A value of 0 for either budget disables that part of the bounding.
    /// </summary>
    public static ContextBounds ApplyContextBounds(
        JsonObject payload, int maxPromptTokens, int maxResponseTokens, int modelTotalCap)
    {
        // 1) Cap / inject max_tokens.
        var effectiveMax = 0;
        if (maxResponseTokens > 0)
        {
            effectiveMax = maxResponseTokens;
            if (payload["max_tokens"] is JsonValue mv
                && mv.TryGetValue<int>(out var requested) && requested > 0)
            {
                effectiveMax = Math.Min(requested, maxResponseTokens);
            }
            if (modelTotalCap > 0)
                effectiveMax = Math.Min(effectiveMax, Math.Max(1, modelTotalCap - 16));
            payload["max_tokens"] = effectiveMax;
        }

        // 2) Trim the prompt to the input budget.
        if (maxPromptTokens <= 0 || payload["messages"] is not JsonArray messages || messages.Count == 0)
            return new ContextBounds(0, 0, effectiveMax);

        var nodes = messages.Select(m => m).ToList();
        var roles = nodes
            .Select(m => m?["role"]?.GetValue<string>() ?? string.Empty)
            .ToList();

        // Always keep leading system message(s); keep the most-recent others within budget.
        var leadingSystem = 0;
        while (leadingSystem < nodes.Count
            && string.Equals(roles[leadingSystem], "system", StringComparison.OrdinalIgnoreCase))
        {
            leadingSystem++;
        }

        var systemTokens = 0;
        for (var i = 0; i < leadingSystem; i++)
            systemTokens += EstimateTokens(MessageText(nodes[i]));

        // Reserve at least 256 tokens for the user message so a large system prompt
        // can never truncate the actual conversation to nothing (the trigger for models
        // responding "your message got cut off").
        var budget = Math.Max(256, maxPromptTokens - systemTokens);

        // Walk newest → oldest over the non-system tail, keeping messages that fit.
        var keepFrom = nodes.Count; // index of first kept tail message
        var used = 0;
        for (var i = nodes.Count - 1; i >= leadingSystem; i--)
        {
            var tok = EstimateTokens(MessageText(nodes[i]));
            if (i == nodes.Count - 1)
            {
                // Always keep the latest message; head-truncate it if it alone exceeds the budget.
                if (tok > budget && nodes[i]?["content"] is JsonValue lv && lv.TryGetValue<string>(out var text))
                {
                    nodes[i]!["content"] = text[..Math.Min(text.Length, budget * 4)];
                    tok = budget;
                }
                used += tok;
                keepFrom = i;
                continue;
            }
            if (used + tok > budget)
                break;
            used += tok;
            keepFrom = i;
        }

        var dropped = keepFrom - leadingSystem;
        if (dropped <= 0)
            return new ContextBounds(0, systemTokens + used, effectiveMax);

        var rebuilt = new JsonArray();
        for (var i = 0; i < leadingSystem; i++)
            rebuilt.Add(nodes[i]!.DeepClone());
        for (var i = keepFrom; i < nodes.Count; i++)
            rebuilt.Add(nodes[i]!.DeepClone());
        payload["messages"] = rebuilt;

        return new ContextBounds(dropped, systemTokens + used, effectiveMax);
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
