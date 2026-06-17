namespace FoundryLocalLlmServer.Server;

public sealed class FoundryLocalOptions
{
    public const string SectionName = "FoundryLocal";

    public string Endpoint { get; set; } = "http://127.0.0.1:5273";

    public string Model { get; set; } = "phi-4-mini";

    // Candidate models the server offers for selection via GET /api/models and
    // POST /api/models/select. The startup default (Model) should also appear here. Expanding
    // the selectable set is purely a config change — add aliases here, no code change needed.
    public string[] AvailableModels { get; set; } = [];

    public string? ApiKey { get; set; }

    public bool UseStubResponses { get; set; } = false;

    // Preferred GPU execution provider for the in-process Foundry runtime. On an NVIDIA RTX
    // GPU (e.g. RTX 4060, Ada Lovelace) "CUDA" is the most broadly compatible accelerator;
    // "NvTensorRtRtx" is the RTX-optimized alternative. Other discovered GPU EPs are still
    // registered as fallbacks so inference never silently drops to CPU.
    public string PreferredExecutionProvider { get; set; } = "CUDA";

    // Maximum number of INPUT (prompt) tokens forwarded to the in-process Foundry runtime per
    // chat request. The embedded ONNX GenAI runtime allocates a CUDA KV-cache arena sized to the
    // request's prompt length and never releases it for the life of the process (an arena
    // high-water-mark that IModel.UnloadAsync / StopWebServiceAsync do NOT reclaim). A web UI
    // that resends an ever-growing conversation therefore drives VRAM to OOM. The proxy trims the
    // oldest turns so a request never exceeds this budget, capping the arena. 0 disables trimming.
    public int MaxPromptTokens { get; set; } = 8192;

    // Hard ceiling for the generated-completion length (max_tokens). Applied on every request —
    // including ones that omit max_tokens (the web UI sends none) — so generation is always
    // bounded. A client-supplied smaller value is honored; a larger value (or the model's full
    // context window) is capped to this. 0 disables the response cap.
    public int MaxResponseTokens { get; set; } = 2048;
}
