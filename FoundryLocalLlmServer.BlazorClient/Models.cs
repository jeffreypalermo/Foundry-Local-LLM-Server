using System.Text.Json.Serialization;

namespace FoundryLocalLlmServer.BlazorClient;

public sealed class FoundryConfig
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class FoundryStatus
{
    public bool Running { get; set; }
    public string? Endpoint { get; set; }
}

public sealed class UnloadAllResponse
{
    public string[] Unloaded { get; set; } = [];
}

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string[] Capabilities { get; set; } = [];
    public bool Text { get; set; }
    public bool Code { get; set; }
    public bool Reasoning { get; set; }
    public bool Vision { get; set; }
    public bool Audio { get; set; }
    public bool Tools { get; set; }
}

public sealed class ModelsResponse
{
    public string Current { get; set; } = "";
    public ModelInfo[] Available { get; set; } = [];
}

public sealed class ChatTurn
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

// Minimal OpenAI chat-completion response shape (only what the UI reads).
public sealed class ChatCompletion
{
    [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
    public sealed class Choice { [JsonPropertyName("message")] public Message? Message { get; set; } }
    public sealed class Message
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; }
    }
    public sealed class ToolCall
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("function")] public Func? Function { get; set; }
    }
    public sealed class Func
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }
}

public sealed class TranscriptionResponse
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}
