# Apoc — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript)
- **Aspire:** `FoundryLocalLlmServer.AppHost` — run with `dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj`
- **Aspire config:** `aspire.config.json`
- **CI:** `dotnet test ./FoundryLocalLlmServer.sln` — integration tests use `UseStubResponses=true` (no GPU needed)
- **User:** Jeffrey Palermo

## Learnings

### 2026-05-11 — opencode integration (phi-4 via Foundry Local proxy)

- **opencode version:** 1.14.31 (already installed via Chocolatey)
- **Config location:** `C:\Users\JeffreyPalermo\.config\opencode\opencode.json` (this is the active config opencode loads)
- **Provider format:** opencode uses `@ai-sdk/openai-compatible` npm package for any OpenAI-compatible endpoint. Config key is `"provider"`, NOT `"providers"`. Each provider entry requires `npm`, `name`, `options.baseURL`, and `models`.
- **`/v1/models` endpoint required:** opencode's `@ai-sdk/openai-compatible` SDK queries `GET /v1/models` to discover available model IDs. The server did not have this endpoint — added it to `Program.cs` returning `phi-4` in OpenAI list format.
- **Config snippet that works:**
  ```json
  "foundry-local": {
    "npm": "@ai-sdk/openai-compatible",
    "name": "Foundry Local (phi-4)",
    "options": { "baseURL": "http://localhost:5537/v1" },
    "models": { "phi-4": { "name": "Phi-4 (local)", "tool_call": false, "temperature": true, "attachment": false, "reasoning": false } }
  }
  ```
- **CLI invocation:** `opencode run --model foundry-local/phi-4 "prompt"` — model flag format is `<providerID>/<modelID>`
- **Verified:** `service=llm providerID=foundry-local modelID=phi-4` appeared in opencode logs confirming the local server was used. No API key required.
- **No CORS issues:** Server is not called from browser by opencode; opencode CLI calls it directly, so no CORS headers needed.
- **Foundry Local GPU service:** Must be running on port 5273 for real inference. Without it, proxy returns 503. `UseStubResponses=true` env var bypasses this for testing.

### 2026-05-11 — Live testing session (Foundry Local offline)

- **Foundry Local status:** NOT running. Port 5273 actively refused connections.
- **Bug found:** `Program.cs` `/v1/chat/completions` proxy had no error handling around `proxyClient.SendAsync`. When Foundry Local is down, `HttpRequestException` bubbled up and the ASP.NET `UseExceptionHandler()` middleware returned a generic 500 ProblemDetails. Frontend showed "Completion failed (500)" — not user-friendly.
- **Fix applied (commit 396b3ee):**
  1. `Program.cs`: wrapped `proxyClient.SendAsync` in try/catch for `HttpRequestException`, now returns `Results.Problem(statusCode: 503)` with a clear message indicating Foundry Local is unreachable.
  2. `frontend/src/App.tsx`: on non-OK fetch responses, now attempts to parse ProblemDetails JSON and surface the `detail` or `title` field as the UI error message.
- **Verified behavior:** UI shows "Could not reach Foundry Local at http://127.0.0.1:5273. Ensure the service is running." — actionable for developers.
- **Build quirk:** `dotnet build` with `Select-String` pipe hangs. Use `-v minimal` without pipes; the build output is parseable inline. Also the `AssemblyInfoInputs.cache` file sometimes gets locked — delete it to unblock a rebuild.
- **All tests pass:** unit tests, frontend lint, frontend production build all green.
