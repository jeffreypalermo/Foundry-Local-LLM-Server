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

### 2026-06-03 — Foundry Local phi-4-mini GPU root-cause (regression since 2026-05-12)

- **Symptom:** `Phi-4-mini-instruct-cuda-gpu:5` (worked 2026-05-12) can no longer be listed,
  downloaded, or run. `foundry model list` -> `Failed to process model #0 on page 1` (x4) ->
  `No models were returned from the Azure Foundry catalog`, then hangs. Same failure poisons
  `download` and `run` (shared `InitModelsAsync` path; `--device` filter is applied too late).
- **Environment is healthy:** foundry **0.8.119** (build 0.8.119.102, the newest installable
  anywhere); RTX 4060, driver 591.55, 8188 MiB VRAM; service running with CUDA + TensorRT-RTX +
  OpenVINO EPs auto-registered. No proxy/DNS/auth issue; there is no `foundry login`.
- **Root cause — Layer 1 (catalog parse crash):** verbose logging (`defaultLogLevel: 0`) pinned
  it to `LocalModelHelper.ToAzureFoundryLocalModel` doing `JsonSerializer.Deserialize` on an
  EMPTY `promptTemplate` of the first catalog entry (`Phi-4-reasoning-generic-cpu:1`) ->
  `"input does not contain any JSON tokens"`. The catalog HTTP fetch SUCCEEDS
  (`Got catalog model list ... Status:Success`); one bad entry aborts the whole list. Confirmed
  by GitHub issues **#752** and **#757** (Microsoft-acknowledged; winget stuck at 0.8.119).
- **Workaround (Layer 1):** seed `%USERPROFILE%\.foundry\cache\models\foundry.modelinfo.json`
  with a known-good 128-model snapshot, restart service. `download`/`load` then resolve models
  locally and bypass the broken remote parse. **Download succeeds, byte-exact** (verified
  model.onnx.data 4,101,851,136 B == expected). Files land in a nested `v5\` subfolder + a
  0-byte `download.tmp`; finalization HANGS on 0.8.119 in non-TTY shells.
- **Manual finalize:** stop service, `Stop-Process -Id <Inference.Service.Agent PID> -Force`
  (name/pipe kills are blocked), flatten `v5\*` up, delete `download.tmp`, restart. Then
  `foundry model load Phi-4-mini-instruct-cuda-gpu-5 --device GPU` (dash folder-id) SUCCEEDS;
  the colon id `:5` reports "not found locally" (registry never written by the hung finalizer).
- **GPU execution PROVEN:** VRAM 5267 -> 7385 MiB, CUDA util 93-95%, ~2 s/response, `/v1/models`
  serves the model.
- **Root cause — Layer 2 (runtime/artifact gap -> garbage output):** completions are degenerate:
  phi-4-mini:5 -> constant token id 0 (`!!!!`); qwen2.5-0.5b:4 (4-bit) -> word-salad.
  Reproduced independently with the latest public `onnxruntime-genai-cuda` **0.14.1** (CPU and
  CUDA): input token IDs are provably correct (`<|user|>`=200021 ... `<|assistant|>`=200019) but
  every output token is id 0. So it is NOT a template/tokenizer fault. phi-4-mini:5's `model.onnx`
  uses an **8-bit `MatMulNBits` (`weight_Q8G32`)** scheme. The artifacts were rebuilt for
  **Foundry Local v1.2.0** (released 2026-05-28: ORT 1.26.0 / GenAI 0.14.0 + "SDK-based model
  versioning so only supported models are shown"). The frozen 0.8.119 runtime is too old.
- **No upgrade path:** winget caps at 0.8.119; GitHub releases v1.0.0/v1.1.0/v1.2.0 have **zero
  assets** (only <=0.8.119 ship `.msix`); MS Store has no package. The real fix is an installable
  v1.2.0+; re-test GPU serving when it ships. Watch #752/#757.
- **Catalog internals (for reference):** POST
  `https://ai.azure.com/api/eastus/ux/v1.0/entities/crossRegion`; model blobs from
  `https://{region}.api.azureml.ms/modelregistry/...`; website CORS mirror
  `https://onnxruntime-foundry-proxy-hpape7gzf2haesef.eastus-01.azurewebsites.net/api/foundryproxy`
  (returned 43 healthy GPU/CUDA entities — catalog DATA is fine; only the CLI parse is brittle).
- **App impact:** none required. Tank's Foundry-Local-only proxy is correct; it will serve real
  completions automatically once the runtime is fixed. Do NOT reintroduce Ollama.

### 2026-06-16 — CUDA execution provider config (Tank)

- **Config key:** Tank added `FoundryLocal:PreferredExecutionProvider` (default `"CUDA"`) to
  FoundryLocalOptions, appsettings.json, and appsettings.Development.json. This surfaces GPU
  provider selection to config, allowing operators to switch CUDA ↔ NvTensorRtRtx without rebuild.
- **EP registration order:** Program.cs EP registration now prefers the configured provider first,
  with other discovered NVIDIA GPU EPs (TensorRT-RTX, etc.) as fallbacks. Inference never silently
  drops to CPU.
- **RTX 4060 targeting:** CUDA is the default, broad-compatible choice for the onboard RTX 4060
  (Ada Lovelace). TensorRT-RTX is available as an alternative without code changes.
- **Upstream fix impact:** When v1.2.0+ becomes installable and GPU models coherently execute,
  this config will direct inference to the optimal provider for the hardware. Stub mode
  (`UseStubResponses=true`) and 503 error handling remain unchanged.
