using System.Diagnostics;
using System.Text.RegularExpressions;
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
            var psi = new ProcessStartInfo("foundry")
            {
                Arguments = $"model download {modelAlias}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start foundry download process.");

            using var cts = new CancellationTokenSource(timeout.Value);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
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
            var psi = new ProcessStartInfo("foundry")
            {
                Arguments = $"model load {modelAlias}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start foundry load process.");

            using var cts = new CancellationTokenSource(timeout.Value);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            // Trust the CLI exit code. The /v1/models verification can't be used here: families whose
            // resolved id isn't an alias prefix (deepseek-r1-* → DeepSeek-R1-Distill-Qwen-*, mistral-7b-v0.2
            // → mistralai-Mistral-7B-*, whisper-* → openai-whisper-*) never satisfy the prefix matcher
            // even when correctly loaded. `foundry model load` exiting 0 means the model is loaded.
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
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

   private static async Task<string?> DiscoverUrlAsync()
   {
       // Cross-platform CLI (v1.x / 0.10+): `foundry server start` boots the daemon and
       // `foundry server status -o json` reports its webUrls.
       await RunFoundryAsync("server start", 60);
       var statusJson = await RunFoundryAsync("server status -o json", 30);
       if (statusJson == null) return null;

       try
       {
           var start = statusJson.IndexOf('{');
           var end = statusJson.LastIndexOf('}');
           if (start < 0 || end <= start) return null;
           var node = System.Text.Json.Nodes.JsonNode.Parse(statusJson[start..(end + 1)]);
           var url = node?["webUrls"]?.AsArray()?.FirstOrDefault()?.GetValue<string>();
           return url?.TrimEnd('/');
       }
       catch { return null; }
   }

   private static async Task<string?> RunFoundryAsync(string arguments, int timeoutSeconds)
   {
       try
       {
           using var process = Process.Start(new ProcessStartInfo("foundry")
           {
               Arguments = arguments,
               UseShellExecute = false,
               RedirectStandardOutput = true,
               RedirectStandardError = true,
           });
           if (process == null) return null;
           var stdoutTask = process.StandardOutput.ReadToEndAsync();
           var stderrTask = process.StandardError.ReadToEndAsync();
           using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
           await process.WaitForExitAsync(cts.Token);
           return (await stdoutTask) + (await stderrTask);
       }
       catch { return null; }
   }
}
