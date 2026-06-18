using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using Xunit;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// GPU integration tests that validate the project's <b>default supported model</b>
/// (<c>qwen2.5-1.5b</c>) running through <b>Foundry Local hosted IN-PROCESS</b> by the server on
/// the RTX GPU — the primary, non-fallback path.
///
/// <para>This class replaces the obsolete <c>PhiFoundryGpuIntegrationTests</c>. <c>phi-4-mini</c>
/// is excluded as a degenerate <c>:5</c> artifact (token-0 output) per Decision&#160;#9 /
/// apoc-supported-models.md, so these tests target the verified-coherent default model instead.</para>
///
/// <para><b>What these tests prove:</b></para>
/// <list type="bullet">
///   <item>The proxy routes <c>/v1/chat/completions</c> to the real, GPU-loaded in-process Foundry runtime.</item>
///   <item>The model performs GPU (CUDA) inference and returns real content.</item>
///   <item>The proxy converts Foundry's responses into OpenAI-compatible payloads (no raw leaks).</item>
///   <item>Both streaming (SSE) and non-streaming responses work.</item>
///   <item>When the requested model is unavailable, the proxy <b>errors</b> rather than silently
///         substituting another inference engine.</item>
/// </list>
///
/// <para>Tests run against the shared in-process server (<see cref="ServerFixture"/>) on
/// http://localhost:5537 with <c>UseStubResponses=false</c>. They run unconditionally and must
/// PASS on the GPU box; when the server/model cannot be made ready they FAIL with a clear message
/// — they never skip.</para>
/// </summary>
[Collection("ServerTests")]
public class FoundryGpuIntegrationTests
{
    /// <summary>Default supported model alias under test (coherent + tool-calling on the RTX 4060).</summary>
    private static readonly string ModelAlias = SupportedModelData.DefaultModel;

    private const string ChatCompletionsRoute = "/v1/chat/completions";
    private const string GpuRequiredCategory = "GPU-Required";

    private readonly ITestOutputHelper _output;
    private readonly ServerFixture _server;

    public FoundryGpuIntegrationTests(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    /// <summary>
    /// Non-streaming completion against the default model on the in-process Foundry GPU runtime.
    /// Verifies OpenAI shape, non-empty content, and a model field that reflects the model family.
    /// </summary>
    [Fact]
    [Trait("Category", GpuRequiredCategory)]
    public async Task FoundryGpu_NonStreaming_ReturnsOpenAiCompletion()
    {
        await _server.EnsureModelAsync(ModelAlias, _output);

        var response = await _server.Client.PostAsJsonAsync(ChatCompletionsRoute, new
        {
            model = ModelAlias,
            stream = false,
            max_tokens = 64,
            messages = new[]
            {
                new { role = "user", content = "Reply with a short greeting. What is 2+2?" },
            },
        });

        Assert.True(response.IsSuccessStatusCode,
            $"Expected success from the GPU path but got {(int)response.StatusCode} {response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);

        // OpenAI-compatible envelope (proxy must convert; no raw Foundry leakage).
        Assert.Equal("chat.completion", payload!["object"]?.GetValue<string>());
        Assert.NotNull(payload["id"]?.GetValue<string>());
        Assert.NotNull(payload["choices"]);

        // Model field should reflect the model family (alias is resolved to a concrete Foundry id).
        var returnedModel = payload["model"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(returnedModel), "Response is missing the 'model' field.");
        Assert.Contains("qwen", returnedModel!, StringComparison.OrdinalIgnoreCase);

        // Content must be real, non-empty inference output.
        var content = payload["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content), "Model returned empty content from the GPU path.");

        _output.WriteLine($"{ModelAlias} (model={returnedModel}) responded: {content}");
    }

    /// <summary>
    /// Streaming completion (Server-Sent Events) against the default model on the GPU.
    /// Verifies the SSE content type, OpenAI <c>chat.completion.chunk</c> framing, a terminal
    /// <c>[DONE]</c>, and that streamed deltas assemble into non-empty content.
    /// </summary>
    [Fact]
    [Trait("Category", GpuRequiredCategory)]
    public async Task FoundryGpu_Streaming_ReturnsSseChunks()
    {
        await _server.EnsureModelAsync(ModelAlias, _output);

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsRoute)
        {
            Content = new StringContent(new JsonObject
            {
                ["model"] = ModelAlias,
                ["stream"] = true,
                ["max_tokens"] = 64,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = "Count from 1 to 3." },
                },
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.True(response.IsSuccessStatusCode,
            $"Expected streaming success from the GPU path but got {(int)response.StatusCode} {response.StatusCode}.");
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var raw = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Streamed {raw.Length} bytes of SSE.");

        Assert.Contains("data:", raw);
        Assert.Contains("[DONE]", raw);

        // Reassemble streamed deltas into text and assert it is non-empty.
        var assembled = new StringBuilder();
        var sawCompletionChunk = false;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = trimmed["data:".Length..].Trim();
            if (json.Length == 0 || json == "[DONE]")
                continue;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(json); }
            catch { continue; }

            if (chunk?["object"]?.GetValue<string>() == "chat.completion.chunk")
                sawCompletionChunk = true;

            // Some frames (e.g. the trailing usage frame) carry an empty choices array;
            // indexing [0] on an empty JsonArray throws, so guard before access.
            var choices = chunk?["choices"] as JsonArray;
            if (choices is not { Count: > 0 })
                continue;

            var delta = choices[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta))
                assembled.Append(delta);
        }

        Assert.True(sawCompletionChunk, "No OpenAI 'chat.completion.chunk' frames were found in the stream.");
        Assert.False(string.IsNullOrWhiteSpace(assembled.ToString()),
            "Streamed response assembled to empty content.");

        _output.WriteLine($"Assembled streamed content: {assembled}");
    }

