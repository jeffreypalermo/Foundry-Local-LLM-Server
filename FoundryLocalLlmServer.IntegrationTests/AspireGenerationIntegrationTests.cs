using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Integration tests that validate each Foundry Local model's ability to generate
/// a working .NET 10 Aspire application via opencode.
/// Requires Foundry Local GPU service to be running (any port — discovered via CLI).
/// Automatically downloads and loads GPU model variants as needed.
/// </summary>
public class AspireGenerationIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AspireGenerationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly Regex AnsiEscapePattern =
        new(@"\x1b\[[0-9;]*[mGKHFJA-Za-z]", RegexOptions.Compiled);

    private static readonly string[] ModelNotAvailablePhrases =
    [
        "model not found",
        "model is not available",
        "failed to load model",
        "cannot find model",
    ];

    public static IEnumerable<object[]> Rtx5090CompatibleModels =>
    [
        ["phi-4",                  "8.37 GB"],
        ["phi-4-mini",             "3.60 GB"],
        ["phi-4-mini-reasoning",   "3.15 GB"],
        ["phi-3.5-mini",           "2.13 GB"],
        ["phi-3-mini-128k",        "2.13 GB"],
        ["phi-3-mini-4k",          "2.13 GB"],
        ["deepseek-r1-14b",        "9.83 GB"],
        ["deepseek-r1-7b",         "5.28 GB"],
        ["deepseek-r1-1.5b",       "1.43 GB"],
        ["mistral-7b-v0.2",        "3.98 GB"],
        ["qwen2.5-14b",            "8.79 GB"],
        ["qwen2.5-coder-14b",      "8.79 GB"],
        ["qwen2.5-coder-7b",       "4.73 GB"],
        ["qwen2.5-coder-1.5b",     "1.25 GB"],
        ["qwen2.5-coder-0.5b",     "0.52 GB"],
        ["qwen2.5-7b",             "4.73 GB"],
        ["qwen2.5-1.5b",           "1.25 GB"],
        ["qwen2.5-0.5b",           "0.52 GB"],
        ["qwen3-14b",              "9.08 GB"],
        ["qwen3-8b",               "5.54 GB"],
        ["qwen3-4b",               "2.63 GB"],
        ["qwen3-1.7b",             "1.29 GB"],
        ["qwen3-0.6b",             "0.48 GB"],
        ["gpt-oss-20b",            "9.65 GB"],
    ];

    [SkippableTheory]
    [MemberData(nameof(Rtx5090CompatibleModels))]
    [Trait("Category", "Integration")]
    public async Task OpenCode_GeneratesAspireApp_BuildsAndRuns(string modelAlias, string gpuFileSize)
    {
        _ = gpuFileSize; // used in test name display only

        var foundryUrl = await FoundryServiceHelper.GetServiceUrlAsync();
        Skip.If(foundryUrl == null || !await FoundryServiceHelper.IsRunningAsync(),
            "Foundry Local not running — skipping live integration test");

        // Ensure the GPU variant is downloaded and loaded
        _output.WriteLine($"Ensuring GPU model ready: {modelAlias}");
        var modelReady = await FoundryServiceHelper.EnsureGpuModelReadyAsync(modelAlias, _output);
        Skip.If(!modelReady,
            $"Could not download/load GPU variant of '{modelAlias}' — model may not be available in catalog");

        // Verify the context window is large enough for opencode
        const int MinContextForOpenCodeTools = 4096;
        var contextWindow = await FoundryServiceHelper.GetModelContextWindowAsync(modelAlias);
        _output.WriteLine($"Model '{modelAlias}' context window: {contextWindow} tokens");
        Skip.If(contextWindow > 0 && contextWindow < MinContextForOpenCodeTools,
            $"Model '{modelAlias}' context window ({contextWindow} tokens) is too small for opencode tool-calling " +
            $"(minimum {MinContextForOpenCodeTools} tokens required).");

        var testRunId = Guid.NewGuid().ToString("N")[..8];
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-gen-{modelAlias}-{testRunId}");
        Directory.CreateDirectory(tempDir);

        var serverExePath = GetServerExePath();
        Process? serverProcess = null;

        try
        {
            serverProcess = StartServer(serverExePath, foundryUrl!);
            await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));

            var prompt = "Create a minimal .NET 10 Aspire web application. " +
                         "Run this command to scaffold it: dotnet new aspire-starter --output . --force. " +
                         "Then run dotnet build to verify it compiles. Report the exit code of the build.";

            var psi = new ProcessStartInfo("opencode")
            {
                Arguments = $"run --model foundry-local/{modelAlias} \"{prompt}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = tempDir,
            };

            _output.WriteLine($"Running opencode with model: {modelAlias}");
            var (openCodeExit, openCodeStdout, openCodeStderr) =
                await RunProcessAsync(psi, TimeSpan.FromSeconds(300));

            var cleanStdout = StripAnsiCodes(openCodeStdout);
            var cleanStderr = StripAnsiCodes(openCodeStderr);
            var combinedOutput = $"{cleanStdout}\n{cleanStderr}";

            _output.WriteLine($"opencode exit code: {openCodeExit}");
            _output.WriteLine($"opencode stdout length: {cleanStdout.Length}");

            // Skip if model wasn't available at runtime
            if (ContainsModelNotAvailablePhrase(combinedOutput))
                Skip.If(true,
                    $"Model '{modelAlias}' not available at runtime — not downloaded yet");

            Assert.True(
                openCodeExit == 0,
                $"opencode exited with code {openCodeExit}.\nstdout:\n{cleanStdout}\nstderr:\n{cleanStderr}");

            // Build assertion
            var createdFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
            _output.WriteLine($"Files created: {createdFiles.Length}");
            Assert.True(
                createdFiles.Length > 0,
                $"opencode ran but created no files in '{tempDir}'. " +
                $"The model likely responded with text instead of using bash/file tools to scaffold the project.\n" +
                $"stdout:\n{cleanStdout}\nstderr:\n{cleanStderr}");

            var buildPsi = new ProcessStartInfo("dotnet")
            {
                Arguments = "build",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = tempDir,
            };

            var (buildExit, buildStdout, buildStderr) =
                await RunProcessAsync(buildPsi, TimeSpan.FromSeconds(120));

            _output.WriteLine($"dotnet build exit code: {buildExit}");
            Assert.True(
                buildExit == 0,
                $"dotnet build failed (exit {buildExit}).\nstdout:\n{buildStdout}\nstderr:\n{buildStderr}");

            // Run assertion — find AppHost project
            var appHostCsproj = Directory
                .GetFiles(tempDir, "*.csproj", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    Path.GetFileName(f).Contains("AppHost", StringComparison.OrdinalIgnoreCase));

            Assert.True(
                appHostCsproj != null,
                $"Could not find an AppHost .csproj in temp directory '{tempDir}'.");

            var runOutput = await RunAspireAppAndCaptureOutputAsync(appHostCsproj!, TimeSpan.FromSeconds(30));

            var startupPhrases = new[]
            {
                "now listening on",
                "application started",
                "aspire dashboard",
                "running on",
            };

            var appStarted = startupPhrases.Any(phrase =>
                runOutput.Contains(phrase, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                appStarted,
                $"Aspire app did not emit a startup message within 30s.\nOutput:\n{runOutput}");

            _output.WriteLine($"✅ Model '{modelAlias}' successfully generated and ran an Aspire app.");
        }
        finally
        {
            if (serverProcess != null)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
                serverProcess.Dispose();
            }

            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string GetServerExePath()
    {
        var repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        var exePath = Path.Combine(
            repoRoot,
            "FoundryLocalLlmServer.Server",
            "bin", "Debug", "net10.0",
            "FoundryLocalLlmServer.Server.exe");

        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                $"Server binary not found at: {exePath}. Run 'dotnet build' on the Server project first.",
                exePath);

        return exePath;
    }

    private static Process StartServer(string exePath, string foundryEndpoint)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5537";
        psi.Environment["FoundryLocal__Endpoint"] = foundryEndpoint;

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start server process.");
    }

    private static async Task WaitForServerReadyAsync(string healthUrl, TimeSpan timeout)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(healthUrl);
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch { /* server not ready yet */ }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Server at {healthUrl} did not become ready within {timeout.TotalSeconds}s.");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        ProcessStartInfo psi, TimeSpan timeout)
    {
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"Process '{psi.FileName}' did not complete within {timeout.TotalSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Starts the Aspire AppHost and collects output until a startup phrase appears or timeout.
    /// </summary>
    private static async Task<string> RunAspireAppAndCaptureOutputAsync(
        string appHostCsproj, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            Arguments = $"run --project \"{appHostCsproj}\" --no-build",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(appHostCsproj)!,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Aspire AppHost process.");

        var outputBuilder = new System.Text.StringBuilder();
        var startupPhrases = new[] { "now listening on", "application started", "aspire dashboard", "running on" };

        var tcs = new TaskCompletionSource<bool>();

        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            outputBuilder.AppendLine(e.Data);
            if (startupPhrases.Any(p => e.Data.Contains(p, StringComparison.OrdinalIgnoreCase)))
                tcs.TrySetResult(true);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetResult(false));

        await tcs.Task;

        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        process.Dispose();

        return outputBuilder.ToString();
    }

    private static string StripAnsiCodes(string input) =>
        AnsiEscapePattern.Replace(input, string.Empty);

    private static bool ContainsModelNotAvailablePhrase(string output) =>
        ModelNotAvailablePhrases.Any(phrase =>
            output.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
