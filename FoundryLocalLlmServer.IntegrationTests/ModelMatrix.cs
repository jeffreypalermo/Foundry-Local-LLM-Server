namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// The curated GPU-compatible Foundry Local catalog for this host (mirrors
/// <c>FoundryLocal:AvailableModels</c> in the Server's appsettings.json), tagged with the
/// capability each model is exercised for in the live integration matrix.
///
/// <para>Capabilities are derived from the model family — Foundry's own <c>vision</c>/<c>toolCalling</c>
/// flags are unreliable (Whisper is flagged vision=true, real VL models vision=false), so we classify
/// by name like the Server's <c>ModelCapabilityCatalog</c> does.</para>
/// </summary>
public static class ModelMatrix
{
    /// <summary>A catalog entry: the Foundry alias plus the kind of prompt the matrix sends it.</summary>
    public sealed record Entry(string Alias, CapabilityKind Kind);

    public enum CapabilityKind
    {
        /// <summary>General text chat.</summary>
        Text,
        /// <summary>Code generation (qwen2.5-coder-*).</summary>
        Code,
        /// <summary>Chain-of-thought reasoning (deepseek-r1-*, phi-4-mini-reasoning).</summary>
        Reasoning,
        /// <summary>Vision-language (image input). Currently runtime-blocked — see matrix tests.</summary>
        Vision,
        /// <summary>Automatic speech recognition (Whisper). No HTTP route yet — see matrix tests.</summary>
        Audio,
    }

    /// <summary>
    /// Every selectable model with its primary capability. The matrix runs one capability-appropriate
    /// prompt per entry; tool-capable models additionally run the tool test (see <see cref="ToolCapableModels"/>).
    /// </summary>
    internal static readonly Entry[] All =
    [
        // ── Phi (text + tool calling) ─────────────────────────────────────────────
        new("phi-4-mini", CapabilityKind.Text),
        new("phi-4", CapabilityKind.Text),
        new("phi-4-mini-reasoning", CapabilityKind.Reasoning),
        new("phi-3.5-mini", CapabilityKind.Text),
        new("phi-3-mini-4k", CapabilityKind.Text),
        new("phi-3-mini-128k", CapabilityKind.Text),

        // ── Qwen3 (text) ──────────────────────────────────────────────────────────
        new("qwen3-0.6b", CapabilityKind.Text),
        new("qwen3-1.7b", CapabilityKind.Text),
        new("qwen3-4b", CapabilityKind.Text),
        new("qwen3-8b", CapabilityKind.Text),
        new("qwen3-14b", CapabilityKind.Text),

        // ── Qwen2.5 (text) ──────────────────────────────────────────────────────────
        new("qwen2.5-0.5b", CapabilityKind.Text),
        new("qwen2.5-1.5b", CapabilityKind.Text),
        new("qwen2.5-7b", CapabilityKind.Text),
        new("qwen2.5-14b", CapabilityKind.Text),

        // ── Qwen2.5-Coder (code) ────────────────────────────────────────────────────
        new("qwen2.5-coder-0.5b", CapabilityKind.Code),
        new("qwen2.5-coder-1.5b", CapabilityKind.Code),
        new("qwen2.5-coder-7b", CapabilityKind.Code),
        new("qwen2.5-coder-14b", CapabilityKind.Code),

        // ── Other text families ─────────────────────────────────────────────────────
        new("qwen3.5-2b-text", CapabilityKind.Text),
        new("deepseek-r1-1.5b", CapabilityKind.Reasoning),
        new("deepseek-r1-7b", CapabilityKind.Reasoning),
        new("deepseek-r1-14b", CapabilityKind.Reasoning),
        new("mistral-7b-v0.2", CapabilityKind.Text),
        new("mistral-nemo-12b-instruct", CapabilityKind.Text),
        new("olmo-3-7b-instruct", CapabilityKind.Text),
        new("smollm3-3b", CapabilityKind.Text),
        new("gpt-oss-20b", CapabilityKind.Text),

        // ── Vision-language (image input) — runtime-blocked on current onnxruntime-genai ──
        new("qwen3-vl-2b-instruct", CapabilityKind.Vision),
        new("qwen3-vl-4b-instruct", CapabilityKind.Vision),
        new("qwen3-vl-8b-instruct", CapabilityKind.Vision),
        new("qwen3.5-0.8b", CapabilityKind.Vision),
        new("qwen3.5-2b", CapabilityKind.Vision),
        new("qwen3.5-4b", CapabilityKind.Vision),
        new("qwen3.5-9b", CapabilityKind.Vision),
        new("ministral-3-3b-instruct-2512", CapabilityKind.Vision),

        // ── Whisper ASR (audio) — loads, but no HTTP transcription route yet ──────────
        new("whisper-tiny", CapabilityKind.Audio),
        new("whisper-base", CapabilityKind.Audio),
        new("whisper-small", CapabilityKind.Audio),
        new("whisper-medium", CapabilityKind.Audio),
        new("whisper-large-v3-turbo", CapabilityKind.Audio),
    ];

    /// <summary>Models that support tool/function calling (Foundry flags the Phi-4-mini family).</summary>
    internal static readonly string[] ToolCapableModels =
    [
        "phi-4-mini",
        "phi-4-mini-reasoning",
    ];

    // ── xUnit MemberData sources (object[] per row) ───────────────────────────────

    public static IEnumerable<object[]> TextLike =>
        All.Where(e => e.Kind is CapabilityKind.Text or CapabilityKind.Code or CapabilityKind.Reasoning)
           .Select(e => new object[] { e.Alias, e.Kind });

    public static IEnumerable<object[]> Vision =>
        All.Where(e => e.Kind == CapabilityKind.Vision).Select(e => new object[] { e.Alias });

    public static IEnumerable<object[]> Audio =>
        All.Where(e => e.Kind == CapabilityKind.Audio).Select(e => new object[] { e.Alias });

    public static IEnumerable<object[]> Tools =>
        ToolCapableModels.Select(m => new object[] { m });
}
