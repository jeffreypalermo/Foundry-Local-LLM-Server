using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Playwright-based integration test that starts the Aspire AppHost,
/// navigates to the frontend, clicks "Send Prompt", and asserts
/// a valid assistant response appears.
/// </summary>
[Collection("ServerTests")]
public class PlaywrightIntegrationTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _output;

    public PlaywrightIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AppHost_SendPrompt_ReturnsAssistantResponse()
    {
        // Ensure Foundry Local is running and has at least one GPU model
        var foundryUrl = await FoundryServiceHelper.GetServiceUrlAsync();
        Assert.True(foundryUrl != null && await FoundryServiceHelper.IsRunningAsync(),
            "Foundry Local is not running. Start it with 'foundry service start'.");

        // Use phi-4-mini as it's relatively small but capable
        _output.WriteLine("Ensuring GPU model ready: phi-4-mini");
        var modelReady = await FoundryServiceHelper.EnsureGpuModelReadyAsync("phi-4-mini", _output);
        Assert.True(modelReady,
            "Could not load GPU variant of phi-4-mini.");

        // Verify wwwroot exists (frontend must be pre-built)
        var serverProjectDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FoundryLocalLlmServer.Server"));
        var wwwrootIndex = Path.Combine(serverProjectDir, "wwwroot", "index.html");
        Assert.True(File.Exists(wwwrootIndex),
            $"Frontend not built — run 'npm run build' in frontend/ and copy dist/ to wwwroot/. Expected: {wwwrootIndex}");

        // Ensure Playwright browsers are installed
        _output.WriteLine("Ensuring Playwright browsers are installed...");
        Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"]);

        // Start the proxy server with phi-4-mini as the configured model
        var serverExePath = GetServerExePath();
        var serverProcess = StartServer(serverExePath, foundryUrl!, "phi-4-mini");

        try
        {
            await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));
            _output.WriteLine("Server is ready.");

            // Run Playwright test
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
            });

            var page = await browser.NewPageAsync();

            // Navigate to the server (static files served from wwwroot)
            _output.WriteLine("Navigating to http://localhost:5537/");
            await page.GotoAsync("http://localhost:5537/");

            // Wait for the page to load and show the model name
            await page.WaitForSelectorAsync("text=Model:", new PageWaitForSelectorOptions
            {
                Timeout = 10000,
            });
            _output.WriteLine("Page loaded successfully.");

            // Verify the page has the default prompt in the textarea
            var textarea = page.Locator("textarea");
            var promptText = await textarea.InputValueAsync();
            _output.WriteLine($"Default prompt: {promptText}");
            Assert.False(string.IsNullOrWhiteSpace(promptText), "Textarea should have a default prompt");

            // Click the "Send Prompt" button
            var sendButton = page.Locator("button[type='submit']");
            var buttonText = await sendButton.TextContentAsync();
            _output.WriteLine($"Button text: {buttonText}");
            Assert.Contains("Send Prompt", buttonText ?? "");

            await sendButton.ClickAsync();
            _output.WriteLine("Clicked 'Send Prompt' button.");

            // Wait for the button to show "Running..." (busy state)
            await page.WaitForSelectorAsync("button:has-text('Running...')", new PageWaitForSelectorOptions
            {
                Timeout = 5000,
            });
            _output.WriteLine("Button shows 'Running...' — request in progress.");

            // Wait for the assistant response to appear (up to 120 seconds for model inference)
            var assistantMessage = page.Locator("article.message.assistant p");
            await assistantMessage.First.WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = 120000,
                State = WaitForSelectorState.Visible,
            });

            var responseText = await assistantMessage.First.TextContentAsync();
            _output.WriteLine($"Assistant response: {responseText?[..Math.Min(200, responseText?.Length ?? 0)]}...");

            // Assert response is meaningful
            Assert.False(string.IsNullOrWhiteSpace(responseText),
                "Assistant response should not be empty");
            Assert.True(responseText!.Length > 10,
                $"Assistant response is too short ({responseText.Length} chars): {responseText}");

            // Verify no error message is shown
            var errorElement = page.Locator("p.error");
            var errorCount = await errorElement.CountAsync();
            if (errorCount > 0)
            {
                var errorText = await errorElement.First.TextContentAsync();
                Assert.Fail($"Error displayed on page: {errorText}");
            }

            // Verify the button returned to "Send Prompt" state
            await page.WaitForSelectorAsync("button:has-text('Send Prompt')", new PageWaitForSelectorOptions
            {
                Timeout = 5000,
            });

            _output.WriteLine("✅ Playwright test passed — Send Prompt works end-to-end.");
        }
        finally
        {
            try { serverProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
            serverProcess.Dispose();
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

    private static Process StartServer(string exePath, string foundryEndpoint, string model = "phi-4")
    {
        // Set WorkingDirectory to the server project directory so ASP.NET Core
        // finds the wwwroot folder for static file serving.
        var serverProjectDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "FoundryLocalLlmServer.Server"));

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = serverProjectDir,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5537";
        psi.Environment["FoundryLocal__Endpoint"] = foundryEndpoint;
        psi.Environment["FoundryLocal__Model"] = model;

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

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}
