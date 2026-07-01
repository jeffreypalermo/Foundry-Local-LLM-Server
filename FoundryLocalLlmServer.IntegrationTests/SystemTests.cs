using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Full system test: boots the real Aspire AppHost (proxy server + React/Vite frontend) with
/// Foundry Local stub responses enabled — GitHub Actions runners have neither a GPU nor Foundry
/// Local installed, so this exercises the whole stack (AppHost orchestration, the server's daemon
/// discovery path, and the UI) without requiring either. Drives the started frontend with
/// Playwright: runs the default fast chat demo and clicks "Unload All Models".
/// </summary>
[Collection("SystemTests")]
public class SystemTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private DistributedApplication? _app;

    public SystemTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FoundryLocalLlmServer_AppHost>();

        // No GPU / Foundry Local daemon on CI runners — the server still runs its normal startup
        // path (which checks for and tries to start the Foundry daemon) but answers chat requests
        // with fast, deterministic stub responses instead of real inference.
        appHost.Configuration["FoundryLocal:UseStubResponses"] = "true";

        _app = await appHost.BuildAsync();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "SystemTest")]
    public async Task AppHost_FastDemo_And_UnloadAllModels_WorkEndToEnd()
    {
        var app = _app ?? throw new InvalidOperationException("AppHost did not start.");

        _output.WriteLine("Waiting for the 'server' resource to become healthy...");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("server").WaitAsync(TimeSpan.FromSeconds(60));

        _output.WriteLine("Waiting for the 'webfrontend' resource to become healthy...");
        await app.ResourceNotifications.WaitForResourceHealthyAsync("webfrontend").WaitAsync(TimeSpan.FromSeconds(60));

        var frontendUri = app.GetEndpoint("webfrontend");
        _output.WriteLine($"Frontend endpoint: {frontendUri}");

        // The Vite dev server's port can accept TCP connections slightly before it can actually
        // serve the app (first compile) — poll until it answers, rather than racing Playwright's
        // navigation against that gap.
        await WaitForHttpOkAsync(frontendUri, TimeSpan.FromSeconds(60));

        _output.WriteLine("Ensuring Playwright's Chromium build is installed...");
        var installExitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installExitCode != 0)
            _output.WriteLine($"Playwright install exited with code {installExitCode} (may already be installed).");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(frontendUri.ToString());
        _output.WriteLine("Navigated to the frontend.");

        // Stub mode short-circuits the daemon reachability check, so the banner should read ready
        // almost immediately — this also exercises the daemon-status banner end to end.
        await page.WaitForSelectorAsync("text=running and ready", new PageWaitForSelectorOptions { Timeout = 15000 });
        _output.WriteLine("Daemon status banner reports ready.");

        await page.WaitForSelectorAsync("text=Model:", new PageWaitForSelectorOptions { Timeout = 15000 });

        // Fast demo: the default scenario's prompt is already filled in; just send it.
        var sendButton = page.Locator("button[type='submit']");
        await sendButton.ClickAsync();
        _output.WriteLine("Clicked 'Send Prompt' for the default fast demo.");

        var assistantMessage = page.Locator("article.message.assistant p");
        await assistantMessage.First.WaitForAsync(new LocatorWaitForOptions
        {
            Timeout = 20000,
            State = WaitForSelectorState.Visible,
        });
        var responseText = await assistantMessage.First.TextContentAsync();
        Assert.False(string.IsNullOrWhiteSpace(responseText), "Stub assistant response should not be empty.");
        _output.WriteLine($"Assistant response: {responseText}");

        var errorCount = await page.Locator("p.error").CountAsync();
        Assert.Equal(0, errorCount);

        // Exercise the "Unload All Models" button too.
        var unloadButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unload All Models" });
        await unloadButton.ClickAsync();
        await page.WaitForSelectorAsync("text=No models were loaded.", new PageWaitForSelectorOptions { Timeout = 15000 });
        _output.WriteLine("'Unload All Models' completed.");
    }

    private static async Task WaitForHttpOkAsync(Uri uri, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetAsync(uri);
                if ((int)response.StatusCode < 500) return;
            }
            catch { /* not ready yet */ }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"{uri} did not respond within {timeout.TotalSeconds}s.");
    }
}
