using System.Text.RegularExpressions;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Discovers the running Foundry Local service URL by invoking the CLI.
/// Provides model download, load, and availability helpers for integration tests.
/// Foundry Local uses a dynamic port (e.g. 53969), not a fixed 5273, so we
/// ask the service directly rather than hardcoding a port.
/// </summary>
internal static class FoundryServiceHelper
{
    /// <summary>Base URL of the integration-test proxy server (not Foundry Local itself).</summary>
    public const string ServerBaseUrl = "http://localhost:5537";

    private static string? _cachedUrl;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the base URL of the running Foundry Local service (e.g. "http://127.0.0.1:53969"),
    /// or null if the service cannot be started or its URL cannot be parsed.
    /// </summary>
    public static async Task<string?> GetServiceUrlAsync()
    {
        if (_cachedUrl != null)
            return _cachedUrl;

        await _lock.WaitAsync();
        try
        {
            if (_cachedUrl != null)
                return _cachedUrl;

            _cachedUrl = await DiscoverUrlAsync();
            return _cachedUrl;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns true when the Foundry Local service is reachable.</summary>
    public static async Task<bool> IsRunningAsync()
    {
        var url = await GetServiceUrlAsync();
        if (url == null) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{url}/v1/models");
            if (!response.IsSuccessStatusCode)
            {
                // Cached URL may be stale — rediscover
                _cachedUrl = null;
                url = await GetServiceUrlAsync();
                if (url == null) return false;
                response = await http.GetAsync($"{url}/v1/models");
            }
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Connection may have been refused if service restarted — try rediscovery
            _cachedUrl = null;
            try
            {
                url = await GetServiceUrlAsync();
                if (url == null) return false;
                using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await http2.GetAsync($"{url}/v1/models");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Returns true when the given model alias has a GPU variant loaded in the Foundry service.
    /// Prefers GPU variants over NPU. If only NPU is loaded, returns false (caller should
    /// download/load the GPU variant).
    /// </summary>
    public static async Task<bool> IsGpuModelAvailableAsync(string modelAlias)
    {
        var url = await GetServiceUrlAsync();
        if (url == null) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{url}/v1/models");
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("data", out var data)) return false;

            foreach (var modelElement in data.EnumerateArray())
            {
                if (!modelElement.TryGetProperty("id", out var idProp)) continue;
                var modelId = idProp.GetString() ?? string.Empty;
                if (!ModelIdMatchesAlias(modelId, modelAlias)) continue;

                // Check if this is a GPU variant (not NPU)
                if (IsGpuVariant(modelId))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when any variant (GPU or NPU) of the model alias is loaded.
    /// </summary>
    public static async Task<bool> IsModelAvailableAsync(string modelAlias)
    {
        var url = await GetServiceUrlAsync();
        if (url == null) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{url}/v1/models");
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("data", out var data)) return false;

            foreach (var modelElement in data.EnumerateArray())
            {
                if (!modelElement.TryGetProperty("id", out var idProp)) continue;
                var modelId = idProp.GetString() ?? string.Empty;
                if (ModelIdMatchesAlias(modelId, modelAlias))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the total context window (maxInputTokens + maxOutputTokens) for the
    /// best loaded model that matches <paramref name="modelAlias"/>. Prefers GPU variants.
    /// Returns 0 if unknown.
    /// </summary>
    public static async Task<int> GetModelContextWindowAsync(string modelAlias)
    {
        var url = await GetServiceUrlAsync();
        if (url == null) return 0;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"{url}/v1/models");
            if (!response.IsSuccessStatusCode) return 0;

            var body = await response.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("data", out var data)) return 0;

            int bestWindow = 0;
            foreach (var modelElement in data.EnumerateArray())
            {
                if (!modelElement.TryGetProperty("id", out var idProp)) continue;
                var modelId = idProp.GetString() ?? string.Empty;
                if (!ModelIdMatchesAlias(modelId, modelAlias)) continue;

                var maxIn = modelElement.TryGetProperty("maxInputTokens", out var inp) ? inp.GetInt32() : 0;
                var maxOut = modelElement.TryGetProperty("maxOutputTokens", out var out_) ? out_.GetInt32() : 0;
                var window = maxIn + maxOut;
                if (window > bestWindow)
                    bestWindow = window;
            }

            return bestWindow;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Downloads the GPU variant of a model if not already downloaded.
    /// Returns true on success, false on failure.
    /// </summary>
    public static async Task<bool> DownloadGpuModelAsync(string modelAlias, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(30);

        try
        {
            await EnsureSdkInitializedAsync();
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            var model = await catalog.GetModelAsync(modelAlias);
            if (model == null) return false;

            using var cts = new CancellationTokenSource(timeout.Value);
            await model.DownloadAsync(ct: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the GPU variant of a model into Foundry Local.
    /// Returns true on success, false on failure.
    /// </summary>
    public static async Task<bool> LoadGpuModelAsync(string modelAlias, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(5);

        try
        {
            await EnsureSdkInitializedAsync();
            var catalog = await FoundryLocalManager.Instance.GetCatalogAsync();
            var model = await catalog.GetModelAsync(modelAlias);
            if (model == null) return false;

            using var cts = new CancellationTokenSource(timeout.Value);
            await model.LoadAsync(ct: cts.Token);

            // Verify the GPU model appears in /v1/models
            for (int i = 0; i < 10; i++)
            {
                if (await IsGpuModelAvailableAsync(modelAlias))
                    return true;
                await Task.Delay(2000);
            }

            return await IsModelAvailableAsync(modelAlias);
        }
        catch
        {
            return false;
        }
    }



    /// <summary>
    /// Ensures a model's GPU variant is downloaded and loaded in Foundry Local.
    /// Downloads if needed, then loads. Returns true if the model is ready for use.
    /// </summary>
    public static async Task<bool> EnsureGpuModelReadyAsync(string modelAlias, ITestOutputHelper? output = null)
    {
        // Check if GPU variant is already loaded
        if (await IsGpuModelAvailableAsync(modelAlias))
        {
            output?.WriteLine($"GPU variant of '{modelAlias}' is already loaded.");
            return true;
        }

        // Download the GPU variant
        output?.WriteLine($"Downloading GPU variant of '{modelAlias}'...");
        var downloaded = await DownloadGpuModelAsync(modelAlias);
        if (!downloaded)
        {
            output?.WriteLine($"Failed to download GPU variant of '{modelAlias}'.");
            return false;
        }
        output?.WriteLine($"Download complete for '{modelAlias}'.");

        // Load the GPU variant
        output?.WriteLine($"Loading GPU variant of '{modelAlias}'...");
        var loaded = await LoadGpuModelAsync(modelAlias);
        if (!loaded)
        {
            output?.WriteLine($"Failed to load GPU variant of '{modelAlias}'.");
            return false;
        }
        output?.WriteLine($"GPU variant of '{modelAlias}' loaded successfully.");

        return true;
    }

    /// <summary>
    /// Device/backend token words that appear immediately after the alias in Foundry model IDs.
    /// For example "phi-4-cuda-gpu:4" — "cuda" terminates the alias "phi-4".
    /// Words like "mini" or "reasoning" are intentionally absent: they extend the alias.
    /// </summary>
    private static readonly string[] BackendTokens =
        ["cuda", "openvino", "generic", "trtrtx", "npu", "gpu", "cpu", "instruct"];

    /// <summary>
    /// Returns true if the model ID represents a GPU variant (cuda, generic-gpu, trtrtx-gpu).
    /// NPU and CPU variants return false.
    /// </summary>
    internal static bool IsGpuVariant(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        // GPU variants contain "cuda-gpu", "generic-gpu", "trtrtx-gpu", or just "-gpu:"
        if (lower.Contains("cuda") || lower.Contains("trtrtx"))
            return true;
        if (lower.Contains("-gpu") && !lower.Contains("npu"))
            return true;
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="modelId"/> corresponds to the given <paramref name="alias"/>.
    /// Examples:
    ///   ModelIdMatchesAlias("Phi-4-cuda-gpu:4",               "phi-4")       → true
    ///   ModelIdMatchesAlias("phi-4-mini-instruct-openvino-npu:1", "phi-4")   → false  (mini ≠ backend)
    ///   ModelIdMatchesAlias("phi-4-mini-instruct-openvino-npu:1", "phi-4-mini") → true
    /// </summary>
    internal static bool ModelIdMatchesAlias(string modelId, string alias)
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

    private static bool ModelIdMatchesAliasCore(string modelId, string alias)
    {
        if (string.Equals(modelId, alias, StringComparison.OrdinalIgnoreCase)) return true;
        if (modelId.StartsWith(alias + ":", StringComparison.OrdinalIgnoreCase)) return true;
        if (!modelId.StartsWith(alias + "-", StringComparison.OrdinalIgnoreCase)) return false;

        var afterAlias = modelId[(alias.Length + 1)..].ToLowerInvariant();
        return BackendTokens.Any(t =>
            afterAlias == t
            || afterAlias.StartsWith(t + "-")
            || afterAlias.StartsWith(t + ":"));
    }

    /// <summary>
    /// Polls the proxy server at <see cref="ServerBaseUrl"/> until it responds or the timeout elapses.
    /// Returns true when the server is ready, false on timeout.
    /// </summary>
    public static async Task<bool> WaitForServerReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await http.GetAsync($"{ServerBaseUrl}/api/foundry");
                if ((int)resp.StatusCode < 500)
                    return true;
            }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

   private static async Task EnsureSdkInitializedAsync()
   {
       if (FoundryLocalManager.IsInitialized) return;

       var modelCacheDir = Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
           ".foundry", "cache", "models");

       await FoundryLocalManager.CreateAsync(
           new Configuration
           {
               AppName = "foundry",
               ModelCacheDir = modelCacheDir,
               Web = new Configuration.WebService { Urls = "http://127.0.0.1:0" },
           },
           NullLogger.Instance);
   }

   private static async Task<string?> DiscoverUrlAsync()
   {
       try
       {
           await EnsureSdkInitializedAsync();

           if (FoundryLocalManager.Instance.Urls is not { Length: > 0 })
               await FoundryLocalManager.Instance.StartWebServiceAsync();

           return FoundryLocalManager.Instance.Urls?.FirstOrDefault();
       }
       catch
       {
           return null;
       }
   }
}
