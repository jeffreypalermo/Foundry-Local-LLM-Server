namespace FoundryLocalLlmServer.Server;

public sealed class OllamaFallbackOptions
{
    public const string SectionName = "OllamaFallback";

    public bool Enabled { get; set; } = true;

    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    public string Model { get; set; } = "phi";
}
