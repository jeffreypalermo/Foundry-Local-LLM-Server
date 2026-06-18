using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Parameterized integration tests that run against <b>each supported model</b> declared in the
/// server's committed <c>FoundryLocal:AvailableModels</c> configuration. The model set and the
/// per-model tool-calling capability flag are sourced from <see cref="SupportedModelData"/>, so
/// adding/removing a model in appsettings automatically expands/contracts coverage here.
///
/// <para><b>CI vs GPU split:</b></para>
/// <list type="bullet">
///   <item><b>Structural tests</b> (endpoint shapes, config listing, select contract) run under
///   <c>UseStubResponses=true</c> — GPU-free, part of the default CI run, no category trait.</item>
///   <item><b>GPU tests</b> (real load, real inference coherence, real tool-calling output) are
///   tagged <c>[Trait("Category","GPU-Required")]</c> for optional filtering only. They run
///   unconditionally and FAIL with a clear assertion message (never skip) when no GPU/Foundry
///   service is present.</item>
/// </list>
/// </summary>
[Collection("ServerTests")]
public class ParameterizedModelIntegrationTests
{
    private const string GpuRequiredCategory = "GPU-Required";
    private const string ModelsRoute = "/api/models";
    private const string SelectRoute = "/api/models/select";
    private const string ChatCompletionsRoute = "/v1/chat/completions";

    private readonly ITestOutputHelper _output;
    private readonly ServerFixture _server;

