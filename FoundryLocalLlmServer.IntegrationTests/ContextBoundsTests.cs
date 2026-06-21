using System.Net.Http.Json;
using Xunit;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Exposes (and guards the fix for) a bug found in exploratory testing: the proxy shipped a fully
/// implemented <see cref="FoundryLocalLlmServer.Server.OpenAiChatHelpers.ApplyContextBounds"/> to cap
/// max_tokens and trim an ever-growing conversation (the documented defense against the in-process
/// Foundry CUDA KV-cache arena growing to OOM), but the chat handler never called it. These tests
/// assert the bounding is applied and surfaced via diagnostic response headers. They run in stub mode
/// (no GPU) so the bounding is exercised independently of inference.
/// </summary>
[Collection("ServerTests")]
public class ContextBoundsTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;
    public ContextBoundsTests(ServerFactory factory) => _factory = factory;

    [Fact]
    public async Task OversizedMaxTokens_IsCappedToMaxResponseTokens()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "phi-4-mini",
            max_tokens = 999_999,
            messages = new[] { new { role = "user", content = "hello" } },
        });
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("X-Context-Max-Tokens", out var values),
            "Proxy did not surface X-Context-Max-Tokens — ApplyContextBounds is not wired into the handler.");
        var capped = int.Parse(values!.First());
        Assert.True(capped <= 2048, $"max_tokens should be capped to MaxResponseTokens (2048) but was {capped}.");
    }

    [Fact]
    public async Task EverGrowingConversation_IsTrimmedToPromptBudget()
    {
        using var client = _factory.CreateClient();

        // ~80 turns of ~3,000 chars (~750 tokens each ≈ 60k tokens) — far over the 8,192 prompt budget.
        var big = new string('x', 3000);
        var messages = Enumerable.Range(0, 80)
            .Select(i => new { role = i % 2 == 0 ? "user" : "assistant", content = $"{big} #{i}" })
            .ToArray();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "phi-4-mini", messages });
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("X-Context-Dropped-Messages", out var values),
            "Proxy did not surface X-Context-Dropped-Messages — prompt trimming is not wired into the handler.");
        var dropped = int.Parse(values!.First());
        Assert.True(dropped > 0, $"An oversized conversation should have dropped old messages but dropped {dropped}.");
    }
}
