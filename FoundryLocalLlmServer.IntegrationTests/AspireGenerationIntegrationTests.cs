using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Integration tests that validate each Foundry Local model's ability to generate
/// code via opencode through the proxy server process.
/// Requires Foundry Local GPU service to be running (any port — discovered via CLI).
/// Automatically downloads and loads GPU model variants as needed.
/// </summary>
[Collection("ServerTests")]
public class AspireGenerationIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public AspireGenerationIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly Regex AnsiEscapePattern =
        new(@"\x1b\[[0-9;]*[mGKHFJA-Za-z]", RegexOptions.Compiled);

    public static IEnumerable<object[]> Rtx5090CompatibleModels =>
    [
        ["phi-4-mini",             "2.39 GB"],
    ];

    /// <summary>
    /// Verifies that opencode can use the proxy server (started as a separate process)
    /// to send a code-generation prompt to a Foundry Local model and receive a meaningful
    /// response containing C# code. This tests the full stack: Foundry → proxy exe → opencode.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rtx5090CompatibleModels))]
    [Trait("Category", "Integration")]
    public async Task OpenCode_GeneratesCodeResponse(string modelAlias, string gpuFileSize)
    {
        _ = gpuFileSize; // used in test name display only

        var foundryUrl = await FoundryServiceHelper.GetServiceUrlAsync();
        Assert.True(foundryUrl != null && await FoundryServiceHelper.IsRunningAsync(),
            "Foundry Local is not running. Start it with 'foundry service start'.");

        _output.WriteLine($"Ensuring GPU model ready: {modelAlias}");
        var modelReady = await FoundryServiceHelper.EnsureGpuModelReadyAsync(modelAlias, _output);
        Assert.True(modelReady,
            $"Could not download/load GPU variant of '{modelAlias}'.");

        var serverExePath = GetServerExePath();
        Process? serverProcess = null;

        try
        {
            serverProcess = StartServer(serverExePath, foundryUrl!);
            await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));

            var prompt = "Write a C# class called Calculator with a method Add that takes two integers and returns their sum. Output only the code.";

            var psi = new ProcessStartInfo("opencode")
            {
                Arguments = $"run --model foundry-local/{modelAlias} \"{prompt}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };

            _output.WriteLine($"Running opencode with model: {modelAlias}");
            var (openCodeExit, openCodeStdout, openCodeStderr) =
                await RunProcessAsync(psi, TimeSpan.FromSeconds(300));

            var cleanStdout = StripAnsiCodes(openCodeStdout);
            var cleanStderr = StripAnsiCodes(openCodeStderr);

            _output.WriteLine($"opencode exit code: {openCodeExit}");
            _output.WriteLine($"opencode stdout ({cleanStdout.Length} chars): {cleanStdout}");

            Assert.True(
                openCodeExit == 0,
                $"opencode exited with code {openCodeExit}.\nstdout:\n{cleanStdout}\nstderr:\n{cleanStderr}");

            Assert.True(
                cleanStdout.Length > 10,
                $"Model returned an empty or near-empty response.\nstdout:\n{cleanStdout}\nstderr:\n{cleanStderr}");

            // The response should contain C# code elements related to the prompt
            var containsCodeContent =
                cleanStdout.Contains("class", StringComparison.OrdinalIgnoreCase) ||
                cleanStdout.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ||
                cleanStdout.Contains("Add", StringComparison.OrdinalIgnoreCase) ||
                cleanStdout.Contains("int", StringComparison.OrdinalIgnoreCase) ||
                cleanStdout.Contains("return", StringComparison.OrdinalIgnoreCase);

            Assert.True(
                containsCodeContent,
                $"Model response did not contain expected C# code elements (class, Calculator, Add, int, return).\n" +
                $"stdout:\n{cleanStdout}");

            _output.WriteLine($"✅ Model '{modelAlias}' generated code response successfully via proxy exe.");
        }
        finally
        {
            if (serverProcess != null)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
                serverProcess.Dispose();
            }
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

        if (psi.RedirectStandardInput)
            process.StandardInput.Close();

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

    private static string StripAnsiCodes(string input) =>
        AnsiEscapePattern.Replace(input, string.Empty);
}
