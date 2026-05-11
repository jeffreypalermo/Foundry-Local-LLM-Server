using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FoundryLocalLlmServer.Server;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.Configure<FoundryLocalOptions>(builder.Configuration.GetSection(FoundryLocalOptions.SectionName));

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/foundry", (IOptions<FoundryLocalOptions> options) =>
{
    var value = options.Value;
    return Results.Ok(new
    {
        value.Endpoint,
        value.Model,
    });
});

app.MapGet("/v1/models", (IOptions<FoundryLocalOptions> options) =>
{
    var model = options.Value.Model;
    return Results.Ok(new
    {
        @object = "list",
        data = new[]
        {
            new
            {
                id = model,
                @object = "model",
                created = 0,
                owned_by = "foundry-local",
            }
        }
    });
});

app.MapPost("/v1/chat/completions", async (HttpContext context, IOptions<FoundryLocalOptions> options, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
{
    var requestPayload = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var foundryOptions = options.Value;

    if (foundryOptions.UseStubResponses)
    {
        var prompt = OpenAiChatHelpers.ExtractLatestUserPrompt(requestPayload);
        var model = requestPayload?["model"]?.GetValue<string>() ?? foundryOptions.Model;
        var stubResponse = OpenAiChatHelpers.CreateStubResponse(model, prompt);

        return Results.Json(stubResponse);
    }

    var targetUri = new Uri(new Uri(foundryOptions.Endpoint), "/v1/chat/completions");
    using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
    {
        Content = new StringContent(requestPayload?.ToJsonString() ?? "{}"),
    };

    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

    if (!string.IsNullOrWhiteSpace(foundryOptions.ApiKey))
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", foundryOptions.ApiKey);
    }

    var proxyClient = httpClientFactory.CreateClient();
    try
    {
        using var response = await proxyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(
            title: "Foundry Local Unavailable",
            detail: $"Could not reach Foundry Local at {foundryOptions.Endpoint}. Ensure the service is running. ({ex.Message})",
            statusCode: 503);
    }
});

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

public partial class Program;
