using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FoundryLocalLlmServer.Server;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.Configure<FoundryLocalOptions>(builder.Configuration.GetSection(FoundryLocalOptions.SectionName));
builder.Services.Configure<OllamaFallbackOptions>(builder.Configuration.GetSection(OllamaFallbackOptions.SectionName));

// Mutable Foundry endpoint — updated by auto-discovery at startup and on connection failures
string? _currentFoundryEndpoint = null;
var _endpointLock = new SemaphoreSlim(1, 1);

// Serialize requests to Foundry — concurrent streaming requests crash the service
var _foundryRequestGate = new SemaphoreSlim(1, 1);

async Task<string?> DiscoverFoundryEndpointAsync()
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo("foundry")
        {
            Arguments = "service start",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc != null)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await proc.WaitForExitAsync(cts.Token);
            var combined = await stdoutTask + await stderrTask;
            var match = Regex.Match(combined, @"(?:running|started)\s+on\s+(http://[^\s/]+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.TrimEnd('/');
        }
    }
    catch { /* Foundry CLI not available */ }
    return null;
}

async Task EnsureModelLoadedAsync(string modelAlias, ILogger logger)
{
    try
    {
        logger.LogInformation("Loading model {Model} on Foundry...", modelAlias);
        using var proc = Process.Start(new ProcessStartInfo("foundry")
        {
            Arguments = $"model load {modelAlias} --device GPU --ttl 7200",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await proc.WaitForExitAsync(cts.Token);
            var output = await proc.StandardOutput.ReadToEndAsync();
            logger.LogInformation("Model load result: {Output}", output.Trim());
        }
    }
    catch (Exception ex) { logger.LogWarning(ex, "Model load failed for {Model}", modelAlias); }
}

async Task<string> GetFoundryEndpointAsync(string configuredEndpoint, bool forceRediscovery = false)
{
    if (!forceRediscovery && _currentFoundryEndpoint != null)
        return _currentFoundryEndpoint;

    await _endpointLock.WaitAsync();
    try
    {
        if (!forceRediscovery && _currentFoundryEndpoint != null)
            return _currentFoundryEndpoint;

        // Check if current endpoint is reachable
        var endpointToCheck = _currentFoundryEndpoint ?? configuredEndpoint;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync($"{endpointToCheck}/v1/models");
            if (resp.IsSuccessStatusCode)
            {
                _currentFoundryEndpoint = endpointToCheck;
                return _currentFoundryEndpoint;
            }
        }
        catch { /* not reachable */ }

        // Discover via CLI
        var discovered = await DiscoverFoundryEndpointAsync();
        _currentFoundryEndpoint = discovered ?? configuredEndpoint;
        return _currentFoundryEndpoint;
    }
    finally
    {
        _endpointLock.Release();
    }
}

// Auto-discover Foundry Local endpoint at startup
if (!builder.Configuration.GetValue<bool>("FoundryLocal:UseStubResponses"))
{
    var configuredEndpoint = builder.Configuration["FoundryLocal:Endpoint"] ?? "http://127.0.0.1:5273";
    var discovered = await GetFoundryEndpointAsync(configuredEndpoint);
    if (discovered != configuredEndpoint)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FoundryLocal:Endpoint"] = discovered
        });
    }
}

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
    if (ModelIdMatchesAliasCore(modelId, alias))
        return true;

    // Accept aliases both with and without a dash between the family name and version:
    // "gemma4" <-> "gemma-4", "phi4" <-> "phi-4".
    var dashedAlias = Regex.Replace(alias, "(?<=[A-Za-z])(?=\\d)", "-");
    if (!string.Equals(dashedAlias, alias, StringComparison.OrdinalIgnoreCase)
        && ModelIdMatchesAliasCore(modelId, dashedAlias))
    {
        return true;
    }

    var compactAlias = alias.Replace("-", string.Empty, StringComparison.Ordinal);
    if (!string.Equals(compactAlias, alias, StringComparison.OrdinalIgnoreCase)
        && ModelIdMatchesAliasCore(modelId, compactAlias))
    {
        return true;
    }

    return false;
}

bool ModelIdMatchesAliasCore(string modelId, string alias)
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