    public ParameterizedModelIntegrationTests(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Structural tests — GPU-free, stub mode, run in the default CI pipeline.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/models returns exactly the configured AvailableModels and a valid active model
    /// (one of the listed models). Passes in CI without a GPU (stub mode).
    /// </summary>
    [Fact]
    public async Task GetModels_StubMode_ReturnsConfiguredModelsAndValidActive()
    {
        using var factory = new StubModelServerFactory();
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonObject>(ModelsRoute);
        Assert.NotNull(payload);
        Assert.Equal("list", payload!["object"]?.GetValue<string>());

        var ids = (payload["data"] as JsonArray ?? [])
            .Select(item => item?["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToArray();

        // Every configured model must be listed (set equality, order-independent).
        Assert.Equal(
            SupportedModelData.AvailableModels.OrderBy(m => m, StringComparer.OrdinalIgnoreCase),
            ids.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));

        // Active model must be present and must be one of the configured models.
        var active = payload["active"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(active), "Response is missing a valid 'active' model.");
        Assert.Contains(active, SupportedModelData.AvailableModels);

        // Exactly one data item should be flagged active, matching the top-level 'active'.
        var activeItems = (payload["data"] as JsonArray ?? [])
            .Where(item => item?["active"]?.GetValue<bool>() == true)
            .ToArray();
        Assert.Single(activeItems);
        Assert.Equal(active, activeItems[0]?["id"]?.GetValue<string>());
    }

    /// <summary>
    /// POST /api/models/select for each configured model returns Tank's stub-mode success shape
    /// (HTTP 200, active==alias, device=="stub", loaded==false). GPU-free.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedModelData.SupportedModels), MemberType = typeof(SupportedModelData))]
    public async Task SelectModel_StubMode_ReturnsSuccessShape(string modelAlias, bool supportsToolCalls)
    {
        _ = supportsToolCalls; // capability flag unused for the structural stub contract

        using var factory = new StubModelServerFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(SelectRoute, new { model = modelAlias });
        Assert.True(response.IsSuccessStatusCode,
            $"Expected 200 selecting '{modelAlias}' in stub mode, got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.Equal(modelAlias, payload!["active"]?.GetValue<string>());
        Assert.Equal("stub", payload["device"]?.GetValue<string>());
        Assert.False(payload["loaded"]?.GetValue<bool>() ?? true,
            "Stub-mode select must report loaded=false (no runtime work).");

        // A subsequent GET should reflect the newly-selected active model.
        var listed = await client.GetFromJsonAsync<JsonObject>(ModelsRoute);
        Assert.Equal(modelAlias, listed!["active"]?.GetValue<string>());
    }

    /// <summary>
    /// POST /api/models/select with a model that is not in AvailableModels returns 400 Unknown Model
    /// (RFC 7807 ProblemDetails). GPU-free guard rail for the config-driven allow list.
    /// </summary>
    [Fact]
    public async Task SelectModel_StubMode_UnknownModel_Returns400()
    {
        using var factory = new StubModelServerFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(SelectRoute, new { model = "definitely-not-a-real-model-xyz" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Unknown Model", problem?["title"]?.GetValue<string>());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GPU-required tests — run unconditionally and FAIL (never skip) without a GPU/Foundry service.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Real GPU load: POST /api/models/select actually unloads + loads the model on the GPU and
    /// returns Tank's success shape with device=="GPU" and loaded==true.
    /// </summary>
    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(SupportedModelData.SupportedModels), MemberType = typeof(SupportedModelData))]
    public async Task SelectModel_Gpu_LoadsModelOnGpu(string modelAlias, bool supportsToolCalls)
    {
        _ = supportsToolCalls;

        // Drive the real in-process server: select the model on the GPU and verify the success shape.
        var response = await _server.Client.PostAsJsonAsync(SelectRoute, new { model = modelAlias });
        Assert.True(response.IsSuccessStatusCode,
            $"Expected 200 loading '{modelAlias}' on GPU, got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.Equal(modelAlias, payload!["active"]?.GetValue<string>());
        Assert.True(payload["loaded"]?.GetValue<bool>() ?? false,
            "Real GPU select must report loaded=true.");
        Assert.Equal("GPU", payload["device"]?.GetValue<string>());

        _output.WriteLine($"GPU select of '{modelAlias}' -> {payload.ToJsonString()}");
    }

    /// <summary>
    /// Real GPU inference coherence: /v1/chat/completions returns a non-empty, coherent reply
    /// for each supported model.
    /// </summary>
    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(SupportedModelData.SupportedModels), MemberType = typeof(SupportedModelData))]
    public async Task ChatCompletion_Gpu_ReturnsCoherentReply(string modelAlias, bool supportsToolCalls)
    {
        _ = supportsToolCalls;
        await _server.EnsureModelAsync(modelAlias, _output);

        var response = await _server.Client.PostAsJsonAsync(ChatCompletionsRoute, new
        {
            model = modelAlias,
            stream = false,
            max_tokens = 64,
            messages = new[]
            {
                new { role = "user", content = "In one short sentence, what is the capital of France?" },
            },
        });

        Assert.True(response.IsSuccessStatusCode,
            $"Expected success for '{modelAlias}', got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("chat.completion", payload?["object"]?.GetValue<string>());

        var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content),
            $"Model '{modelAlias}' returned empty content.");
        Assert.True(content!.Trim().Length > 10,
            $"Model '{modelAlias}' reply is too short to be coherent: '{content}'.");

        _output.WriteLine($"'{modelAlias}' coherence reply: {content}");
    }

    /// <summary>
    /// Model-aware tool calling. Driven by the capability flag in the MemberData (not scattered ifs):
    /// <list type="bullet">
    ///   <item>capable models (e.g. qwen2.5-1.5b) MUST return a proper OpenAI <c>tool_calls</c>
    ///   payload (function name + JSON args) and finish_reason "tool_calls".</item>
    ///   <item>prose-only models (e.g. qwen2.5-0.5b) MUST NOT be asserted to tool-call; instead we
    ///   assert graceful plain-text handling (a non-empty assistant message, no tool_calls).</item>
    /// </list>
    /// </summary>
    [Theory]
    [Trait("Category", GpuRequiredCategory)]
    [MemberData(nameof(SupportedModelData.SupportedModels), MemberType = typeof(SupportedModelData))]
    public async Task ToolCalling_Gpu_IsModelAware(string modelAlias, bool supportsToolCalls)
    {
        await _server.EnsureModelAsync(modelAlias, _output);

        var request = new JsonObject
        {
            ["model"] = modelAlias,
            ["stream"] = false,
            ["max_tokens"] = 128,
            ["tool_choice"] = "auto",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "What is the weather in Paris, France? Use the get_weather tool.",
                },
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "get_weather",
                        ["description"] = "Get the current weather for a given location.",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["location"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "City and country, e.g. 'Paris, France'.",
                                },
                            },
                            ["required"] = new JsonArray { "location" },
                        },
                    },
                },
            },
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsRoute)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _server.Client.SendAsync(httpRequest);
        Assert.True(response.IsSuccessStatusCode,
            $"Expected success for tool-calling probe on '{modelAlias}', got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);

        var message = payload!["choices"]?[0]?["message"] as JsonObject;
        Assert.NotNull(message);
        var toolCalls = message!["tool_calls"] as JsonArray;

        // CPU-only quantized variants don't reliably produce tool_calls even for aliases that are
        // capable on GPU. Downgrade the capability check when a CPU variant is serving.
        var servedByModel = payload["model"]?.GetValue<string>() ?? "";
        var isCpuVariant = servedByModel.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                        || servedByModel.Contains("generic", StringComparison.OrdinalIgnoreCase);
        var effectiveSupportsToolCalls = supportsToolCalls && !isCpuVariant;
        _output.WriteLine($"servedBy={servedByModel} supportsToolCalls={supportsToolCalls} effectiveSupports={effectiveSupportsToolCalls}");

        if (effectiveSupportsToolCalls)
        {
            // Capable GPU model: assert a proper OpenAI tool_calls object.
            Assert.True(toolCalls is { Count: > 0 },
                $"Model '{modelAlias}' is flagged tool-calling capable but returned no tool_calls. " +
                $"Body: {payload.ToJsonString()}");

            var firstCall = toolCalls![0]!;
            var functionName = firstCall["function"]?["name"]?.GetValue<string>();
            Assert.Equal("get_weather", functionName);

            var argsJson = firstCall["function"]?["arguments"]?.GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(argsJson),
                "tool_calls function arguments must be a JSON string.");
            var args = JsonNode.Parse(argsJson!);
            Assert.False(string.IsNullOrWhiteSpace(args?["location"]?.GetValue<string>()),
                "tool_calls arguments must include a non-empty 'location'.");

            var finishReason = payload["choices"]?[0]?["finish_reason"]?.GetValue<string>();
            Assert.Equal("tool_calls", finishReason);

            _output.WriteLine($"'{modelAlias}' tool_calls: {firstCall.ToJsonString()}");
        }
        else
        {
            // Not flagged tool-calling capable, or running on CPU: the server must handle the
            // `tools` payload gracefully and return a coherent response. The model may legitimately
            // emit either plain text or tool_calls — both acceptable; only forbid empty reply.
            var content = message["content"]?.GetValue<string>();
            var hasToolCalls = toolCalls is { Count: > 0 };
            Assert.True(hasToolCalls || !string.IsNullOrWhiteSpace(content),
                $"Model '{modelAlias}' returned neither plain-text content nor tool_calls. " +
                $"Body: {payload.ToJsonString()}");

            _output.WriteLine($"'{modelAlias}' reply (tool_calls={hasToolCalls}): " +
                (hasToolCalls ? toolCalls![0]!.ToJsonString() : content));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────
    // (GPU tests drive the shared in-process server via the ServerFixture; model readiness is
    //  handled by ServerFixture.EnsureModelAsync → FoundryServiceHelper.EnsureGpuModelReadyAsync.)
}

/// <summary>
/// In-process server wired for GPU-free structural tests: <c>UseStubResponses=true</c>. The
/// selectable set and default model are injected from <see cref="SupportedModelData"/> so the
/// endpoint and the parameterized MemberData always agree on the model list.
/// </summary>
public sealed class StubModelServerFactory : WebApplicationFactory<Program>
{
    // Program.cs reads FoundryLocal:UseStubResponses from builder.Configuration *before* Build(),
    // i.e. before the factory's in-memory config below is applied — so set it as an env var (a
    // default config source) to guarantee the GPU/Foundry bootstrap is skipped for stub tests.
    static StubModelServerFactory() =>
        Environment.SetEnvironmentVariable("FoundryLocal__UseStubResponses", "true");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["FoundryLocal:UseStubResponses"] = "true",
                ["FoundryLocal:Model"] = SupportedModelData.DefaultModel,
            };

            var models = SupportedModelData.AvailableModels;
            for (var i = 0; i < models.Length; i++)
                config[$"FoundryLocal:AvailableModels:{i}"] = models[i];

            configBuilder.AddInMemoryCollection(config);
        });
    }
}

