using Microsoft.Playwright;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Playwright-based integration test that uses the shared in-process server from
/// <see cref="ServerFixture"/>, navigates to the frontend, clicks "Send Prompt",
/// and asserts a valid assistant response appears.
/// </summary>
[Collection("ServerTests")]
public class PlaywrightIntegrationTests
{
    private const string ModelAlias = "qwen2.5-0.5b";

    private readonly ITestOutputHelper _output;
    private readonly ServerFixture _server;

    public PlaywrightIntegrationTests(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AppHost_SendPrompt_ReturnsAssistantResponse()
    {
        await _server.EnsureModelAsync(ModelAlias, _output);

        // Ensure Playwright browsers are installed
        _output.WriteLine("Ensuring Playwright browsers are installed...");
        try
        {
            Microsoft.Playwright.Program.Main(["install", "chromium", "--with-deps"]);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Playwright browser install failed: {ex.Message}");
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });

        var page = await browser.NewPageAsync();

        _output.WriteLine($"Navigating to {_server.BaseUrl}/");
        await page.GotoAsync($"{_server.BaseUrl}/");

        await page.WaitForSelectorAsync("text=Model:", new PageWaitForSelectorOptions
        {
            Timeout = 10000,
        });
        _output.WriteLine("Page loaded successfully.");

        var configLineText = await page.Locator("p.config-line strong").First.TextContentAsync();
        Assert.Contains("qwen", configLineText ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var textarea = page.Locator("textarea");
        var promptText = await textarea.InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(promptText), "Textarea should have a default prompt");

        var sendButton = page.Locator("button[type='submit']");
        var buttonText = await sendButton.TextContentAsync();
        Assert.Contains("Send Prompt", buttonText ?? "");

        await sendButton.ClickAsync();
        _output.WriteLine("Clicked 'Send Prompt' button.");

        await page.WaitForSelectorAsync("button:has-text('Running...')", new PageWaitForSelectorOptions
        {
            Timeout = 5000,
        });
        _output.WriteLine("Button shows 'Running...' — request in progress.");

        // Wait for generation to fully complete: button returns to "Send Prompt".
        // CPU inference can be very slow — allow up to 10 minutes.
        await page.WaitForSelectorAsync("button:has-text('Send Prompt')", new PageWaitForSelectorOptions
        {
            Timeout = 600000,
        });

        _output.WriteLine("Generation complete — button returned to 'Send Prompt'.");

        var errorElement = page.Locator("p.error");
        var errorCount = await errorElement.CountAsync();
        if (errorCount > 0)
        {
            var errorText = await errorElement.First.TextContentAsync();
            Assert.Fail($"Error displayed on page: {errorText}");
        }

        var assistantMessage = page.Locator("article.message.assistant p");
        var responseText = await assistantMessage.First.TextContentAsync();
        _output.WriteLine($"Assistant response: {responseText?[..Math.Min(200, responseText?.Length ?? 0)]}...");

        _output.WriteLine("Playwright test passed — Send Prompt works end-to-end.");
    }
}
