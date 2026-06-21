using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Live, GPU-required integration matrix that exercises <b>every</b> model in the curated
/// <c>AvailableModels</c> catalog against real Foundry Local inference through the proxy — no stubs,
/// no skips. Each model is loaded on the GPU and processes a capability-appropriate prompt.
///
/// <para><b>Coverage by capability:</b></para>
/// <list type="bullet">
///   <item><b>Text / Code / Reasoning</b> (<see cref="EveryTextModel_ProcessesPromptAndReturnsRealOutput"/>):
///         each model generates real, non-empty output for a prompt tailored to its specialty.</item>
///   <item><b>Tools</b> (<see cref="ToolCapableModel_AcceptsToolsAndReturnsValidShape"/>): tool-capable
///         models accept an OpenAI <c>tools</c> array and return a valid completion shape.</item>
///   <item><b>Vision</b> (<see cref="VisionModel_RuntimeBlocked_DocumentsLimitation"/>) and
///         <b>Audio/Whisper</b> (<see cref="WhisperModel_LoadsButHasNoHttpAudioRoute"/>): pin the
///         current platform limitation so the suite stays green and honest, and <i>fail loudly</i>
///         the moment Foundry's runtime gains support — at which point they convert to real
///         capability tests.</item>
/// </list>
///
/// <para>Every test is <c>[Trait("Category","GPU-Required")]</c>. <c>privatebuild.ps1</c> runs them by
/// default (live Foundry); CI filters the category out. Nothing self-skips: if Foundry is down or a
/// model cannot be readied, the test FAILS.</para>
/// </summary>
[Collection("ServerTests")]
public class FullCapabilityMatrixTests
{
    private const string GpuRequiredCategory = "GPU-Required";
    private const string ChatRoute = "/v1/chat/completions";

    private readonly ITestOutputHelper _output;

    public FullCapabilityMatrixTests(ITestOutputHelper output) => _output = output;

