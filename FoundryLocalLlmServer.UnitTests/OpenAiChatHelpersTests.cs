using System.Text.Json.Nodes;
using FoundryLocalLlmServer.Server;

namespace FoundryLocalLlmServer.UnitTests;

public class OpenAiChatHelpersTests
{
    [Fact]
    public void ExtractLatestUserPrompt_ReturnsLatestUserText()
    {
        var payload = JsonNode.Parse(
            """
            {
              "messages": [
                { "role": "system", "content": "You are helpful" },
                { "role": "user", "content": "first question" },
                { "role": "assistant", "content": "answer" },
                { "role": "user", "content": "latest question" }
              ]
            }
            """);

        var prompt = OpenAiChatHelpers.ExtractLatestUserPrompt(payload);

        Assert.Equal("latest question", prompt);
    }

    [Fact]
    public void CreateStubResponse_ContainsModelAndPrompt()
    {
        var response = OpenAiChatHelpers.CreateStubResponse("phi-4", "hello from test");

        Assert.Equal("chat.completion", response["object"]?.GetValue<string>());
        Assert.Equal("phi-4", response["model"]?.GetValue<string>());
        Assert.Contains("hello from test", response["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
    }
}
