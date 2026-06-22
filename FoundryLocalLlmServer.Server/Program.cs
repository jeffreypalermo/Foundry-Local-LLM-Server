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
// Allow browser clients served from other origins (e.g. the Blazor WASM client on its own port, or
// the Vite dev server) to call the API directly — proving multi-client access to one server process.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("X-Context-Dropped-Messages", "X-Context-Max-Tokens", "X-Context-Input-Tokens")));
builder.Services.Configure<FoundryLocalOptions>(builder.Configuration.GetSection(FoundryLocalOptions.SectionName));
builder.Services.Configure<OllamaFallbackOptions>(builder.Configuration.GetSection(OllamaFallbackOptions.SectionName));

// Mutable Foundry endpoint — updated by auto-discovery at startup and on connection failures
string? _currentFoundryEndpoint = null;
var _endpointLock = new SemaphoreSlim(1, 1);

// Mutable active model — initialized from config, changed at runtime via POST /api/models/select.
// Used as the default whenever a chat request omits an explicit "model" and surfaced by /api/foundry
// so the SPA shows (and sends) the current selection.
string _selectedModel = builder.Configuration["FoundryLocal:Model"] ?? "phi-4-mini";
var _selectedModelLock = new object();
string GetSelectedModel() { lock (_selectedModelLock) return _selectedModel; }

// Serialize requests to Foundry — concurrent streaming requests crash the service
var _foundryRequestGate = new SemaphoreSlim(1, 1);

// Runs the `foundry` CLI (cross-platform v1.x / CLI 0.10+) and returns combined stdout+stderr.
// Returns null if the CLI is unavailable or times out.
static async Task<string?> RunFoundryAsync(string arguments, int timeoutSeconds)
{
    try
    {
        using var proc = Process.Start(new ProcessStartInfo("foundry")
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc == null) return null;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await proc.WaitForExitAsync(cts.Token);
        return await stdoutTask + await stderrTask;
    }
    catch { return null; }
}

// Extracts the first JSON object from CLI output that may have leading log/spinner lines.
static JsonNode? ParseFirstJson(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    var start = text.IndexOf('{');
    var end = text.LastIndexOf('}');
    if (start < 0 || end <= start) return null;
    try { return JsonNode.Parse(text[start..(end + 1)]); }
    catch { return null; }
}

async Task<string?> DiscoverFoundryEndpointAsync()
{
    // New CLI: `foundry server start` boots the daemon; `foundry server status -o json` reports webUrls.
    await RunFoundryAsync("server start", 60);
    var statusJson = await RunFoundryAsync("server status -o json", 30);
    var url = ParseFirstJson(statusJson)?["webUrls"]?.AsArray()?.FirstOrDefault()?.GetValue<string>();
    return url?.TrimEnd('/');
}

async Task EnsureModelLoadedAsync(string modelAlias, ILogger logger)
{
    // New CLI auto-selects the best variant; the `--device`/`--ttl` flags were removed.
    logger.LogInformation("Loading model {Model} on Foundry...", modelAlias);
    var output = await RunFoundryAsync($"model load {modelAlias}", 180);
    if (output != null)
        logger.LogInformation("Model load result: {Output}", output.Trim());
    else
        logger.LogWarning("Model load produced no output for {Model}", modelAlias);
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
app.UseCors();

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
        Model = GetSelectedModel(),
    });
});

// Selectable models for this server. The list is the configured AvailableModels (curated to the
// GPU-compatible catalog for this host); falls back to the single configured Model when empty.
// Each entry carries its capability set (text/code/reasoning/vision/audio/tools) so the SPA can
// render a capability-specific test panel for the selected model.
app.MapGet("/api/models", (IOptions<FoundryLocalOptions> options) =>
{
    var aliases = options.Value.AvailableModels is { Length: > 0 } list
        ? list
        : [options.Value.Model];

    var available = aliases.Select(alias =>
    {
        var caps = ModelCapabilityCatalog.For(alias);
        return new
        {
            id = alias,
            capabilities = caps.Names(),
            text = caps.Text,
            code = caps.Code,
            reasoning = caps.Reasoning,
            vision = caps.Vision,
            audio = caps.Audio,
            tools = caps.Tools,
        };
    }).ToArray();

    return Results.Ok(new
    {
        current = GetSelectedModel(),
        available,
    });
});