    // ── Text / Code / Reasoning ───────────────────────────────────────────────────

    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(ModelMatrix.TextLike), MemberType = typeof(ModelMatrix))]
    public async Task EveryTextModel_ProcessesPromptAndReturnsRealOutput(string alias, ModelMatrix.CapabilityKind kind)
    {
        var factory = await RequireModelReadyAsync(alias);
        using var owner = factory;
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(8); // first request loads the model on the GPU (large/reasoning models are slow)

        var (prompt, maxTokens) = PromptFor(kind);

        var response = await client.PostAsJsonAsync(ChatRoute, new
        {
            model = alias,
            stream = false,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } },
        });

        Assert.True(response.IsSuccessStatusCode,
            $"[{alias}/{kind}] expected success but got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.Equal("chat.completion", payload!["object"]?.GetValue<string>());

        var content = payload["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content), $"[{alias}/{kind}] returned empty content.");
        _output.WriteLine($"[{alias}/{kind}] -> {Trim(content!)}");

        // Capability-specific evidence the model actually did the task.
        switch (kind)
        {
            case ModelMatrix.CapabilityKind.Code:
                Assert.True(
                    content!.Contains("def", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("return", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("sum", StringComparison.OrdinalIgnoreCase)
                    || content.Contains('('),
                    $"[{alias}] code output lacks any code token: {Trim(content)}");
                break;
            case ModelMatrix.CapabilityKind.Reasoning:
                Assert.True(
                    content!.Contains("43") || content.Contains("forty-three", StringComparison.OrdinalIgnoreCase),
                    $"[{alias}] reasoning answer did not reach 43: {Trim(content)}");
                break;
        }
    }

    // ── Tools ─────────────────────────────────────────────────────────────────────

    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(ModelMatrix.Tools), MemberType = typeof(ModelMatrix))]
    public async Task ToolCapableModel_AcceptsToolsAndReturnsValidShape(string alias)
    {
        var factory = await RequireModelReadyAsync(alias);
        using var owner = factory;
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var body = new JsonObject
        {
            ["model"] = alias,
            ["stream"] = false,
            ["max_tokens"] = 128,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "What is the weather in Paris? Use the get_weather tool." },
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "get_weather",
                        ["description"] = "Get the current weather for a city",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } },
                            ["required"] = new JsonArray { "city" },
                        },
                    },
                },
            },
            ["tool_choice"] = "auto",
        };

        var response = await client.PostAsJsonAsync(ChatRoute, body);
        Assert.True(response.IsSuccessStatusCode,
            $"[{alias}] tool request failed with {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.Equal("chat.completion", payload!["object"]?.GetValue<string>());

        var message = payload["choices"]?[0]?["message"];
        Assert.NotNull(message);
        // Valid OpenAI shape: either a tool_calls array fired, or assistant content is present.
        var toolCalls = message!["tool_calls"] as JsonArray;
        var content = message["content"]?.GetValue<string>();
        Assert.True(toolCalls is not null || !string.IsNullOrWhiteSpace(content),
            $"[{alias}] tool response had neither tool_calls nor content.");

        _output.WriteLine($"[{alias}/tools] tool_calls={(toolCalls?.Count ?? 0)} content={Trim(content ?? "")}");
    }

    // ── Vision (runtime-blocked) ────────────────────────────────────────────────────

    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(ModelMatrix.Vision), MemberType = typeof(ModelMatrix))]
    public async Task VisionModel_DescribesImageContent(string alias)
    {
        // Real vision capability test: send a generated image and assert the VL model produces a
        // description referencing the depicted subject. The image is a large green circle on white.
        var factory = await RequireModelReadyAsync(alias);
        using var owner = factory;
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var pngDataUrl = "data:image/png;base64," + GreenCirclePngBase64();
        var body = new JsonObject
        {
            ["model"] = alias,
            ["stream"] = false,
            ["max_tokens"] = 64,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Describe this image in one short sentence, including the main shape and its color." },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = pngDataUrl } },
                    },
                },
            },
        };

        var response = await client.PostAsJsonAsync(ChatRoute, body);
        Assert.True(response.IsSuccessStatusCode,
            $"[{alias}/vision] expected success but got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content), $"[{alias}/vision] returned empty content for an image prompt.");
        _output.WriteLine($"[{alias}/vision] -> {Trim(content!)}");

        // The model must have actually looked at the image: it should mention a shape or a color.
        string[] visualWords =
            ["circle", "round", "dot", "sphere", "ball", "oval", "shape",
             "green", "white", "color", "colour", "background"];
        Assert.True(visualWords.Any(w => content!.Contains(w, StringComparison.OrdinalIgnoreCase)),
            $"[{alias}/vision] description names no shape/color — did it read the image? Got: {Trim(content!)}");
    }

    // ── Audio / Whisper (speech-to-text) ───────────────────────────────────────────────

    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(ModelMatrix.Audio), MemberType = typeof(ModelMatrix))]
    public async Task WhisperModel_TranscribesSpeechAudio(string alias)
    {
        // Real ASR test: POST a known speech clip ("The quick brown fox jumps over the lazy dog.")
        // to the proxy's OpenAI-compatible /v1/audio/transcriptions endpoint (which bridges to the
        // `foundry transcribe` CLI) and assert the transcript contains the expected words.
        var factory = await RequireModelReadyAsync(alias);
        using var owner = factory;
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);

        var wavPath = TestAssetPath("speech.wav");
        Assert.True(File.Exists(wavPath), $"Missing test asset {wavPath}");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(alias), "model" },
            { new ByteArrayContent(await File.ReadAllBytesAsync(wavPath)) { Headers = { { "Content-Type", "audio/wav" } } }, "file", "speech.wav" },
        };

        var response = await client.PostAsync("/v1/audio/transcriptions", form);
        Assert.True(response.IsSuccessStatusCode,
            $"[{alias}/audio] transcription failed with {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        var text = payload?["text"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(text), $"[{alias}/audio] empty transcript.");
        _output.WriteLine($"[{alias}/audio] -> {Trim(text!)}");

        string[] expected = ["fox", "quick", "brown", "lazy", "dog"];
        Assert.True(expected.Any(w => text!.Contains(w, StringComparison.OrdinalIgnoreCase)),
            $"[{alias}/audio] transcript missing expected words. Got: {Trim(text!)}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static (string Prompt, int MaxTokens) PromptFor(ModelMatrix.CapabilityKind kind) => kind switch
    {
        ModelMatrix.CapabilityKind.Code =>
            ("Write a Python function named total that returns the sum of a list of integers. Output only the code.", 200),
        ModelMatrix.CapabilityKind.Reasoning =>
            ("What is 17 plus 26? Think briefly, then state the final number.", 800),
        _ => ("Reply with one short, friendly sentence about the ocean.", 96),
    };

    /// <summary>
    /// Builds a proxy wired to live Foundry. The proxy itself loads the resolved GPU variant on first
    /// use (the cross-platform daemon needs the exact id loaded and `model load &lt;alias&gt;` would pick a
    /// non-GPU variant), so this just confirms the daemon is reachable.
    /// </summary>
    private static async Task<WebApplicationFactory<Program>> RequireModelReadyAsync(string alias)
    {
        var foundryUrl = await FoundryServiceHelper.GetServiceUrlAsync();
        Assert.False(string.IsNullOrEmpty(foundryUrl),
            "Foundry Local daemon is not running — start it with `foundry server start`.");
        return new PhiFoundryServerFactory(foundryUrl!, alias);
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    /// <summary>Path to a file under the project's TestAssets folder (copied next to the test dll).</summary>
    private static string TestAssetPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);

    /// <summary>A 128×128 white PNG with a centered green circle — a clear subject for VL models.</summary>
    private static string GreenCirclePngBase64() =>
        "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAMtSURBVHhe7ZJLaiRBDAX76HPz8SbBzTPGtv5ZioDYFEgpqd7rP6zmpR9gFwRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOQRgOY8NwOvfK9wn8qit9Idl+hSu30R/TIc3c+X0+gMmeRtXTazHnuwtXDGpHvcmpzN6Qj3mzU5l5GR6vCc5jXET6cGe6CRGTaOHerJTGDOJHmiDE2ifQo+y0U5aX9dDbLaLtpf1ANjzK1pe1cXx02rKX9SF8auVlL6mi+L3VlH2ki6IP1tBzSsEwGQFJa/oYvh7s0l/QRfCv5tJanddBO1mkdZZF0C/GeR0JQApZpDSVQfHOKOJ70gAUo0mvKMOjPFGEtuNAJQYSWg3HRTzjCKuEwEoNYqwTjog5htBTBcC0GIEMV0IQIsRhHTRwbBOL/4OBKBVL/4OBKBVL+4OOhDW68FXTQBG6MFXTQBG6MFVrYNgn1bslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbslQRglFbMlToA9mrFXkkIRmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXkkARmnFXnnQQbBeD75qAjBCD75qAjBCD77qgw6EdXrxdyAArXrxdyAArXrxdzjoYJhvBDFdCECLEcR0IQAtRhDT5aADYp5RxHUiAKVGEdfpoINivJHEdiMAJUYS2+2gA2Oc0cR3JACpRhPf8aCDo98McroSgBQzyOl60AXQbhZ5nQ+6CP7dTHK7H3Qh/L3Z5L9w0MXwZyuoeYUAmKyg5pWDLojfW0XdSwddFL9aSe1rB10YP62m/sWDLo49v6Ln1YMeYLNd9L180ENstJPe19/Qo2xwAjOmOOiBnuwU5kxy0EM90UnMmuagB3uS05g30Rt6vJudytzJ3tBj3uR05k/4hh53srdwz6Rv6LEneRv3TSzoD+jwZu6eXtAfk+lTeM4mgv6wCJ/IM7eCX0MAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlkMAlvMBEdINHCVt3DQAAAAASUVORK5CYII=";
}
