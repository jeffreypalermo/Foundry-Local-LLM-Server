using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace FoundryLocalLlmServer.IntegrationTests;

/// <summary>
/// Regression test for the GPU VRAM leak Jeffrey reported: sending the same long prompt many times
/// through the web UI drove the in-process Foundry runtime to load ever-larger (non-reclaimable)
/// CUDA KV-cache arenas until the 8 GB RTX 4060 ran out of memory and inference crawled — even
/// though the model itself needs &lt; 3 GB.
///
/// <para><b>Confirmed root cause (Apoc, 2026-06-16):</b> the embedded ONNX GenAI runtime sizes its
/// CUDA arena to each request's <i>prompt length</i> and never releases it for the life of the
/// process (<c>IModel.UnloadAsync</c>/<c>StopWebServiceAsync</c> do NOT reclaim it). The web UI
/// resends the whole, ever-growing conversation each turn with no <c>max_tokens</c>, so the arena
/// climbs to OOM. The fix bounds every request server-side (cap <c>max_tokens</c> + trim the oldest
/// turns to <c>FoundryLocal:MaxPromptTokens</c>), keeping the footprint flat and the loaded-model
/// count at exactly one.</para>
///
/// <para>This test drives the real SPA with Playwright (the exact repro flow), measures GPU memory
/// via <c>nvidia-smi</c> after each of 10 iterations, and asserts the peak stays within a sane
/// single-model bound and that the runtime never reports more than one loaded model instance. It
/// FAILS on the buggy server (VRAM climbs past the ceiling) and PASSES on the fixed server. Runs
/// unconditionally and FAILS (never skips) if anything in the flow cannot complete.</para>
///
/// <para><b>Protected selectors</b> (must not change): <c>button[type='submit']</c>,
/// <c>article.message.assistant p</c>, <c>p.error</c>, <c>p.config-line &gt; strong</c>.</para>
/// </summary>
[Collection("ServerTests")]
public class RepeatedPromptVramTests
{
    private const int Iterations = 10;

    // The card is 8188 MiB total; a single qwen2.5-1.5b CUDA load is ~2.2–2.5 GB. A leak-free run
    // plateaus around ~3.3 GB (model + one bounded prompt's KV). The ceiling sits in the task's
    // "~4.5–5 GB" window, comfortably below the card total — the buggy server blows past it (~7.9 GB).
    private const int VramCeilingMiB = 5000;

    // The exact user-reported repro prompt (note the embedded quotes around the letter r).
    private const string ReproPrompt =
        "write a 8000 line poem about cats. Make every 3rd couplet also rhyme. " +
        "Do not use the letter \"r\" anywhere";

    private readonly ITestOutputHelper _output;
    private readonly ServerFixture _server;

