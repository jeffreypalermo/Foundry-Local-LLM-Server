using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FoundryLocalLlmServer.Server;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.Configure<FoundryLocalOptions>(builder.Configuration.GetSection(FoundryLocalOptions.SectionName));

// In-process Foundry Local v1.2 SDK bootstrap: registers the CUDA EP, downloads/loads
// the model, and starts the SDK's embedded OpenAI-compatible web service in the
// background. Endpoints return 503 with readiness detail until it is ready.
builder.Services.AddSingleton<FoundryLocalBootstrapper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FoundryLocalBootstrapper>());

// Serialize requests to Foundry — concurrent streaming requests crash the runtime
var _foundryRequestGate = new SemaphoreSlim(1, 1);

var app = builder.Build();

app.UseExceptionHandler();

// Readiness gate: null when ready, otherwise a 503 ProblemDetails describing progress.
IResult? NotReadyProblem(FoundryLocalBootstrapper bootstrapper)
{
    var status = bootstrapper.Status;
    if (status.State == FoundryReadinessState.Ready)
        return null;

    var detail = status.State switch
    {
        FoundryReadinessState.NotStarted => "Foundry Local initialization has not started yet.",
        FoundryReadinessState.CreatingManager => "Initializing the Foundry Local runtime...",
        FoundryReadinessState.RegisteringExecutionProvider =>
            $"Downloading/registering execution provider {status.ExecutionProvider} ({status.EpDownloadPercent:F1}%). First run can take a long time.",
        FoundryReadinessState.DownloadingModel =>
            $"Downloading model {status.ModelId} ({status.ModelDownloadPercent:F1}%).",
        FoundryReadinessState.LoadingModel => $"Loading model {status.ModelId} into GPU memory...",
        FoundryReadinessState.StartingWebService => "Starting the Foundry inference service...",
        FoundryReadinessState.Failed => $"Foundry Local initialization failed: {status.LastError}",
        _ => "Foundry Local is not ready.",
    };

    return Results.Problem(
        title: "Foundry Local Not Ready",
        detail: detail,
        statusCode: 503,
        extensions: new Dictionary<string, object?> { ["foundryStatus"] = status });
}

app.MapGet("/api/foundry", (IOptions<FoundryLocalOptions> options, FoundryLocalBootstrapper bootstrapper) =>
{
    var value = options.Value;
    return Results.Ok(new
    {
        Endpoint = bootstrapper.Endpoint ?? value.Endpoint,
        value.Model,
        bootstrapper.Status.State,
    });
});

// Detailed readiness/status: state, EP + model download progress, last error.
app.MapGet("/api/foundry/status", (FoundryLocalBootstrapper bootstrapper) =>
    Results.Ok(bootstrapper.Status));

// Cache of alias → (resolvedId, maxTotalTokens) — cleared when /v1/models is refreshed.
// maxTotalTokens = maxInputTokens + maxOutputTokens as reported by Foundry.
// Foundry enforces this as the hard limit for the max_tokens parameter.
var aliasCache = new ConcurrentDictionary<string, (string ResolvedId, int MaxTokens)>(StringComparer.OrdinalIgnoreCase);

// Cached models list with expiry — avoids hammering Foundry on every request
FoundryModel[] cachedModels = [];
DateTime cachedModelsExpiry = DateTime.MinValue;
var cachedModelsLock = new SemaphoreSlim(1, 1);

// Safe fallback: cap max_tokens when model resolution fails to prevent Foundry OOM crashes
const int DefaultMaxTokensFallback = 4096;

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