// Change the active model. Only aliases present in AvailableModels are accepted (when that list is
// configured); this just flips the default the proxy sends — Foundry loads the model lazily on the
// next chat request. No code change is needed to grow the selectable set, only AvailableModels.
app.MapPost("/api/models/select", async (HttpContext context, IOptions<FoundryLocalOptions> options, CancellationToken cancellationToken) =>
{
    JsonNode? body;
    try
    {
        body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "Request body is not valid JSON." });
    }
    var requested = body?["model"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
    if (string.IsNullOrWhiteSpace(requested))
        return Results.BadRequest(new { error = "Request body must include a non-empty \"model\"." });

    var available = options.Value.AvailableModels ?? [];
    if (available.Length > 0
        && !available.Contains(requested, StringComparer.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            error = $"Model '{requested}' is not in AvailableModels.",
            available,
        });
    }

    lock (_selectedModelLock) _selectedModel = requested;
    return Results.Ok(new { current = requested });
});

// OpenAI-compatible speech-to-text. Foundry Local exposes transcription only via the CLI
// (`foundry transcribe`), not over the daemon's HTTP API, so this endpoint bridges multipart audio
// uploads to the CLI and returns the OpenAI `{ "text": ... }` shape. Used by the SPA's audio panel.
app.MapPost("/v1/audio/transcriptions", async (HttpContext context, IOptions<FoundryLocalOptions> options, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' field." });

    var form = await context.Request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Missing audio 'file'." });

    var model = form["model"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(model)) model = GetSelectedModel();

    // Optional language hint (e.g. "en"). Validated to a short alpha code to keep it shell-safe.
    var language = form["language"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(language) && !Regex.IsMatch(language, "^[a-zA-Z]{2,8}$"))
        language = null;

    if (options.Value.UseStubResponses)
        return Results.Json(new { text = $"[stub transcript for {file.FileName}]", model, language });

    // Persist the upload so the CLI can read it, preserving the extension Whisper expects.
    var ext = Path.GetExtension(file.FileName);
    if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
    var tempPath = Path.Combine(Path.GetTempPath(), $"foundry-stt-{Guid.NewGuid():N}{ext}");
    try
    {
        await using (var fs = File.Create(tempPath))
            await file.CopyToAsync(fs, cancellationToken);

        var langArg = string.IsNullOrWhiteSpace(language) ? "" : $" -l {language}";
        var output = await RunFoundryAsync($"transcribe -m {model} -f \"{tempPath}\"{langArg} -o json", 240);
        var text = ParseFirstJson(output)?["text"]?.GetValue<string>();
        if (text is null)
        {
            logger.LogWarning("Transcription produced no text for {Model}. Raw: {Raw}", model, output?.Trim());
            return Results.Problem(title: "Transcription failed", detail: "Foundry returned no transcript.", statusCode: 502);
        }

        return Results.Json(new { text = text.Trim(), model });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Transcription error for {Model}", model);
        return Results.Problem(title: "Transcription error", detail: ex.Message, statusCode: 500);
    }
    finally
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
    }
});

// Cache of alias → (resolvedId, maxTotalTokens) — cleared when /v1/models is refreshed.
// maxTotalTokens = maxInputTokens + maxOutputTokens as reported by Foundry.
// Foundry enforces this as the hard limit for the max_tokens parameter.
var aliasCache = new ConcurrentDictionary<string, (string ResolvedId, int MaxTokens)>(StringComparer.OrdinalIgnoreCase);

