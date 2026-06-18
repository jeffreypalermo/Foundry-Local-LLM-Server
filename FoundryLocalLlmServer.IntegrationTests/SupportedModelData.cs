using Microsoft.Extensions.Configuration;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Single source of truth for the parameterized per-model integration tests.
///
/// <para>The <b>model set</b> is read from the server's committed configuration
/// (<c>FoundryLocal:AvailableModels</c> in <c>appsettings.json</c>) so the test harness
/// auto-adapts whenever a model is added to / removed from that array — no test edits needed.</para>
///
/// <para>The <b>tool-calling capability flag</b> per model is the empirically-verified hardware
/// truth from Apoc's full RTX 4060 catalog sweep (2026-06-16):
/// (<c>.squad/decisions/inbox/apoc-full-catalog-complete.md</c>). Only qwen2.5 Instruct models
/// and smollm3-3b emit proper OpenAI <c>tool_calls</c> payloads. Other models (qwen3/phi/coder)
/// return prose or XML-wrapped calls which don't parse as tool_calls.</para>
/// </summary>
internal static class SupportedModelData
{
    /// <summary>
    /// Model aliases empirically verified to return a proper OpenAI <c>tool_calls</c> object
    /// (populated array, not empty) through the Foundry Local endpoint. qwen3/phi/coder models
    /// return prose or XML-wrapped calls instead of proper tool_calls.
    /// Source: Apoc, apoc-full-catalog-complete.md (2026-06-16).
    /// </summary>
    private static readonly HashSet<string> ToolCallingCapableAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "qwen2.5-0.5b",
            "qwen2.5-1.5b",
            "smollm3-3b"
        };

    /// <summary>
    /// Hard-coded fallback used only if the server's appsettings.json cannot be located/parsed
    /// from the test project. Mirrors the committed set so coverage never silently collapses.
    /// </summary>
    private static readonly string[] FallbackAvailableModels = [
        "qwen2.5-0.5b"
    ];

    private static IConfigurationRoot? _serverConfig;
    private static readonly object _configLock = new();

    /// <summary>Returns true when the alias is verified to support OpenAI tool calling.</summary>
    public static bool SupportsToolCalls(string modelAlias) =>
        ToolCallingCapableAliases.Contains(modelAlias);

    /// <summary>
    /// The configured selectable model set (<c>FoundryLocal:AvailableModels</c>), read from the
    /// server's committed appsettings.json. Falls back to the known-good set if unreadable.
    /// </summary>
    public static string[] AvailableModels
    {
        get
        {
            var fromConfig = ServerConfiguration()
                ?.GetSection("FoundryLocal:AvailableModels").Get<string[]>();

            return fromConfig is { Length: > 0 } ? fromConfig : FallbackAvailableModels;
        }
    }

    /// <summary>The configured startup default active model (<c>FoundryLocal:Model</c>).</summary>
    public static string DefaultModel =>
        ServerConfiguration()?["FoundryLocal:Model"] ?? AvailableModels[0];

    /// <summary>
    /// xUnit <see cref="MemberDataAttribute"/> source. Yields one row per configured model:
    /// <c>(string modelAlias, bool supportsToolCalls)</c>. Adding/removing a model in
    /// <c>FoundryLocal:AvailableModels</c> automatically expands/contracts every parameterized test.
    /// </summary>
    public static IEnumerable<object[]> SupportedModels()
    {
        foreach (var alias in AvailableModels)
            yield return [alias, SupportsToolCalls(alias)];
    }

    /// <summary>
    /// Builds (once) an <see cref="IConfigurationRoot"/> from the server project's appsettings.json,
    /// located by walking up from the test output directory to the solution root.
    /// </summary>
    private static IConfigurationRoot? ServerConfiguration()
    {
        if (_serverConfig != null)
            return _serverConfig;

        lock (_configLock)
        {
            if (_serverConfig != null)
                return _serverConfig;

            var appSettingsPath = FindServerAppSettings();
            if (appSettingsPath == null)
                return null;

            _serverConfig = new ConfigurationBuilder()
                .AddJsonFile(appSettingsPath, optional: false)
                .Build();

            return _serverConfig;
        }
    }

    /// <summary>
    /// Walks up from the test binary directory until it finds the solution root (containing
    /// <c>FoundryLocalLlmServer.sln</c>), then returns the server project's appsettings.json path.
    /// Returns null if not found (callers fall back to the static model set).
    /// </summary>
    private static string? FindServerAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FoundryLocalLlmServer.sln")))
            {
                var candidate = Path.Combine(
                    dir.FullName, "FoundryLocalLlmServer.Server", "appsettings.json");
                return File.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
