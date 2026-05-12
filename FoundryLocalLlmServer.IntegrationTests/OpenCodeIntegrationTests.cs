using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Live integration test: starts the server binary, runs the opencode CLI against it,
/// and verifies a coherent response from phi-4.
/// Requires Foundry Local GPU service running on port 5273.
/// Skips automatically when that service is unavailable.
/// </summary>
public class OpenCodeIntegrationTests
{
    // Matches ANSI color/cursor escape sequences produced by opencode's terminal output
    private static readonly Regex AnsiEscapePattern =
        new(@"\x1b\[[0-9;]*[mGKHFJA-Za-z]", RegexOptions.Compiled);

    // CLI-style error prefix: "Error: ..." or "error: ..."
    private static readonly Regex ErrorPrefixPattern =
        new(@"^error:", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Stack-trace-style exception lines: "SomeException: message"
    private static readonly Regex ExceptionColonPattern =
        new(@"\w+Exception\s*:", RegexOptions.Multiline);

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task OpenCodeCli_RunsAgainstServer_ReturnsValidResponse()
    {
        // Skip when Foundry Local GPU service is not available (CI without GPU)
        var foundryAvailable = await IsFoundryLocalRunningAsync();
        Skip.If(!foundryAvailable,
            "Foundry Local not running on port 5273 — skipping live integration test");

        var serverExePath = GetServerExePath();
        Process? serverProcess = null;

        try
        {
            serverProcess = StartServer(serverExePath);
            await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));

            var (exitCode, stdout, stderr) = await RunOpenCodeAsync(TimeSpan.FromSeconds(60));

            var cleanStdout = StripAnsiCodes(stdout);
            var cleanStderr = StripAnsiCodes(stderr);

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
        // Test runs from: .../FoundryLocalLlmServer.IntegrationTests/bin/Debug/net10.0/
        // Repo root is four directories up.
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

    private static Process StartServer(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5537";

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
                // Any non-5xx status means the server accepted the connection
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
            Arguments = "run --model foundry-local/phi-4 \"What is 2+2? Answer in one sentence.\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start opencode process.");

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

    private static async Task<bool> IsFoundryLocalRunningAsync()
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await httpClient.GetAsync("http://127.0.0.1:5273/v1/models");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
