# Skill: Playwright Live E2E Test with Process-Started Server

## When to Use
When you need to validate the full frontend → backend → LLM inference path using a real browser, a real server binary, and a real model — as if launched from Visual Studio.

## Pattern Overview

The test:
1. Skips if Foundry Local is not running (graceful degradation)
2. Ensures the GPU model is loaded (downloads/loads if needed)
3. Checks wwwroot/index.html exists (frontend must be pre-built via `npm run build`)
4. Installs Playwright browsers headlessly
5. Starts the server binary via `Process.Start` with environment vars pointing at Foundry
6. Polls `/api/foundry` health endpoint until ready (max 30s)
7. Navigates Chromium to the server root, clicks "Send Prompt"
8. Waits up to 120s for real LLM response in `article.message.assistant p`
9. Asserts response is non-empty and > 10 chars; asserts no error element shown

## Key Implementation Details

### Starting the server in-process
```csharp
var psi = new ProcessStartInfo(serverExePath)
{
    UseShellExecute = false,
    WorkingDirectory = serverProjectDir, // CRITICAL: ASP.NET Core must find wwwroot here
};
psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5537";
psi.Environment["FoundryLocal__Endpoint"] = foundryUrl;  // dynamic port from FoundryServiceHelper
psi.Environment["FoundryLocal__Model"] = "phi-4-mini";
var serverProcess = Process.Start(psi);
```

### Waiting for server ready
```csharp
// Poll /api/foundry — returns 200 once the server has resolved the model alias
await WaitForServerReadyAsync("http://localhost:5537/api/foundry", TimeSpan.FromSeconds(30));
```

### Playwright assertions for real LLM
```csharp
// Wait for busy state first (confirms click was processed)
await page.WaitForSelectorAsync("button:has-text('Running...')", new() { Timeout = 5000 });

// Wait for response — real LLM may take up to 120s
var assistantMessage = page.Locator("article.message.assistant p");
await assistantMessage.First.WaitForAsync(new() { Timeout = 120000, State = WaitForSelectorState.Visible });

// Structural assertion only — never assert on specific text from a real LLM
Assert.True(responseText!.Length > 10, $"Response too short: {responseText}");
```

### Cleanup
```csharp
finally
{
    try { serverProcess.Kill(entireProcessTree: true); } catch { }
    serverProcess.Dispose();
}
```

## Pre-conditions Checklist
- [ ] `dotnet build ./FoundryLocalLlmServer.sln` — builds server binary to `bin/Debug/net10.0/`
- [ ] `npm run build` in `frontend/` — copies dist to `wwwroot/` via Vite config
- [ ] `foundry service status` — confirms service is running (port is dynamic)
- [ ] Model loaded: check `GET /v1/models` for `phi-4-mini*cuda-gpu*` entry

## Run Command
```
dotnet test ./FoundryLocalLlmServer.IntegrationTests/FoundryLocalLlmServer.IntegrationTests.csproj \
  --filter "FullyQualifiedName~PlaywrightIntegrationTests" \
  -v normal --no-build
```

> **Note:** Use `--no-build` after a solution-level build. Running `dotnet test` against the `.sln` triggers a full NuGet restore that can stall in the output stream.

## Observed Performance (2026-05-12)
- Server ready: ~57 seconds (Playwright browser install + ASP.NET startup)
- LLM inference (phi-4-mini-instruct-cuda-gpu): ~1.8 seconds
- Total test duration: 63.2 seconds
