namespace FoundryLocalLlmServer.Server;

public sealed class FoundryLocalOptions
{
    public const string SectionName = "FoundryLocal";

    public string Endpoint { get; set; } = "http://127.0.0.1:5273";

    public string Model { get; set; } = "phi-4";

    public string? ApiKey { get; set; }

    public bool UseStubResponses { get; set; }
}