    /// <summary>
    /// Dedicated OpenAI-format validation for the non-streaming GPU response.
    /// Asserts the full required envelope, ensuring the proxy never leaks raw Foundry payloads.
    /// </summary>
    [Fact]
    [Trait("Category", GpuRequiredCategory)]
    public async Task FoundryGpu_Response_MatchesOpenAiSchema()
    {
        await _server.EnsureModelAsync(ModelAlias, _output);

        var response = await _server.Client.PostAsJsonAsync(ChatCompletionsRoute, new
        {
            model = ModelAlias,
            stream = false,
            max_tokens = 32,
            messages = new[]
            {
                new { role = "user", content = "Say hello." },
            },
        });

        Assert.True(response.IsSuccessStatusCode,
            $"Expected success but got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);

        // Required top-level OpenAI fields.
        Assert.Equal("chat.completion", payload!["object"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(payload["id"]?.GetValue<string>()), "Missing 'id'.");
        Assert.NotNull(payload["created"]);
        Assert.Contains("qwen", payload["model"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);

        // choices[0] shape: index, message.role == assistant, finish_reason present.
        var choice = payload["choices"]?[0];
        Assert.NotNull(choice);
        Assert.NotNull(choice!["index"]);
        Assert.Equal("assistant", choice["message"]?["role"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(choice["message"]?["content"]?.GetValue<string>()),
            "Assistant message content is empty.");
        Assert.NotNull(choice["finish_reason"]);

        _output.WriteLine($"OpenAI schema validated for GPU response: {payload.ToJsonString()}");
    }

    /// <summary>
    /// Error case: a model that Foundry Local cannot serve must surface as an error — NOT a
    /// silent substitution by another engine. With no fallback engine, the proxy returns a
    /// non-2xx status and never produces a foreign completion.
    /// </summary>
    [Fact]
    [Trait("Category", GpuRequiredCategory)]
    public async Task FoundryGpu_ModelNotAvailable_ErrorsWithoutFallback()
    {
        await _server.EnsureModelAsync(ModelAlias, _output);

        const string bogusModel = "this-model-does-not-exist-anywhere-xyz";

        var response = await _server.Client.PostAsJsonAsync(ChatCompletionsRoute, new
        {
            model = bogusModel,
            stream = false,
            max_tokens = 16,
            messages = new[]
            {
                new { role = "user", content = "This should not succeed." },
            },
        });

        // Must be an error — there is no fallback engine, so Foundry's failure is not masked.
        Assert.False(response.IsSuccessStatusCode,
            $"Expected an error for an unavailable model, but got {(int)response.StatusCode} success.");
        Assert.True(response.StatusCode is HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.InternalServerError
                        or HttpStatusCode.NotFound
                        or HttpStatusCode.BadRequest,
            $"Unexpected status code {(int)response.StatusCode} {response.StatusCode} for unavailable model.");

        // Prove no foreign completion leaked through: the body must not be a successful
        // OpenAI chat.completion for the bogus model.
        var body = await response.Content.ReadAsStringAsync();
        JsonNode? parsed = null;
        try { parsed = JsonNode.Parse(body); } catch { /* non-JSON error bodies are fine */ }
        Assert.NotEqual("chat.completion", parsed?["object"]?.GetValue<string>());

        _output.WriteLine($"Unavailable model correctly errored ({(int)response.StatusCode}) without fallback.");
    }
}
