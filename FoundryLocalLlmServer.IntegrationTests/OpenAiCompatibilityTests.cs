using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

namespace FoundryLocalLlmServer.IntegrationTests;

public class OpenAiCompatibilityTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;

    public OpenAiCompatibilityTests(ServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenAiRoute_ReturnsCompletionPayload()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "phi-4",
            messages = new[]
            {
                new { role = "user", content = "summarize this test" },
            },
        });

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(payload);
        Assert.Equal("phi-4", payload["model"]?.GetValue<string>());
        Assert.Contains("summarize this test", payload["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
    }

    [Fact]
    public async Task MicrosoftExtensionsAIChatClient_CanCallOpenAiFormatEndpoint()
    {
        using var client = _factory.CreateClient();
        var endpoint = new Uri("http://localhost/v1");
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            Transport = new HttpClientPipelineTransport(client),
        };

        var sdkClient = new ChatClient(
            "phi-4",
            new ApiKeyCredential("local-test-key"),
            options);

        IChatClient chatClient = sdkClient.AsIChatClient();

        var response = await chatClient.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "confirm openai compatibility")]);

        var text = string.Concat(response.Messages.SelectMany(m => m.Contents.OfType<TextContent>()).Select(c => c.Text));

        Assert.Contains("openai compatibility", text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ServerFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FoundryLocal:Model"] = "phi-4",
                ["FoundryLocal:UseStubResponses"] = "true",
            });
        });
    }
}