// Single-model VRAM discipline. The cross-platform daemon (CLI 0.10+) does NOT auto-load on request
// (it rejects unloaded ids with a 400), and `foundry model load <alias>` auto-picks a non-GPU variant
// for some models — so the proxy loads the exact resolved GPU id itself. It also UNLOADS the previously
// loaded model first: keeping two large models resident on a 16 GB GPU OOM-crashes the daemon, so we
// hold exactly one model at a time. State is process-static (see ModelLoadState) so the discipline
// spans every host in the process — critical for the sequential integration matrix.
async Task EnsureExclusiveLoadAsync(string resolvedId, ILogger logger, bool daemonRestarted = false)
{
    await ModelLoadState.Lock.WaitAsync();
    try
    {
        // After a daemon restart nothing is resident; forget the stale bookkeeping.
        if (daemonRestarted) ModelLoadState.CurrentLoadedId = null;

        if (string.Equals(ModelLoadState.CurrentLoadedId, resolvedId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrEmpty(ModelLoadState.CurrentLoadedId))
        {
            logger.LogInformation("Unloading {Prev} to free VRAM before loading {Next}", ModelLoadState.CurrentLoadedId, resolvedId);
            await RunFoundryAsync($"model unload {ModelLoadState.CurrentLoadedId}", 60);
        }

        await EnsureModelLoadedAsync(resolvedId, logger);
        ModelLoadState.CurrentLoadedId = resolvedId;
    }
    finally
    {
        ModelLoadState.Lock.Release();
    }
}

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

// Authoritative friendly-alias → resolved-id resolution via `foundry model info <alias> -o json`,
// cached per alias. Needed for families whose resolved id is NOT an alias prefix — e.g.
// "deepseek-r1-7b" → "deepseek-r1-distill-qwen-7b-cuda-gpu", "mistral-7b-v0.2" →
// "mistralai-Mistral-7B-Instruct-v0-2-cuda-gpu", "whisper-tiny" → "openai-whisper-tiny-cuda-gpu".
// We return the variant's displayName, which matches the id form the daemon's /v1/models reports.
var _modelInfoCache = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

async Task<string?> ResolveViaModelInfoAsync(string alias)
{
    if (_modelInfoCache.TryGetValue(alias, out var cachedId))
        return cachedId;

    var json = await RunFoundryAsync($"model info {alias} -o json", 30);
    var model = ParseFirstJson(json)?["model"];
    // Prefer displayName (no ":N" suffix → matches /v1/models ids); fall back to id.
    var resolved = model?["displayName"]?.GetValue<string>() ?? model?["id"]?.GetValue<string>();
    _modelInfoCache[alias] = resolved;
    return resolved;
}

async Task<(string ResolvedId, int MaxTokens)> ResolveModelAsync(string requestedModel, string endpoint, IHttpClientFactory factory)
{
    if (aliasCache.TryGetValue(requestedModel, out var cached))
        return cached;

    var foundryModels = await GetFoundryModelsAsync(endpoint, factory);

    int CapFor(string id) => foundryModels
        .FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.MaxTotalTokens ?? 0;

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
    if (match != null)
    {
        var entry = (match.Id, match.MaxTotalTokens);
        aliasCache[requestedModel] = entry;
        return entry;
    }

    // Heuristic found nothing — consult Foundry's own resolver (handles deepseek/mistral/whisper).
    var mappedId = await ResolveViaModelInfoAsync(requestedModel);
    if (!string.IsNullOrWhiteSpace(mappedId))
    {
        var entry = (mappedId!, CapFor(mappedId!));
        aliasCache[requestedModel] = entry;
        return entry;
    }

    var result = (requestedModel, 0); // fall back; 0 means no capping
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
    JsonNode? requestPayload;
    try
    {
        requestPayload = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "Request body is not valid JSON." });
    }

    // OpenAI requires "messages" to be a non-empty array. Reject other shapes / empty / missing
    // cleanly with a 400 — otherwise an empty array is forwarded to the daemon (confusing 404
    // "model not found") or answered by the stub, and a non-array throws (AsArray) → 500.
    if (requestPayload?["messages"] is not JsonArray messagesArray || messagesArray.Count == 0)
        return Results.BadRequest(new { error = "'messages' must be a non-empty array." });

    // Each element must be an object with a non-empty string "role". Anything else (a bare number,
    // a numeric role) would throw deeper in ApplyContextBounds (m["role"] / GetValue<string>) → 500;
    // reject it up front with a spec-aligned 400.
    foreach (var msg in messagesArray)
    {
        if (msg is not JsonObject msgObj
            || !(msgObj["role"] is JsonValue roleVal && roleVal.TryGetValue<string>(out var roleStr) && !string.IsNullOrWhiteSpace(roleStr)))
        {
            return Results.BadRequest(new { error = "Each message must be an object with a non-empty string 'role'." });
        }
    }

    var foundryOptions = options.Value;
    var ollama = ollamaOptions.Value;

    // Default to the runtime-selected model when the client doesn't pin one (or pins a non-string),
    // so POST /api/models/select governs model-less requests and a bad "model" never throws → 500.
    if (requestPayload is JsonObject reqObj)
    {
        var pinned = reqObj["model"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
        if (string.IsNullOrWhiteSpace(pinned))
            reqObj["model"] = GetSelectedModel();
    }

    // Bound the request so a single call can't blow up the in-process Foundry CUDA KV-cache arena:
    // cap max_tokens to MaxResponseTokens and trim an ever-growing conversation to MaxPromptTokens.
    // Applied to every request (stub + real) and surfaced via diagnostic headers.
    if (requestPayload is JsonObject boundsObj)
    {
        var bounds = OpenAiChatHelpers.ApplyContextBounds(
            boundsObj, foundryOptions.MaxPromptTokens, foundryOptions.MaxResponseTokens, modelTotalCap: 0);
        context.Response.Headers["X-Context-Dropped-Messages"] = bounds.DroppedMessages.ToString();
        context.Response.Headers["X-Context-Max-Tokens"] = bounds.MaxTokens.ToString();
        context.Response.Headers["X-Context-Input-Tokens"] = bounds.InputTokens.ToString();
    }

    if (foundryOptions.UseStubResponses)
    {
        var prompt = OpenAiChatHelpers.ExtractLatestUserPrompt(requestPayload);
        var model = requestPayload?["model"]?.GetValue<string>() ?? GetSelectedModel();
        var isStreamingStub = requestPayload?["stream"]?.GetValue<bool>() == true;

        if (isStreamingStub)
        {
            var stubChunk = OpenAiChatHelpers.CreateStubStreamingResponse(model, prompt);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync($"data: {stubChunk}\n\n", cancellationToken);
            await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
            return Results.Empty;
        }

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

                // The daemon needs the exact resolved id loaded before it will serve it.
                if (!string.IsNullOrWhiteSpace(resolvedModel))
                    await EnsureExclusiveLoadAsync(resolvedModel, logger);
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
                const int maxAttempts = 4;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        // The cross-platform daemon can drop out (crash + auto-restart) while cycling
                        // large models. Back off, restart/re-discover it, and re-load the resolved GPU
                        // variant before retrying so a transient outage doesn't surface as a failure.
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellationToken);
                        aliasCache.Clear();
                        cachedModelsExpiry = DateTime.MinValue;

                        var rediscovered = await GetFoundryEndpointAsync(foundryOptions.Endpoint, forceRediscovery: true);
                        if (requestPayload?["model"] is JsonNode retryModelNode)
                        {
                            var retryRequested = originalModelAlias ?? retryModelNode.GetValue<string>();
                            var (retryResolved, _) = await ResolveModelAsync(retryRequested, rediscovered, httpClientFactory);
                            requestPayload["model"] = retryResolved;
                            await EnsureExclusiveLoadAsync(retryResolved, logger, daemonRestarted: true);
                            payloadJson = requestPayload?.ToJsonString() ?? "{}";
                        }
                        logger.LogWarning("Retry {Attempt}/{Max} after daemon issue", attempt + 1, maxAttempts);
                    }

                    var endpoint = await GetFoundryEndpointAsync(foundryOptions.Endpoint);

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
                        
                        // Non-success is often a transient "model not loaded" during a daemon
                        // restart — retry (with reload) until the last attempt, then fall back.
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.LogWarning("Foundry returned {StatusCode} (attempt {Attempt}/{Max})", response.StatusCode, attempt + 1, maxAttempts);
                            if (attempt < maxAttempts - 1) continue;
                            foundryFailed = true;
                            break; // exhausted retries — try Ollama / surface error
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
                    catch (HttpRequestException) when (attempt < maxAttempts - 1)
                    {
                        logger.LogWarning("Foundry unreachable at {Endpoint}, will re-discover and retry...", endpoint);
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