async Task<FoundryModel[]> GetFoundryModelsAsync(string endpoint, IHttpClientFactory factory, bool forceRefresh = false)
{
    if (!forceRefresh && cachedModels.Length > 0 && DateTime.UtcNow < cachedModelsExpiry)
        return cachedModels;

    await cachedModelsLock.WaitAsync();
    try
    {
        // Double-check after acquiring lock
        if (!forceRefresh && cachedModels.Length > 0 && DateTime.UtcNow < cachedModelsExpiry)
            return cachedModels;

        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var resp = await client.GetAsync(new Uri(new Uri(endpoint), "/v1/models"));
        if (!resp.IsSuccessStatusCode) return cachedModels; // return stale cache

        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        var models = json?["data"]?.AsArray()
            .Select(n => new FoundryModel(
                n?["id"]?.GetValue<string>() ?? string.Empty,
                n?["maxInputTokens"]?.GetValue<int>() ?? 0,
                n?["maxOutputTokens"]?.GetValue<int>() ?? 0,
                n?["toolCalling"]?.GetValue<bool>() ?? false))
            .Where(m => m.Id.Length > 0)
            .ToArray() ?? [];

        if (models.Length > 0)
        {
            cachedModels = models;
            cachedModelsExpiry = DateTime.UtcNow.AddSeconds(30);
        }
        return cachedModels;
    }
    catch
    {
        return cachedModels; // return stale cache on error
    }
    finally
    {
        cachedModelsLock.Release();
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

app.MapGet("/v1/models", async (IOptions<FoundryLocalOptions> options, IHttpClientFactory httpClientFactory, FoundryLocalBootstrapper bootstrapper) =>
{
    var foundryOptions = options.Value;

    if (foundryOptions.UseStubResponses)
    {
        return Results.Ok(new
        {
            @object = "list",
            data = new[]
            {
                new { id = foundryOptions.Model, @object = "model", created = 0, owned_by = "foundry-local" }
            }
        });
    }

    if (NotReadyProblem(bootstrapper) is IResult notReady)
        return notReady;

    aliasCache.Clear(); // refresh alias cache when models are re-queried

    var foundryModels = await GetFoundryModelsAsync(bootstrapper.Endpoint!, httpClientFactory, forceRefresh: true);

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
                maxInputTokens = m.MaxInputTokens,
                maxOutputTokens = m.MaxOutputTokens,
                toolCalling = m.ToolCalling,
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

app.MapPost("/v1/chat/completions", async (HttpContext context, IOptions<FoundryLocalOptions> options, IHttpClientFactory httpClientFactory, FoundryLocalBootstrapper bootstrapper, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
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

        // 503 with readiness detail until the in-process runtime is fully up
        if (NotReadyProblem(bootstrapper) is IResult notReady)
            return notReady;

        var endpoint = bootstrapper.Endpoint!;

        // Resolve alias → actual Foundry model ID and learn the model's token cap
        int maxTokensCap = 0;
        if (requestPayload?["model"] is JsonNode modelNode)
        {
            var requestedModel = modelNode.GetValue<string>();
            logger.LogInformation("Resolving model alias: {Model} → endpoint {Endpoint}", requestedModel, endpoint);
            var (resolvedModel, cap) = await ResolveModelAsync(requestedModel, endpoint, httpClientFactory);
            maxTokensCap = cap;
            logger.LogInformation("Resolved model: {Resolved} (cap={Cap})", resolvedModel, cap);
            if (!string.Equals(requestedModel, resolvedModel, StringComparison.OrdinalIgnoreCase))
                requestPayload["model"] = resolvedModel;
        }

        // Cap max_tokens to the model's total context window to prevent OOM/400 errors
        var effectiveCap = maxTokensCap > 0 ? maxTokensCap : DefaultMaxTokensFallback;
        if (requestPayload?["max_tokens"] is JsonNode maxTokensNode)
        {
            var requested = maxTokensNode.GetValue<int>();
            if (requested > effectiveCap)
            {
                logger.LogInformation("Capping max_tokens from {Requested} to {Cap}", requested, effectiveCap);
                requestPayload["max_tokens"] = effectiveCap;
            }
        }

        var isStreaming = requestPayload?["stream"]?.GetValue<bool>() == true;
        var payloadJson = requestPayload?.ToJsonString() ?? "{}";

        // Serialize requests — Foundry crashes on concurrent streaming requests
        await _foundryRequestGate.WaitAsync(cancellationToken);
        try
        {
            var targetUri = new Uri(new Uri(endpoint), "/v1/chat/completions");
            logger.LogInformation("Proxying to {TargetUri} (stream={Stream})", targetUri, isStreaming);

            using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
            {
                Content = new StringContent(payloadJson),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

            if (!string.IsNullOrWhiteSpace(foundryOptions.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", foundryOptions.ApiKey);

            var proxyClient = httpClientFactory.CreateClient();
            using var response = await proxyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (isStreaming && response.IsSuccessStatusCode)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await stream.CopyToAsync(context.Response.Body, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Inference died mid-stream; send [DONE] so the client doesn't hang
                    logger.LogWarning(ex, "SSE stream interrupted, sending [DONE] to client");
                    try { await context.Response.WriteAsync("\ndata: [DONE]\n\n"); } catch { }
                }
                return Results.Empty;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
        }
        finally
        {
            _foundryRequestGate.Release();
        }
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "Foundry Local inference service unreachable");
        return Results.Problem(
            title: "Foundry Local Unavailable",
            detail: $"Could not reach the in-process Foundry inference service. ({ex.Message})",
            statusCode: 503);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled error in /v1/chat/completions");
        return Results.Problem(
            title: "Internal Server Error",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

public partial class Program;

// ── Supporting types (must follow all top-level statements) ────────────────────

record FoundryModel(string Id, int MaxInputTokens, int MaxOutputTokens, bool ToolCalling)
{
    public int MaxTotalTokens => MaxInputTokens + MaxOutputTokens;
}
