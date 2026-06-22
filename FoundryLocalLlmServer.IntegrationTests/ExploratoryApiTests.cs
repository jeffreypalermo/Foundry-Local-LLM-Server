using System.Net;
using System.Net.Http.Headers;
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

    // Exploratory: /api/models/select with a malformed body must be a 400, not a 500.
    [Fact]
    public async Task SelectModel_MalformedJson_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{ nope ", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/models/select", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: a non-array "messages" is invalid input; the proxy must reject it cleanly (400),
    // not throw (AsArray() on a JsonValue) and surface a 500.
    [Fact]
    public async Task ChatCompletions_MessagesNotArray_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":\"phi-4-mini\",\"messages\":\"hello\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: an empty "messages" array is invalid (OpenAI requires a non-empty array). It must
    // be a clean 400 — not forwarded to the daemon (which returns a confusing 404 "model not found")
    // nor answered by the stub.
    [Theory]
    [InlineData("{\"model\":\"phi-4-mini\",\"messages\":[]}")]
    [InlineData("{\"model\":\"phi-4-mini\"}")]
    public async Task ChatCompletions_EmptyOrMissingMessages_Returns400(string body)
    {
        using var client = _factory.CreateClient();
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: a non-string "model" must not crash the select endpoint (GetValue<string>() throws
    // on a JSON number) — it should be a clean 400.
    [Fact]
    public async Task SelectModel_NonStringModel_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":123}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/models/select", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: a non-string "model" in a chat request must not surface as a 500 — the proxy should
    // fall back to the selected default rather than throw in GetValue<string>().
    [Fact]
    public async Task ChatCompletions_NonStringModel_DoesNotCrash()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":123,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // Exploratory: a "messages" element that isn't an object (e.g. a bare number) is invalid input.
    // ApplyContextBounds indexes each element (m["role"]) which throws on a JsonValue → 500. Must be 400.
    [Fact]
    public async Task ChatCompletions_NonObjectMessageElement_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":\"phi-4-mini\",\"messages\":[123]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: a non-string "role" (GetValue<string>() throws) is invalid input → 400, not 500.
    [Fact]
    public async Task ChatCompletions_NonStringRole_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":\"phi-4-mini\",\"messages\":[{\"role\":5,\"content\":\"hi\"}]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: numeric "content" (MessageText calls GetValue<string>() on it) must not crash the
    // proxy — the bounding/prompt helpers should tolerate it rather than surface a 500.
    [Fact]
    public async Task ChatCompletions_NumericContent_DoesNotCrash()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("{\"model\":\"phi-4-mini\",\"messages\":[{\"role\":\"user\",\"content\":123}]}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // Exploratory: a non-boolean "stream" (string or number) must not crash the proxy. GetValue<bool>()
    // throws on a non-bool JsonValue → 500. A non-bool should be treated as non-streaming (false).
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task ChatCompletions_NonBooleanStream_DoesNotCrash(string streamValue)
    {
        using var client = _factory.CreateClient();
        var body = $"{{\"model\":\"phi-4-mini\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}],\"stream\":{streamValue}}}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        // A non-bool stream means non-streaming: a normal JSON body, not an SSE stream.
        Assert.DoesNotContain("text/event-stream", response.Content.Headers.ContentType?.ToString() ?? "");
    }

    // Exploratory: a body that is valid JSON but not an object (array, number, string, bool) must be a
    // clean 400 — indexing a JsonArray/JsonValue with a string property name throws → 500 otherwise.
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    [InlineData("true")]
    public async Task ChatCompletions_NonObjectBody_Returns400_Not500(string body)
    {
        using var client = _factory.CreateClient();
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    [InlineData("true")]
    public async Task SelectModel_NonObjectBody_Returns400_Not500(string body)
    {
        using var client = _factory.CreateClient();
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/models/select", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory (security): the transcription "model" form field is interpolated into the foundry
    // CLI argument string. A value that isn't an allow-listed alias (here, one carrying an injected
    // "-f" argument) must be rejected with a 400 before it can reach the process — mirroring how the
    // language field is validated and how /api/models/select allow-lists the model.
    [Fact]
    public async Task Transcription_ModelNotInAllowlist_Returns400()
    {
        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "clip.wav");
        form.Add(new StringContent("whisper-base -f C:\\victim.wav"), "model");

        var response = await client.PostAsync("/v1/audio/transcriptions", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Exploratory: a multipart content-type with a malformed body makes ReadFormAsync throw
    // (InvalidDataException). That call is not guarded, so it surfaces as 500. Should be a clean 400.
    [Fact]
    public async Task Transcription_MalformedMultipart_Returns400_Not500()
    {
        using var client = _factory.CreateClient();
        var content = new StringContent("this is not a valid multipart body", Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=----WebKitBoundaryXYZ");

        var response = await client.PostAsync("/v1/audio/transcriptions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Round 4: content/value-shape robustness — none of these may surface a 500 ──────────────

    private async Task<HttpResponseMessage> PostChat(string body)
    {
        var client = _factory.CreateClient();
        return await client.PostAsync("/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    [Fact] // vision-style content: text + image_url parts (object form)
    public async Task ChatCompletions_VisionImageUrlParts_DoesNotCrash()
    {
        var body = "{\"model\":\"qwen3-vl-2b-instruct\",\"messages\":[{\"role\":\"user\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"What is this?\"},"
            + "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,iVBORw0KGgo=\"}}]}]}";
        var r = await PostChat(body);
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // malformed vision part: image_url is a bare string, not an object
    public async Task ChatCompletions_ImageUrlAsString_DoesNotCrash()
    {
        var body = "{\"model\":\"qwen3-vl-2b-instruct\",\"messages\":[{\"role\":\"user\",\"content\":["
            + "{\"type\":\"image_url\",\"image_url\":\"not-an-object\"}]}]}";
        var r = await PostChat(body);
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // a single oversized message exercises ApplyContextBounds' head-truncation branch
    public async Task ChatCompletions_HugeContent_IsBoundedNot500()
    {
        var huge = new string('x', 1_000_000);
        var body = $"{{\"model\":\"phi-4-mini\",\"messages\":[{{\"role\":\"user\",\"content\":\"{huge}\"}}]}}";
        var r = await PostChat(body);
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // content: null is valid (e.g. assistant tool-call messages) — must not crash
    public async Task ChatCompletions_NullContent_DoesNotCrash()
    {
        var r = await PostChat("{\"model\":\"phi-4-mini\",\"messages\":[{\"role\":\"user\",\"content\":null}]}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // a very long conversation must be trimmed, not crash or hang
    public async Task ChatCompletions_ManyMessages_IsTrimmedNot500()
    {
        var sb = new StringBuilder("{\"model\":\"phi-4-mini\",\"messages\":[");
        for (var i = 0; i < 2000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"role\":\"user\",\"content\":\"message ").Append(i).Append("\"}");
        }
        sb.Append("]}");
        var r = await PostChat(sb.ToString());
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Theory] // boundary max_tokens values must not crash (huge / negative / zero)
    [InlineData("999999999")]
    [InlineData("-5")]
    [InlineData("0")]
    public async Task ChatCompletions_BoundaryMaxTokens_DoesNotCrash(string mt)
    {
        var body = $"{{\"model\":\"phi-4-mini\",\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}}],\"max_tokens\":{mt}}}";
        var r = await PostChat(body);
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // unicode / emoji / control chars in content must round-trip without crashing
    public async Task ChatCompletions_UnicodeContent_DoesNotCrash()
    {
        var r = await PostChat("{\"model\":\"phi-4-mini\",\"messages\":[{\"role\":\"user\",\"content\":\"héllo 🌍 日本語 \\u0001\\u0007\"}]}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    [Fact] // a conversation with no user message (only system) must not crash
    public async Task ChatCompletions_OnlySystemMessage_DoesNotCrash()
    {
        var r = await PostChat("{\"model\":\"phi-4-mini\",\"messages\":[{\"role\":\"system\",\"content\":\"You are helpful.\"}]}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode);
    }
}