    public RepeatedPromptVramTests(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "GPU-Required")]
    public async Task RepeatedPrompt_DoesNotLeakVram_OrStackModelCopies()
    {
        var modelAlias = SupportedModelData.DefaultModel;
        await _server.EnsureModelAsync(modelAlias, _output);

        var wwwrootIndex = Path.Combine(ServerFixture.GetServerProjectDir(), "wwwroot", "index.html");
        Assert.True(File.Exists(wwwrootIndex), $"Frontend not served — expected built SPA at {wwwrootIndex}.");

        // Baseline: GPU memory once the single model is loaded and ready, before any UI iteration.
        // The suite shares one server, so this captures whatever footprint already exists; the leak
        // is demonstrated by GROWTH across the repeated prompts, not an absolute starting value.
        var baselineVram = ReadGpuMemoryUsedMiB();
        _output.WriteLine($"Baseline (single-model) VRAM: {baselineVram} MiB");
        Assert.True(baselineVram < VramCeilingMiB,
            $"Baseline VRAM {baselineVram} MiB already exceeds the ceiling {VramCeilingMiB} MiB.");

        var installExit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        Assert.True(installExit == 0, $"Playwright Chromium install failed with exit code {installExit}.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync($"{_server.BaseUrl}/");

        var configStrong = page.Locator("p.config-line > strong").First;
        await configStrong.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000, State = WaitForSelectorState.Visible });
        Assert.Contains("qwen", await configStrong.TextContentAsync() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var textarea = page.Locator("textarea");
        var sendButton = page.Locator("button[type='submit']");

        var peakVram = baselineVram;

        for (var i = 1; i <= Iterations; i++)
        {
            // Type the exact repro prompt and submit via the protected button — the real UI flow.
            await textarea.FillAsync(ReproPrompt);
            await sendButton.ClickAsync();

            // Wait for the assistant response for THIS iteration (i bubbles) or a surfaced error.
            var sw = Stopwatch.StartNew();
            await WaitForAssistantOrErrorAsync(page, expectedAssistantCount: i, timeoutMs: 240_000);
            sw.Stop();

            // The protected error paragraph must stay empty — no error rendered.
            var errorCount = await page.Locator("p.error").CountAsync();
            if (errorCount > 0)
            {
                var errorText = await page.Locator("p.error").First.TextContentAsync();
                Assert.Fail($"Iteration {i}: error displayed on page: {errorText}");
            }

            // Wait for the button to return to idle so the next submit is accepted.
            await page.WaitForSelectorAsync("button:has-text('Send Prompt')",
                new PageWaitForSelectorOptions { Timeout = 20_000 });

            var vram = ReadGpuMemoryUsedMiB();
            var loaded = await GetLoadedModelCountAsync();
            if (vram > peakVram) peakVram = vram;

            _output.WriteLine($"Iter {i,2}: {sw.Elapsed.TotalSeconds,6:N1}s loadedModels={loaded} VRAM={vram} MiB");

            // Invariant on every iteration: never more than one resident model instance.
            Assert.True(loaded <= 1,
                $"Iteration {i}: {loaded} loaded model instances — duplicates are resident (expected ≤ 1).");
        }

        var growth = peakVram - baselineVram;
        _output.WriteLine($"Baseline {baselineVram} MiB; peak after {Iterations} iterations: {peakVram} MiB " +
                          $"(growth {growth} MiB, ceiling {VramCeilingMiB} MiB, card total 8188 MiB).");

        // The peak must stay within a sane single-model bound and safely below the card total —
        // this is what fails on the leaky server, where VRAM climbs to ~7.9 GB.
        Assert.True(peakVram <= VramCeilingMiB,
            $"VRAM leak: peak {peakVram} MiB exceeded the ceiling {VramCeilingMiB} MiB after {Iterations} repeated prompts.");
        Assert.True(peakVram < 8188 - 1000,
            $"VRAM peak {peakVram} MiB is dangerously close to the 8188 MiB card total.");

        // Growth across the run must be bounded: the leak adds ~4.5 GB of orphaned KV arenas, while
        // the fixed server plateaus after the first bounded prompt (≈ a few hundred MiB of growth).
        Assert.True(growth < 2500,
            $"VRAM grew {growth} MiB across {Iterations} repeated prompts — the per-request footprint is not being bounded.");
    }

    /// <summary>
    /// Waits until at least <paramref name="expectedAssistantCount"/> COMPLETED assistant bubbles
    /// have rendered (this iteration's reply arrived) or an error paragraph appears. The transient
    /// "Generating response…" bubble (<c>article.message.assistant.pending</c>) is excluded.
    /// </summary>
    private static async Task WaitForAssistantOrErrorAsync(IPage page, int expectedAssistantCount, int timeoutMs)
    {
        var completed = page.Locator("article.message.assistant:not(.pending) p");
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await page.Locator("p.error").CountAsync() > 0)
                return;

            var assistantCount = await completed.CountAsync();
            if (assistantCount >= expectedAssistantCount)
            {
                var text = await completed.Nth(expectedAssistantCount - 1).TextContentAsync();
                if (!string.IsNullOrWhiteSpace(text))
                    return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Timed out waiting for assistant response #{expectedAssistantCount} (or an error) to render.");
    }

    /// <summary>Number of model instances the in-process runtime reports as loaded (GET /api/models).</summary>
    private async Task<int> GetLoadedModelCountAsync()
    {
        var payload = await _server.Client.GetFromJsonAsync<JsonObject>("/api/models");
        var data = payload?["data"] as JsonArray;
        if (data is null) return 0;
        return data.Count(item => item?["loaded"]?.GetValue<bool>() == true);
    }

    /// <summary>Reads total GPU memory used (MiB) from nvidia-smi. Fails the test if unavailable.</summary>
    private static int ReadGpuMemoryUsedMiB()
    {
        var psi = new ProcessStartInfo("nvidia-smi",
            "--query-gpu=memory.used --format=csv,noheader,nounits")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start nvidia-smi — is the NVIDIA driver installed?");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);

        // First GPU's reading (one RTX 4060 on this box).
        var firstLine = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(firstLine),
            "nvidia-smi returned no memory reading — cannot verify GPU VRAM.");

        return int.Parse(firstLine!);
    }
}
