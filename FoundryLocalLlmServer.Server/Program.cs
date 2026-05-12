using System.Collections.Concurrent;
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

// Cache of alias → (resolvedId, maxTotalTokens) — cleared when /v1/models is refreshed.
// maxTotalTokens = maxInputTokens + maxOutputTokens as reported by Foundry.
// Foundry enforces this as the hard limit for the max_tokens parameter.
var aliasCache = new ConcurrentDictionary<string, (string ResolvedId, int MaxTokens)>(StringComparer.OrdinalIgnoreCase);

// Token words that immediately follow the alias in Foundry model IDs (e.g. "phi-4-cuda-gpu:4")
// "mini", "reasoning", etc. are NOT here because they extend the alias, not terminate it.
string[] backendTokens = ["cuda", "openvino", "generic", "trtrtx", "npu", "gpu", "cpu", "instruct"];

bool IsGpuVariant(string modelId)
{
    var lower = modelId.ToLowerInvariant();
    if (lower.Contains("cuda") || lower.Contains("trtrtx"))
        return true;
    if (lower.Contains("-gpu") && !lower.Contains("npu"))
        return true;
    return false;
}

bool ModelIdMatchesAlias(string modelId, string alias)
{
    if (string.Equals(modelId, alias, StringComparison.OrdinalIgnoreCase)) return true;
    if (modelId.StartsWith(alias + ":", StringComparison.OrdinalIgnoreCase)) return true;
    if (!modelId.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)) return false;

    var afterAlias = modelId[(alias.Length + 1)..].ToLowerInvariant();
    return backendTokens.Any(t => afterAlias == t
        || afterAlias.StartsWith(t + "-")
        || afterAlias.StartsWith(t + ":"));
}

async Task<FoundryModel[]> GetFoundryModelsAsync(string endpoint, IHttpClientFactory factory)
{
    try
    {
        using var client = factory.CreateClient();
        var resp = await client.GetAsync(new Uri(new Uri(endpoint), "/v1/models"));
        if (!resp.IsSuccessStatusCode) return [];

        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        return json?["data"]?.AsArray()
            .Select(n => new FoundryModel(
                n?["id"]?.GetValue<string>() ?? string.Empty,
                n?["maxInputTokens"]?.GetValue<int>() ?? 0,
                n?["maxOutputTokens"]?.GetValue<int>() ?? 0))
            .Where(m => m.Id.Length > 0)
            .ToArray() ?? [];
    }
    catch
    {
        return [];
    }
}

async Task<(string ResolvedId, int MaxTokens)> ResolveModelAsync(string requestedModel, string endpoint, IHttpClientFactory factory)
{
    if (aliasCache.TryGetValue(requestedModel, out var cached))
        return cached;

    var foundryModels = await GetFoundryModelsAsync(endpoint, factory);

    // If Foundry already knows this exact ID, use it as-is
    var exact = foundryModels.FirstOrDefault(m =>
        string.Equals(m.Id, requestedModel, StringComparison.OrdinalIgnoreCase));
    if (exact != null)
    {
        var entry = (exact.Id, exact.MaxTotalTokens);
        aliasCache[requestedModel] = entry;
        return entry;
    }

    // Find matching models and prefer GPU variants over NPU
    var matches = foundryModels.Where(m => ModelIdMatchesAlias(m.Id, requestedModel)).ToArray();
    var gpuMatch = matches.FirstOrDefault(m => IsGpuVariant(m.Id));
    var match = gpuMatch ?? matches.FirstOrDefault();
    var result = match != null
        ? (match.Id, match.MaxTotalTokens)
        : (requestedModel, 0); // fall back; 0 means no capping

    aliasCache[requestedModel] = result;
    return result;
}

app.MapGet("/v1/models", async (IOptions<FoundryLocalOptions> options, IHttpClientFactory httpClientFactory) =>
{
    aliasCache.Clear(); // refresh alias cache when models are re-queried

    var foundryModels = await GetFoundryModelsAsync(options.Value.Endpoint, httpClientFactory);

    // If Foundry returned real models, proxy them through; otherwise return the configured default
    if (foundryModels.Length > 0)
    {
        return Results.Ok(new
        {
            @object = "list",
            data = foundryModels.Select(m => new
            {
                id = m.Id,
                @object = "model",
                created = 0,
                owned_by = "foundry-local",
            }).ToArray()
        });
    }

    var model = options.Value.Model;
    return Results.Ok(new
    {
        @object = "list",
        data = new[]
        {
            new { id = model, @object = "model", created = 0, owned_by = "foundry-local" }
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

    // Resolve alias → actual Foundry model ID and learn the model's token cap
    int maxTokensCap = 0;
    if (requestPayload?["model"] is JsonNode modelNode)
    {
        var requestedModel = modelNode.GetValue<string>();
        var (resolvedModel, cap) = await ResolveModelAsync(requestedModel, foundryOptions.Endpoint, httpClientFactory);
        maxTokensCap = cap;
        if (!string.Equals(requestedModel, resolvedModel, StringComparison.OrdinalIgnoreCase))
            requestPayload["model"] = resolvedModel;
    }

    // Cap max_tokens to the model's total context window to prevent 400 Bad Request
    if (maxTokensCap > 0 && requestPayload?["max_tokens"] is JsonNode maxTokensNode)
    {
        var requested = maxTokensNode.GetValue<int>();
        if (requested > maxTokensCap)
            requestPayload["max_tokens"] = maxTokensCap;
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

// ── Supporting types (must follow all top-level statements) ────────────────────

record FoundryModel(string Id, int MaxInputTokens, int MaxOutputTokens)
{
    public int MaxTotalTokens => MaxInputTokens + MaxOutputTokens;
}