app.MapGet("/v1/models", async (IOptions<FoundryLocalOptions> options, IHttpClientFactory httpClientFactory) =>
{
    aliasCache.Clear(); // refresh alias cache when models are re-queried

    var endpoint = await GetFoundryEndpointAsync(options.Value.Endpoint);
    var foundryModels = await GetFoundryModelsAsync(endpoint, httpClientFactory, forceRefresh: true);

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

app.MapPost("/v1/chat/completions", async (HttpContext context, IOptions<FoundryLocalOptions> options, IOptions<OllamaFallbackOptions> ollamaOptions, IHttpClientFactory httpClientFactory, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var requestPayload = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    var foundryOptions = options.Value;
    var ollama = ollamaOptions.Value;

    if (foundryOptions.UseStubResponses)
    {
        var prompt = OpenAiChatHelpers.ExtractLatestUserPrompt(requestPayload);
        var model = requestPayload?["model"]?.GetValue<string>() ?? foundryOptions.Model;
        var stubResponse = OpenAiChatHelpers.CreateStubResponse(model, prompt);

        return Results.Json(stubResponse);
    }

    // Try Foundry first, unless fallback-only mode is enabled
    bool foundryFailed = false;
    try
    {
        // Resolve alias → actual Foundry model ID and learn the model's token cap
            int maxTokensCap = 0;
            var currentEndpoint = await GetFoundryEndpointAsync(foundryOptions.Endpoint);
            string? originalModelAlias = null;
            if (requestPayload?["model"] is JsonNode modelNode)
            {
                var requestedModel = modelNode.GetValue<string>();
                originalModelAlias = requestedModel;
                logger.LogInformation("Resolving model alias: {Model} → endpoint {Endpoint}", requestedModel, currentEndpoint);
                var (resolvedModel, cap) = await ResolveModelAsync(requestedModel, currentEndpoint, httpClientFactory);
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

            // Serialize requests — Foundry Local crashes on concurrent streaming requests
            await _foundryRequestGate.WaitAsync(cancellationToken);
            try
            {
                // Try the request, and if Foundry is unreachable, re-discover and retry once
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var endpoint = await GetFoundryEndpointAsync(foundryOptions.Endpoint, forceRediscovery: attempt > 0);
                    if (attempt > 0)
                    {
                        // Clear model caches so alias resolution uses the new endpoint
                        aliasCache.Clear();
                        cachedModelsExpiry = DateTime.MinValue;

                        // Ensure the model is loaded on the (possibly restarted) Foundry instance
                        var modelToLoad = originalModelAlias ?? foundryOptions.Model;
                        await EnsureModelLoadedAsync(modelToLoad, logger);

                        // Re-resolve model alias against the new endpoint
                        if (requestPayload?["model"] is JsonNode retryModelNode)
                        {
                            var retryRequested = retryModelNode.GetValue<string>();
                            var (retryResolved, retryCap) = await ResolveModelAsync(retryRequested, endpoint, httpClientFactory);
                            if (!string.Equals(retryRequested, retryResolved, StringComparison.OrdinalIgnoreCase))
                                requestPayload["model"] = retryResolved;
                            payloadJson = requestPayload?.ToJsonString() ?? "{}";
                        }

                        logger.LogInformation("Retry with re-discovered endpoint: {Endpoint}", endpoint);
                    }

                    var targetUri = new Uri(new Uri(endpoint), "/v1/chat/completions");
                    logger.LogInformation("Proxying to {TargetUri} (stream={Stream}, attempt={Attempt})", targetUri, isStreaming, attempt);

                    try
                    {
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
                                // Foundry died mid-stream; send [DONE] so the client doesn't hang
                                logger.LogWarning(ex, "SSE stream interrupted, sending [DONE] to client");
                                try { await context.Response.WriteAsync("\ndata: [DONE]\n\n"); } catch { }
                            }
                            return Results.Empty;
                        }
                        
                        // If streaming but failed, or non-streaming with error, fall back to Ollama
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.LogWarning("Foundry returned error {StatusCode}, will try Ollama fallback", response.StatusCode);
                            foundryFailed = true;
                            break; // exit retry loop to try Ollama
                        }

                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        
                        // If Foundry returned an error, try Ollama fallback
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.LogWarning("Foundry returned error {StatusCode}, will try Ollama fallback", response.StatusCode);
                            foundryFailed = true;
                            break; // exit retry loop to try Ollama
                        }
                        
                        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
                    }
                    catch (HttpRequestException) when (attempt == 0)
                    {
                        logger.LogWarning("Foundry unreachable at {Endpoint}, attempting re-discovery...", endpoint);
                        continue; // retry with re-discovery
                    }
                }

                // Should not reach here, but handle gracefully
                logger.LogWarning("Foundry Local unavailable after all attempts");
                foundryFailed = true;
            }
            finally
            {
                _foundryRequestGate.Release();
            }
            
            // If Foundry failed and Ollama is enabled, try Ollama
            if (foundryFailed && ollama.Enabled)
            {
                logger.LogWarning("Foundry Local failed, routing to Ollama fallback");
                
                // Extract data from the already-parsed requestPayload
                var messages = requestPayload?["messages"];
                var model = requestPayload?["model"]?.GetValue<string>() ?? ollama.Model;
                var stream = requestPayload?["stream"]?.GetValue<bool>() ?? false;
                var maxTokens = requestPayload?["max_tokens"]?.GetValue<int>();
                
                // Create a new payload for Ollama with cloned messages
                var ollamaBody = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = messages != null ? JsonNode.Parse(messages.ToJsonString()) : null,
                    ["stream"] = stream,
                };
                
                if (maxTokens.HasValue && maxTokens > 0)
                    ollamaBody["num_predict"] = maxTokens;
                
                var targetUri = new Uri(new Uri(ollama.Endpoint), "/api/chat");
                logger.LogInformation("Proxying to Ollama at {TargetUri} (stream={Stream})", targetUri, stream);
                
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
                    {
                        Content = new StringContent(ollamaBody.ToJsonString()),
                    };
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                    
                    var proxyClient = httpClientFactory.CreateClient();
                    proxyClient.Timeout = TimeSpan.FromMinutes(5);
                    using var response = await proxyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    
                    if (stream && response.IsSuccessStatusCode)
                    {
                        context.Response.StatusCode = (int)response.StatusCode;
                        context.Response.ContentType = "text/event-stream";
                        context.Response.Headers.CacheControl = "no-cache";
                        context.Response.Headers.Connection = "keep-alive";
                        try
                        {
                            await using var stream_response = await response.Content.ReadAsStreamAsync(cancellationToken);
                            using var reader = new StreamReader(stream_response);
                            string? line;
                            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                            {
                                if (line.StartsWith("data: "))
                                {
                                    var jsonStr = line["data: ".Length..];
                                    try
                                    {
                                        var ollamaJson = JsonNode.Parse(jsonStr);
                                        var openAiChunk = ConvertOllamaToOpenAiFormat(ollamaJson, model);
                                        await context.Response.WriteAsync($"data: {openAiChunk.ToJsonString()}\n\n", cancellationToken);
                                    }
                                    catch { }
                                }
                            }
                            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                        }
                        catch (Exception streamEx)
                        {
                            logger.LogWarning(streamEx, "Ollama stream interrupted");
                            try { await context.Response.WriteAsync("\ndata: [DONE]\n\n"); } catch { }
                        }
                        return Results.Empty;
                    }
                    
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    
                    // Convert Ollama response to OpenAI format
                    if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(body))
                    {
                        try
                        {
                            var ollamaResp = JsonNode.Parse(body);
                            var openAiResp = ConvertOllamaToOpenAiFormat(ollamaResp, model);
                            return Results.Json(openAiResp);
                        }
                        catch { }
                    }
                    
                    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
                }
                catch (HttpRequestException ollamaEx)
                {
                    logger.LogError(ollamaEx, "Ollama also unavailable at {Endpoint}", ollama.Endpoint);
                    return Results.Problem(
                        title: "All LLM Backends Unavailable",
                        detail: $"Foundry Local and Ollama are unavailable. Ensure at least one service is running.",
                        statusCode: 503);
                }
            }

            // If we get here, Foundry failed and Ollama is either disabled or also unavailable
            if (foundryFailed)
            {
                return Results.Problem(
                    title: "Foundry Local Unavailable",
                    detail: "Foundry Local is unavailable and Ollama fallback is not enabled.",
                    statusCode: 503);
            }

        // This shouldn't be reached, but return a generic error just in case
        return Results.Problem(
            title: "Internal Server Error",
            detail: "Unexpected error in chat completions handler.",
            statusCode: 500);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception in /v1/chat/completions");
        return Results.Problem(
            title: "Internal Server Error",
            detail: ex.Message,
            statusCode: 500);
    }
});

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles();

