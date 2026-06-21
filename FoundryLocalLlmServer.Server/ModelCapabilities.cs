namespace FoundryLocalLlmServer.Server;

/// <summary>
/// Process-wide record of which model variant is currently loaded on the Foundry daemon. Static so it
/// is shared across every host instance in the process (e.g. the sequential WebApplicationFactory hosts
/// of the integration matrix) — that lets the proxy unload the previously loaded model before loading
/// the next one, keeping exactly ONE model resident. Holding two large models on a 16 GB GPU OOM-crashes
/// the daemon, so single-model discipline is what keeps the matrix stable.
/// </summary>
public static class ModelLoadState
{
    public static readonly SemaphoreSlim Lock = new(1, 1);
    public static string? CurrentLoadedId;
}

/// <summary>
/// The capability axes a Foundry Local model can expose. Derived from the model family/alias
/// (Foundry's own <c>vision</c>/<c>toolCalling</c> flags are unreliable — e.g. Whisper is flagged
/// vision=true, real VL models are flagged vision=false). The UI renders a capability-specific test
/// panel per model, and the live integration matrix picks capability-appropriate prompts/assertions.
/// </summary>
public sealed record ModelCapabilities(
    bool Text,
    bool Code,
    bool Reasoning,
    bool Vision,
    bool Audio,
    bool Tools)
{
    /// <summary>Stable list of capability names that are true, for JSON/UI consumption.</summary>
    public string[] Names()
    {
        var list = new List<string>(6);
        if (Text) list.Add("text");
        if (Code) list.Add("code");
        if (Reasoning) list.Add("reasoning");
        if (Vision) list.Add("vision");
        if (Audio) list.Add("audio");
        if (Tools) list.Add("tools");
        return [.. list];
    }
}

/// <summary>
/// Classifies a Foundry Local alias into its capability set using family/name heuristics that match
/// the curated <c>AvailableModels</c> catalog. Centralised so the API, the SPA, and the integration
/// tests all agree on what a given model can do.
/// </summary>
public static class ModelCapabilityCatalog
{
    /// <summary>
    /// Returns the capabilities for a model alias (e.g. "qwen2.5-coder-7b", "qwen3-vl-4b-instruct",
    /// "whisper-base"). Matching is case-insensitive and tolerant of the proxy's resolved IDs
    /// (e.g. "qwen3-vl-4b-instruct-cuda-gpu:2") because it uses substring/prefix tests on the alias.
    /// </summary>
    public static ModelCapabilities For(string alias)
    {
        var a = (alias ?? string.Empty).ToLowerInvariant();

        // Audio (automatic-speech-recognition): Whisper family. Audio-only — not a text chat model.
        var audio = a.Contains("whisper");
        if (audio)
            return new ModelCapabilities(Text: false, Code: false, Reasoning: false, Vision: false, Audio: true, Tools: false);

        // Vision-language chat: Qwen-VL, Ministral, and the Qwen3.5 VL line (but NOT qwen3.5-*-text,
        // which is the text-only export).
        var vision =
            a.Contains("-vl-")
            || a.Contains("ministral")
            || (a.StartsWith("qwen3.5-") && !a.Contains("text"));

        // Code-specialised models.
        var code = a.Contains("coder");

        // Reasoning / "thinking" models that emit chain-of-thought.
        var reasoning = a.Contains("deepseek-r1") || a.Contains("reasoning");

        // Tool / function calling. Conservative: the Phi-4-mini family is the one Foundry reliably
        // flags toolCalling=true. The proxy can still forward `tools` to any model; this flag drives
        // which models the UI offers a tool-calling panel for.
        var tools = a.Contains("phi-4-mini") || a.Contains("phi4-mini");

        // Every non-audio catalog model is a text chat model.
        return new ModelCapabilities(Text: true, Code: code, Reasoning: reasoning, Vision: vision, Audio: false, Tools: tools);
    }
}
