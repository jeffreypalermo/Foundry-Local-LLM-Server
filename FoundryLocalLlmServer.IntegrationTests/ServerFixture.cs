using System.Diagnostics;
using Xunit;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Collection-scoped fixture that starts the real <b>in-process Foundry Local server EXE</b> ONCE
/// on the fixed dev URL <see cref="FoundryServiceHelper.ServerBaseUrl"/> (http://localhost:5537),
/// with <c>UseStubResponses=false</c> so Foundry Local actually bootstraps on the GPU.
///
/// <para>Starting the published server EXE is more reliable for the in-process Foundry bootstrap
/// than <c>WebApplicationFactory&lt;Program&gt;</c>, and it serves the built SPA from
/// <c>wwwroot</c> (needed by the Playwright test). The fixture also publishes the frontend's built
/// <c>dist</c> into the server's <c>wwwroot</c> when it is missing.</para>
///
/// <para>Individual tests switch the active model per-test through
/// <see cref="EnsureModelAsync"/> (which calls <c>POST /api/models/select</c>), reusing the two
/// CACHED qwen models — no large re-downloads.</para>
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public string BaseUrl => FoundryServiceHelper.ServerBaseUrl;

    /// <summary>An HttpClient pointed at the running in-process server.</summary>
    public HttpClient Client { get; private set; } = default!;

    private Process? _server;
    private bool _startedByUs;

    public async Task InitializeAsync()
    {
        Client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };

        // Reuse an already-running proxy server (e.g. a dev instance or CI stub server)
        // instead of failing to bind :5537. Skip the frontend build in this case — the
        // externally-started server already has its own wwwroot (or doesn't need one for API tests).
        if (await IsProxyServerRunningAsync())
        {
            _startedByUs = false;
            return;
        }

        EnsureFrontendPublishedToWwwroot();

        var exePath = GetServerExePath();
        var serverProjectDir = GetServerProjectDir();

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = serverProjectDir,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        psi.Environment["FoundryLocal__UseStubResponses"] = "false";

        // Inject Python CUDA DLL paths so the Foundry SDK can find onnxruntime_providers_cuda.dll,
        // cublas64_12.dll, cudnn64_9.dll, etc. (CUDA toolkit bundled with Python onnxruntime 1.26.0).
        var pythonBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "Lib", "site-packages");
        var cudaPaths = new[]
        {
            Path.Combine(pythonBase, "onnxruntime", "capi"),
            Path.Combine(pythonBase, "nvidia", "cuda_runtime", "bin"),
            Path.Combine(pythonBase, "nvidia", "cublas", "bin"),
            Path.Combine(pythonBase, "nvidia", "cudnn", "bin"),
            Path.Combine(pythonBase, "nvidia", "cufft", "bin"),
            Path.Combine(pythonBase, "nvidia", "curand", "bin"),
            Path.Combine(pythonBase, "nvidia", "nvjitlink", "bin"),
            Path.Combine(pythonBase, "nvidia", "cuda_nvrtc", "bin"),
        };
        var existingPath = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? "";
        psi.Environment["PATH"] = string.Join(Path.PathSeparator.ToString(), cudaPaths) + Path.PathSeparator + existingPath;

        _server = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the in-process server EXE.");
        _startedByUs = true;

        // First boot can take several minutes (EP/model download on a cold cache); the qwen models
        // here are pre-cached so it is normally fast, but allow generous headroom.
        var ready = await FoundryServiceHelper.WaitForServerReadyAsync(TimeSpan.FromMinutes(8));
        if (!ready)
            throw new TimeoutException(
                $"In-process server did not become ready on {BaseUrl} within the allotted time.");
    }

    /// <summary>
    /// Ensures the given alias is the active, GPU-loaded model. Throws (test FAILS, never skips)
    /// when the model cannot be made ready.
    /// </summary>
    public async Task EnsureModelAsync(string modelAlias, Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        output?.WriteLine($"Loading model on server: {modelAlias}");

        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { model = modelAlias }),
            System.Text.Encoding.UTF8,
            "application/json");

        // Selecting a model triggers the server to load it in its own foundry engine
        // (not the test process's foundry instance, which is on a different dynamic port).
        var resp = await Client.PostAsync("/api/models/select", content);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Model '{modelAlias}' could not be loaded on the GPU. " +
                $"Server POST /api/models/select returned {(int)resp.StatusCode}: {body}");
        }

        output?.WriteLine($"Model '{modelAlias}' is active on server.");
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();

        // Killing the server process tears down the in-process Foundry runtime, freeing GPU VRAM.
        if (_startedByUs && _server is { HasExited: false })
        {
            try { _server.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { _server.WaitForExit(10_000); } catch { /* best effort */ }
        }

        _server?.Dispose();
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> IsProxyServerRunningAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{FoundryServiceHelper.ServerBaseUrl}/api/foundry");
            return (int)resp.StatusCode < 500;
        }
        catch { return false; }
    }

    internal static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FoundryLocalLlmServer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: four levels up from the test bin (…/bin/Debug/net10.0/ → repo root).
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    internal static string GetServerProjectDir() =>
        Path.Combine(GetRepoRoot(), "FoundryLocalLlmServer.Server");

    internal static string GetServerExePath()
    {
        var binDebug = Path.Combine(GetServerProjectDir(), "bin", "Debug");
        if (Directory.Exists(binDebug))
        {
            // The TFM folder is windows/RID-specific (e.g. net10.0-windows10.0.18362.0[\win-x64]);
            // locate the built server exe wherever it landed. Prefer a RID-specific build.
            var candidates = Directory
                .GetFiles(binDebug, "FoundryLocalLlmServer.Server.exe", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (candidates.Length > 0)
                return candidates[0];
        }

        throw new FileNotFoundException(
            $"Server binary not found under {binDebug}. Build the Server project first " +
            "(dotnet build .\\FoundryLocalLlmServer.sln -c Debug).");
    }

    /// <summary>
    /// Publishes the frontend's built <c>dist</c> into the server's <c>wwwroot</c> so the SPA is
    /// served at the server root (required by the Playwright test). No-op if already present.
    /// </summary>
    private static void EnsureFrontendPublishedToWwwroot()
    {
        var serverProjectDir = GetServerProjectDir();
        var wwwroot = Path.Combine(serverProjectDir, "wwwroot");
        var indexHtml = Path.Combine(wwwroot, "index.html");
        if (File.Exists(indexHtml))
            return;

        var dist = Path.Combine(GetRepoRoot(), "frontend", "dist");
        var distIndex = Path.Combine(dist, "index.html");
        if (!File.Exists(distIndex))
        {
            // Build the frontend if its dist is missing.
            BuildFrontend();
        }

        if (!File.Exists(distIndex))
            throw new FileNotFoundException(
                $"Frontend build output not found at {distIndex}. Run 'npm run build' in frontend/.",
                distIndex);

        CopyDirectory(dist, wwwroot);
    }

    private static void BuildFrontend()
    {
        var frontendDir = Path.Combine(GetRepoRoot(), "frontend");

        RunCommand(frontendDir, "npm.cmd", "ci");
        RunCommand(frontendDir, "npm.cmd", "run build");
    }

    private static void RunCommand(string workingDir, string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName} {arguments}'.");
        process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' failed with exit code {process.ExitCode}.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}