// Helper function to convert Ollama format to OpenAI format
JsonObject ConvertOllamaToOpenAiFormat(JsonNode? ollamaNode, string model)
{
    var response = new JsonObject
    {
        ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
        ["object"] = "chat.completion",
        ["created"] = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        ["model"] = model,
        ["choices"] = new JsonArray(),
        ["usage"] = new JsonObject
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
        }
    };
    
    var choices = (JsonArray)response["choices"]!;
    var message = new JsonObject();
    
    if (ollamaNode is JsonObject ollamaObj)
    {
        // Extract message content from Ollama response
        if (ollamaObj["message"] is JsonObject msgObj && msgObj["content"] is JsonValue contentVal)
        {
            var content = contentVal.GetValue<string>();
            message["role"] = "assistant";
            message["content"] = content;
        }
        
        // Parse tokens if available
        var promptTokens = ollamaObj["prompt_eval_count"]?.GetValue<int>() ?? 0;
        var completionTokens = ollamaObj["eval_count"]?.GetValue<int>() ?? 0;
        
        ((JsonObject)response["usage"]!)["prompt_tokens"] = promptTokens;
        ((JsonObject)response["usage"]!)["completion_tokens"] = completionTokens;
        ((JsonObject)response["usage"]!)["total_tokens"] = promptTokens + completionTokens;
    }
    
    choices.Add(new JsonObject
    {
        ["index"] = 0,
        ["message"] = message.Count > 0 ? message : new JsonObject { ["role"] = "assistant", ["content"] = "" },
        ["finish_reason"] = "stop",
    });
    
    return response;
}

app.Run();

public partial class Program;

// ── Supporting types (must follow all top-level statements) ────────────────────

record FoundryModel(string Id, int MaxInputTokens, int MaxOutputTokens, bool ToolCalling)
{
    public int MaxTotalTokens => MaxInputTokens + MaxOutputTokens;
}
