using System.Net;
using System.Text;
using Xunit;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Bugs found during exploratory testing of the API surface, each captured as a regression test.
/// Stub mode (no GPU) so they exercise the proxy's request handling, not inference.
/// </summary>
[Collection("ServerTests")]
public class ExploratoryApiTests : IClassFixture<ServerFactory>
{
    private readonly ServerFactory _factory;
    public ExploratoryApiTests(ServerFactory factory) => _factory = factory;

    // Exploratory: a malformed JSON body is a client error and must not surface as a 500.
    [Fact]
    public async Task MalformedJsonBody_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{ this is not valid json ", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
