using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Live integration test: starts the server binary, runs the opencode CLI against it,
/// and verifies a coherent response from phi-4-mini.
/// Requires Foundry Local GPU service to be running (any port — discovered via CLI).
/// Automatically downloads and loads the GPU model variant if needed.
/// </summary>
[Collection("ServerTests")]
public class OpenCodeIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public OpenCodeIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Matches ANSI color/cursor escape sequences produced by opencode's terminal output
    private static readonly Regex AnsiEscapePattern =
        new(@"\x1b\[[0-9;]*[mGKHFJA-Za-z]", RegexOptions.Compiled);

    // CLI-style error prefix: "Error: ..." or "error: ..."
    private static readonly Regex ErrorPrefixPattern =
        new(@"^error:", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Stack-trace-style exception lines: "SomeException: message"
    private static readonly Regex ExceptionColonPattern =
        new(@"\w+Exception\s*:", RegexOptions.Multiline);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenCodeCli_RunsAgainstServer_ReturnsValidResponse()
    {
        // Foundry Local GPU service must be available
        var foundryUrl = await FoundryServiceHelper.GetServiceUrlAsync();
        Assert.NotNull(foundryUrl);
        Assert.True(await FoundryServiceHelper.IsRunningAsync(),
            "Foundry Local is not running. Start it with 'foundry service start'.");
        Assert.True(await IsCommandAvailableAsync("opencode"),
            "opencode CLI is not installed or not available on PATH.");

        // Ensure the GPU variant of phi-4-mini is downloaded and loaded
        // (phi-4-mini has 131K context, sufficient for opencode's tool-calling system prompt;
        //  phi-4's 16K context is too small and causes timeouts)
        _output.WriteLine("Ensuring GPU model ready: phi-4-mini");
        var modelReady = await FoundryServiceHelper.EnsureGpuModelReadyAsync("phi-4-mini", _output);
        Assert.True(modelReady,
            "Could not download/load GPU variant of 'phi-4-mini'.");

        var serverExePath = GetServerExePath();
        Process? serverProcess = null;

        try
        {
            serverProcess = StartServer(serverExePath, foundryUrl!);
            await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));
            _output.WriteLine("Server is ready on port 5537.");

            var (exitCode, stdout, stderr) = await RunOpenCodeAsync(TimeSpan.FromSeconds(300));

            var cleanStdout = StripAnsiCodes(stdout);
            var cleanStderr = StripAnsiCodes(stderr);

            _output.WriteLine($"opencode exit code: {exitCode}");
            _output.WriteLine($"stdout length: {cleanStdout.Length}");

            // Primary signal: exit code
            Assert.Equal(0, exitCode);

            // Response must have actual content
            Assert.True(
                cleanStdout.Trim().Length > 0,
                $"opencode stdout was empty or whitespace. stderr:\n{cleanStderr}");

            // No CLI-style "Error: ..." lines in stdout
            Assert.False(
                ErrorPrefixPattern.IsMatch(cleanStdout),
                $"Stdout contains an error-prefix line. Full output:\n{cleanStdout}");

            // No stack-trace-style exception lines in stdout
            Assert.False(
                ExceptionColonPattern.IsMatch(cleanStdout),
                $"Stdout contains an exception-style line. Full output:\n{cleanStdout}");

            // Stderr should not contain error prefixes or stack traces
            var stderrErrorLines = cleanStderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => ErrorPrefixPattern.IsMatch(line) || ExceptionColonPattern.IsMatch(line))
                .ToArray();

            Assert.Empty(stderrErrorLines);

            _output.WriteLine("✅ phi-4 opencode test passed.");
        }
        finally
        {
            if (serverProcess != null)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { /* best effort cleanup */ }
                serverProcess.Dispose();
            }
        }
    }

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
            catch { /* server not ready yet — keep polling */ }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Server at {healthUrl} did not become ready within {timeout.TotalSeconds}s.");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunOpenCodeAsync(
        TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("opencode")
        {
            Arguments = "run --model foundry-local/phi-4-mini \"What is 2+2? Answer in one sentence.\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start opencode process.");

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
                $"opencode process did not complete within {timeout.TotalSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static string StripAnsiCodes(string input) =>
        AnsiEscapePattern.Replace(input, string.Empty);

    private static async Task<bool> IsCommandAvailableAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
