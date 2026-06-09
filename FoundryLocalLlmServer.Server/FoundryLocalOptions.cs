namespace FoundryLocalLlmServer.Server;

public sealed class FoundryLocalOptions
{
    public const string SectionName = "FoundryLocal";

    public string Endpoint { get; set; } = "http://127.0.0.1:5273";

    public string Model { get; set; } = "phi-4";

    public string? ApiKey { get; set; }

    public bool UseStubResponses { get; set; }

    /// <summary>App name passed to the Foundry Local SDK (controls data/cache dirs).</summary>
    public string AppName { get; set; } = "FoundryLocalLlmServer";

    /// <summary>
    /// ONNX Runtime execution provider to download/register. Only this EP is requested,
    /// avoiding multi-GB downloads of unrelated providers. CUDA matches the
    /// *-cuda-gpu model variants.
    /// </summary>
    public string ExecutionProvider { get; set; } = "CUDAExecutionProvider";

    /// <summary>Fail initialization if no GPU variant of the model exists (no CPU fallback).</summary>
    public bool RequireGpu { get; set; } = true;

    /// <summary>Bind address for the SDK's embedded OpenAI-compatible web service.</summary>
    public string WebServiceUrls { get; set; } = "http://127.0.0.1:0";

    /// <summary>
    /// Per-attempt initialization timeout. Generous by default because the one-time
    /// CUDA EP download is hundreds of MB and must not be interrupted.
    /// </summary>
    public int InitializationTimeoutMinutes { get; set; } = 90;

    /// <summary>Maximum initialization attempts before giving up (retries use exponential backoff).</summary>
    public int MaxInitializationAttempts { get; set; } = 5;
}
