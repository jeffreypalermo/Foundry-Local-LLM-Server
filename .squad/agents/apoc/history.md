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

### 2026-05-12 — Resumed live testing (validation session)

- **Test environment:** Ran server binary directly with `FoundryLocalLlmServer.Server.exe` (no Aspire orchestrator, faster iteration).
- **Test 1 (Config endpoint):** `GET /api/foundry` returns 200 with correct endpoint and model.
- **Test 2 (Model discovery):** `GET /v1/models` returns 200 with OpenAI-compatible list format, phi-4 model present.
- **Test 3 (Stub response):** `POST /v1/chat/completions` with `UseStubResponses=true` returns 200 with valid OpenAI-compatible response including choices[0].message.content.
- **Test 4 (Error handling):** `POST /v1/chat/completions` with `UseStubResponses=false` and Foundry Local unreachable:
  - Returns **HTTP 503** (correct status for upstream dependency failure)
  - Content-Type: `application/problem+json` (ProblemDetails per RFC 7807)
  - JSON body includes `title: "Foundry Local Unavailable"` and `detail: "Could not reach Foundry Local at http://127.0.0.1:5273. Ensure the service is running."`
  - Detail field includes exception message with exact endpoint and reason (connection refused)
- **Frontend integration:** App.tsx correctly parses ProblemDetails and displays `detail` field to user.
- **Verdict:** ✅ All critical paths working. Error handling is actionable and semantically correct. No defects found.

### 2026-05-12 — Live Playwright E2E test (real LLM, Send Prompt button)

- **Full solution build:** ✅ All 4 projects (`Server`, `AppHost`, `UnitTests`, `IntegrationTests`) succeeded with `dotnet build ./FoundryLocalLlmServer.sln -v minimal`. Note: `--no-build` should be used for `dotnet test` when already built to skip the second restore cycle.
- **Frontend build:** ✅ `npm run build` in `frontend/` produced `dist/` in 119ms (Vite 8.0.7, TypeScript compiled).
- **Foundry Local status:** ✅ Running. `foundry service status` confirmed `🟢 Model management service is running on http://127.0.0.1:53874/openai/status`. Note: port is dynamic (53874 this session, was 5273 in earlier sessions) — `FoundryServiceHelper` correctly discovers it via `foundry service start` regex pattern.
- **GPU model loaded:** ✅ `Phi-4-mini-instruct-cuda-gpu:5` confirmed via `GET /v1/models`. The `ModelIdMatchesAlias("Phi-4-mini-instruct-cuda-gpu:5", "phi-4-mini")` → true because `instruct` is a BackendToken.
- **Playwright test result:** ✅ **PASSED** — `AppHost_SendPrompt_ReturnsAssistantResponse` — 1 test, 0 failed, 0 skipped, duration 63.2s.
  - Server started on port 5537 in ~57 seconds (Playwright browser install + server spinup).
  - Model alias resolved: `phi-4-mini` → `Phi-4-mini-instruct-cuda-gpu:5` (context cap=131072).
  - Chat completion proxied to Foundry Local, 200 response received in 1.78 seconds.
  - Real LLM response verified non-empty by assertion.
- **Run command:** `dotnet test ./FoundryLocalLlmServer.IntegrationTests/FoundryLocalLlmServer.IntegrationTests.csproj --filter "FullyQualifiedName~PlaywrightIntegrationTests" -v normal --no-build`
- **Key infra note:** Running `dotnet test` against the `.sln` with many projects triggers a NuGet restore that can appear hung (shows only timing ticks). Run against the specific project with `--no-build` after a solution-level `dotnet build` for reliable CI iteration.
