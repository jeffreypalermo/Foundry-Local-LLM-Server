using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Discovers the running Foundry Local service URL by invoking the CLI.
/// Foundry Local uses a dynamic port (e.g. 53969), not a fixed 5273, so we
/// ask the service directly rather than hardcoding a port.
/// </summary>
internal static class FoundryServiceHelper
{
    private static readonly Regex ServiceUrlPattern =
        new(@"running on (http://[^\s/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when the given model alias appears in the Foundry service model list,
    /// using exact alias matching (not a substring check) to avoid false positives
    /// where "phi-4" would match "phi-4-mini-instruct-openvino-npu:1".
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
    /// first loaded model that matches <paramref name="modelAlias"/>, or 0 if unknown.
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

            foreach (var modelElement in data.EnumerateArray())
            {
                if (!modelElement.TryGetProperty("id", out var idProp)) continue;
                var modelId = idProp.GetString() ?? string.Empty;
                if (!ModelIdMatchesAlias(modelId, modelAlias)) continue;

                var maxIn  = modelElement.TryGetProperty("maxInputTokens",  out var inp) ? inp.GetInt32() : 0;
                var maxOut = modelElement.TryGetProperty("maxOutputTokens", out var out_) ? out_.GetInt32() : 0;
                return maxIn + maxOut;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }



    /// <summary>
    /// Device/backend token words that appear immediately after the alias in Foundry model IDs.
    /// For example "phi-4-cuda-gpu:4" — "cuda" terminates the alias "phi-4".
    /// Words like "mini" or "reasoning" are intentionally absent: they extend the alias.
    /// </summary>
    private static readonly string[] BackendTokens =
        ["cuda", "openvino", "generic", "trtrtx", "npu", "gpu", "cpu", "instruct"];

    /// <summary>
    /// Returns true if <paramref name="modelId"/> corresponds to the given <paramref name="alias"/>.
    /// Examples:
    ///   ModelIdMatchesAlias("Phi-4-cuda-gpu:4",               "phi-4")       → true
    ///   ModelIdMatchesAlias("phi-4-mini-instruct-openvino-npu:1", "phi-4")   → false  (mini ≠ backend)
    ///   ModelIdMatchesAlias("phi-4-mini-instruct-openvino-npu:1", "phi-4-mini") → true
    /// </summary>
    private static bool ModelIdMatchesAlias(string modelId, string alias)
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
        // "foundry service start" outputs either:
        //   "Service is Started on http://127.0.0.1:XXXXX/, PID NNNNN"
        //   "Service is already running on http://127.0.0.1:XXXXX/."
        // We parse the URL from either form.
        try
        {
            var psi = new ProcessStartInfo("foundry")
            {
                Arguments = "service start",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start foundry process.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cts.Token);

            var combined = (await stdoutTask) + (await stderrTask);
            var match = ServiceUrlPattern.Match(combined);
            if (!match.Success)
                return null;

            // Strip trailing slash so callers can append paths directly
            return match.Groups[1].Value.TrimEnd('/');
        }
        catch
        {
            return null;
        }
    }
}
