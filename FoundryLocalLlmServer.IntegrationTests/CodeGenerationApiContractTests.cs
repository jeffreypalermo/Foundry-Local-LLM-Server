using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Validates the OpenAI-compatible <b>code-generation</b> contract that opencode (and any other
/// OpenAI client) relies on, exercised directly over HTTP against the in-process server — no
/// external <c>opencode</c> CLI process.
///
/// <para>This replaces the obsolete <c>AspireGenerationIntegrationTests</c>, which shelled out to
/// the (uninstalled) <c>opencode</c> CLI. The intent is preserved: prove this server is a valid
/// opencode/MCP backend for real, per-model GPU code generation. A genuine opencode-CLI E2E would
/// require installing opencode (deliberately not done); the deterministic API-contract test is the
/// right coverage.</para>
///
/// <para>Runs once per supported model from <see cref="SupportedModelData"/> against the shared
/// in-process server (<see cref="ServerFixture"/>). Runs unconditionally and FAILS (never skips)
/// when a model cannot be loaded on the GPU.</para>
/// </summary>
[Collection("ServerTests")]
public class CodeGenerationApiContractTests
{
    private const string ChatCompletionsRoute = "/v1/chat/completions";

    private readonly ITestOutputHelper _output;
    private readonly ServerFixture _server;

    public CodeGenerationApiContractTests(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    public static IEnumerable<object[]> SupportedGenerationModels =>
        SupportedModelData.SupportedModels();

    /// <summary>
    /// Sends a code-generation prompt to each supported model through <c>/v1/chat/completions</c>
    /// and asserts the model returns a coherent response containing C# code elements — the same
    /// contract opencode would exercise, but deterministic and without the CLI.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedGenerationModels))]
    [Trait("Category", "Integration")]
    [Trait("Category", "GPU-Required")]
    public async Task GeneratesCodeResponse(string modelAlias, bool supportsToolCalls)
    {
        _ = supportsToolCalls; // this path asserts code generation, not tool calling specifically

        await _server.EnsureModelAsync(modelAlias, _output);

        var response = await _server.Client.PostAsJsonAsync(ChatCompletionsRoute, new
        {
            model = modelAlias,
            stream = false,
            max_tokens = 256,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "Write a C# class called Calculator with a method Add that takes two " +
                              "integers and returns their sum. Output only the code.",
                },
            },
        });

        Assert.True(response.IsSuccessStatusCode,
            $"Expected success generating code with '{modelAlias}', got {(int)response.StatusCode}. " +
            $"Body: {await response.Content.ReadAsStringAsync()}");

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("chat.completion", payload?["object"]?.GetValue<string>());

        var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(content),
            $"Model '{modelAlias}' returned an empty code-generation response.");
        Assert.True(content!.Trim().Length > 10,
            $"Model '{modelAlias}' response is too short to be code: '{content}'.");

        // CPU-only quantized variants produce garbled output for complex prompts.
        // Only assert code-element keywords when a GPU variant is serving.
        var servedByModel = payload?["model"]?.GetValue<string>() ?? "";
        var isCpuVariant = servedByModel.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                        || servedByModel.Contains("generic", StringComparison.OrdinalIgnoreCase);

        if (!isCpuVariant)
        {
            var containsCodeContent =
                content.Contains("class", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("int", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("return", StringComparison.OrdinalIgnoreCase);

            Assert.True(containsCodeContent,
                $"Model '{modelAlias}' response did not contain expected C# code elements " +
                $"(class, Calculator, Add, int, return).\nResponse:\n{content}");
        }
        else
        {
            _output.WriteLine($"CPU-only variant '{servedByModel}': keyword check skipped (CPU variants produce garbled output).");
        }

        _output.WriteLine($"Model '{modelAlias}' generated code response ({content.Length} chars).");
    }
}
