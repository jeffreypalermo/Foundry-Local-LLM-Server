# Tank — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, OpenAI-compatible API
- **Server project:** `FoundryLocalLlmServer.Server`
- **Foundry Local:** `http://127.0.0.1:5273`, model `phi-4` by default (override via `FoundryLocal:Model`, `FoundryLocal:Endpoint`)
- **Integration tests:** `OpenAiCompatibilityTests` uses real Foundry Local with `[SkippableFact]` — skips gracefully when service not running. `UseStubResponses` removed from test config.
- **Test command:** `dotnet test ./FoundryLocalLlmServer.sln`
- **User:** Jeffrey Palermo

## Learnings

### 2026-05-12 — Real-mode integration tests with SkippableFact

- Removed `UseStubResponses=true` from `ServerFactory` config; tests now proxy to actual Foundry Local.
- `ServerFactory` implements `IAsyncLifetime` to call `FoundryServiceHelper.GetServiceUrlAsync()` before `ConfigureWebHost` runs; URL cached in `_foundryUrl`, injected as `FoundryLocal:Endpoint`.
- `IAsyncLifetime.DisposeAsync()` (returns `Task`) and `IAsyncDisposable.DisposeAsync()` (returns `ValueTask`) coexist without conflict on a `WebApplicationFactory<T>` subclass via explicit interface implementation.
- Assertions changed from prompt-echo string checks to structural: non-null + `Length > 0` — real LLMs don't echo the prompt back.
- `Xunit.SkippableFact` v1.4.13 was already in the project; `[SkippableFact]` + `Skip.If(url == null, ...)` is the correct pattern for environment-gated integration tests.

### 2026-06-02 — Build/test/smoke validation pass

- `dotnet build .\FoundryLocalLlmServer.sln` succeeds; `dotnet test .\FoundryLocalLlmServer.sln` passes with environment-gated skips when Foundry Local is not running (7 total, 4 passed, 3 skipped).
- Frontend checks pass with `npm run lint` and `npm run build`; `npm ci` may fail on Windows with an `EPERM` unlink against `@rolldown` native binary when the file is locked.
- Running `FoundryLocalLlmServer.Server` directly serves APIs but returns `404` at `/` without published `wwwroot`; for local end-to-end smoke, run frontend dev server with `SERVER_HTTP` pointed at backend and validate `/api/foundry` plus `/v1/chat/completions` through Vite proxy.

### 2026-06-03 — Removed Ollama fallback; restored clean in-process Foundry Local integration

- **Why:** Standing directive from Jeffrey — this codebase must NOT use Ollama. The model runs in-process via Foundry Local on the local RTX 4060 GPU, per the official Microsoft Foundry Local docs. A prior session had bolted an Ollama fallback onto the proxy.
- **What was removed:**
  - `FoundryLocalLlmServer.Server/OllamaFallbackOptions.cs` (deleted).
  - DI registration `builder.Services.Configure<OllamaFallbackOptions>(...)` in `Program.cs`.
  - All Ollama routing in the `/v1/chat/completions` handler — the `/api/chat` proxy, OpenAI↔Ollama format conversion, and the `ConvertOllamaToOpenAiFormat` helper.
  - `OllamaFallback` section from `appsettings.json` (Development settings had none).
  - Ollama comments/config in `FoundryLocalLlmServer.IntegrationTests/PhiFoundryGpuIntegrationTests.cs` (incl. renamed test `...ErrorsWithoutFallback`, dropped `OllamaFallback:Enabled=false` from `PhiFoundryServerFactory`).
- **Clean integration shape (`Program.cs`):** discover dynamic Foundry endpoint via `foundry service start` regex (`GetFoundryEndpointAsync`/`DiscoverFoundryEndpointAsync`) → resolve model alias against `GET /v1/models` preferring GPU/cuda variant (`ResolveModelAsync`/`IsGpuVariant`) → cap `max_tokens` to the model context window → proxy `/v1/chat/completions` (SSE stream + non-streaming) and `/v1/models` straight through, preserving the OpenAI contract. On a non-2xx from Foundry the upstream status+body is forwarded unchanged; when Foundry is genuinely unreachable after re-discovery+retry the handler returns **503 ProblemDetails** (decision #2) — never another engine. `SemaphoreSlim(1,1)` request gate and `UseStubResponses=true` stub mode preserved.
- **Verification:** `dotnet build ./FoundryLocalLlmServer.sln -v minimal` → 0 errors/0 warnings; unit tests 2/2 pass; stub-based integration tests pass. GPU-required and opencode-CLI integration tests fail clearly when the GPU model isn't downloaded / opencode isn't installed — intended behavior (Apoc handling GPU model availability separately). Grep of app+test code for "ollama"/"Ollama" → zero matches. Kill stale `MSBuild.exe` before building if the build hangs.

### 2026-06-16 — Latest WinML SDK confirmed + RTX 4060 CUDA EP made config-driven

- **WinML package version:** queried NuGet flat-container — published stable versions are 0.8.0.1 … 1.2.3. **1.2.3 is the latest stable** (no newer stable; ignored pre-release). csproj `Microsoft.AI.Foundry.Local.WinML` stays at **1.2.3**; project restores cleanly.
- **RTX 4060 (Ada Lovelace) EP:** CUDA is the preferred/most-compatible accelerator. The SDK's `DiscoverEps()` reports names like `CUDAExecutionProvider` and `NvTensorRtRtxExecutionProvider`. EP filter now matches `cuda`/`tensorrt`/`trtrtx`/`nv` substrings and **orders the preferred EP first** so CUDA is the active provider; other GPU EPs remain registered as fallbacks (never silently drops to CPU). `NvTensorRtRtxExecutionProvider` is the RTX-optimized alternative (TensorRT-RTX).
- **New config key:** `FoundryLocal:PreferredExecutionProvider` (default `"CUDA"`) added to `FoundryLocalOptions`, `appsettings.json`, and `appsettings.Development.json`. Config-driven, never hardcoded. Passed into `InitializeFoundryAsync(...)`.
- **Stale comments fixed:** removed "v1.2.0 SDK" reference in `Program.cs` (now version-agnostic: "Microsoft.AI.Foundry.Local.WinML SDK").
- **Unchanged on purpose:** `RuntimeIdentifiers=win-x64` pinning kept (non-Windows builds fail NETSDK1047). No Ollama/external fallback. 503 ProblemDetails on Foundry unreachable preserved. Stub mode for CI untouched.
- **Verification:** `dotnet restore` clean; `dotnet build -c Debug` (win-x64) → 0 errors (2 pre-existing unused-variable warnings). `dotnet test --filter "Category!=GPU-Required"` → unit tests 2/2 pass; the 3 opencode/Playwright/Aspire integration tests fail with "Value is null" only because the live Foundry GPU service + opencode CLI aren't present in this environment — intended, pre-existing, unrelated to this change.

### 2026-06-16 — EP-aware variant selection + qwen2.5-0.5b GPU default (Apoc end-to-end run)

- **SelectGpuVariant now EP-aware:** was blindly picking the first `DeviceType==GPU` variant, which selected the **OpenVINO (Intel) GPU variant** instead of NVIDIA, causing "Model … is not loaded" proxy mismatch. Now prefers the variant whose `ExecutionProvider` matches the configured preferred EP (CUDA), then any NVIDIA GPU EP, then any GPU. `EnsureModelLoadedAsync` threaded `preferredEp` through the call.
- **Proxy EP alignment:** `ResolveModelAsync` now matches the exact variant the runtime loaded (CUDA/TensorRT), so proxy `/v1/chat/completions` targets the right backend.
- **CUDA default restored:** Decision #6 compliance — was drifted to `NvTensorRtRtx` in `appsettings.json`, `appsettings.Development.json`, `FoundryLocalOptions`, and Program.cs fallback. All reset to `CUDA`.
- **Model default → `qwen2.5-0.5b`:** switched from `phi-4-mini-instruct-cuda-gpu:5` (whose GPU artifact yields token-0 garbage on 1.2.3) to `qwen2.5-0.5b-instruct-cuda-gpu:4` (proven coherent on GPU in live end-to-end test). Updated both `appsettings.json` and `appsettings.Development.json`.
- **End-to-end proof (Apoc 2026-06-16):** AppHost launched (0 console errors after clearing stale Aspire orphan sockets), Playwright clicked "Send Prompt", received 2586-char coherent reply (p.error=0, finish_reason=stop). nvidia-smi: VRAM +1537 MiB, util 77–82%, server.exe in compute-apps. Foundry log: "Loaded model (device=GPU)".
- **Decision #4 status:** In-process 1.2.3 WinML runtime resolves token-0 for compatible artifacts (qwen2.5-0.5b = coherent now; phi-4-mini = still token-0, waiting on compatible artifact from Microsoft). End-to-end proven live on RTX 4060.

### 2026-06-16 — Model listing & runtime model-switch API (backend slice)

- **New endpoints** (OpenAI `GET /v1/models` left untouched):
  - `GET /api/models` — lists selectable models with live state. Response:
    `{ "object": "list", "active": "<alias>", "data": [ { "id": "<alias>", "loaded": bool, "cached": bool, "active": bool } ] }`.
    `loaded` from `catalog.GetLoadedModelsAsync()` (matched by `IModel.Alias`), `cached` from `IModel.IsCachedAsync()`, `active` = current in-memory active model. Works in stub mode / pre-init (state defaults false; list still returned).
  - `POST /api/models/select` — body `{ "model": "<alias>" }`. Validates alias ∈ `AvailableModels` (400 if not). Under `_foundryRequestGate`: unloads loaded model(s) via `IModel.UnloadAsync()`, resolves alias, `SelectGpuVariant` (CUDA/EP-aware, excludes OpenVINO), downloads if not cached, `LoadAsync()`, updates active model, clears `aliasCache` + `cachedModelsExpiry`. Success: `{ "active", "id", "device", "executionProvider", "loaded": true }`. Stub mode: records alias only, `{ "active", "id": null, "device": "stub", "executionProvider": null, "loaded": false }`. Errors as ProblemDetails: 400 invalid/unknown, 503 runtime not initialized (no Ollama fallback), 500 load failed (server stays usable, reloads on next chat).
- **Config key:** `FoundryLocal:AvailableModels` (string[]) added to `FoundryLocalOptions` + both appsettings, seeded `["qwen2.5-0.5b"]`. Expanding the selectable set = add aliases to config, no code change. Empty → falls back to `[activeModel]`.
- **Active-model state:** in-memory `_activeModel` in `Program.cs`, initialized from `FoundryLocal:Model` (startup default), overridden by `/api/models/select`. `/v1/chat/completions` now defaults the `model` field to `_activeModel` when the request omits it (both stub and proxy paths); retry-path reload also uses `_activeModel`. All mutations under `_foundryRequestGate` so a switch never races in-flight chat.
- **Verification:** `dotnet build -c Debug` (win-x64) → 0 errors (2 pre-existing unused-var warnings). `dotnet test --filter "Category!=GPU-Required&Category!=Integration"` → UnitTests 2/2, IntegrationTests 4/4 passed.
- **Contract published:** `.squad/decisions/inbox/tank-model-switch-api.md` for Trinity (UI) and Switch (tests).

### 2026-06-16 — Supported model set finalized: qwen2.5-1.5b (default) + qwen2.5-0.5b

**Per Apoc's live GPU verification on RTX 4060:**
- **Default model:** `qwen2.5-1.5b` (coherent + proper OpenAI `tool_calls`; ~2.5 GB VRAM)
- **Fallback:** `qwen2.5-0.5b` (coherent, prose-only; ~1.8 GB VRAM)
- **`FoundryLocal:AvailableModels` configuration:** `["qwen2.5-1.5b", "qwen2.5-0.5b"]` (committed to both appsettings)

Excluded: phi-4-mini (token-0 degenerate on 1.2.3), all 7B models (95% VRAM on 8 GB → OOM-risk), qwen2.5-coder-3b (not in catalog).

The config-driven `AvailableModels` array now drives the `/api/models` list returned by GET and validated by POST. Any future model add/remove is a simple config edit with no code change required.

### 2026-06-16 — Apoc VRAM leak fix + context-bounding

Server now caps per-request context with FoundryLocalOptions.MaxPromptTokens (default 1024) and MaxResponseTokens (default 2048) via OpenAiChatHelpers.ApplyContextBounds in /v1/chat/completions. Fixed VRAM leak (peak 7867→3259 MiB, latency 175–190s→≤16s) caused by unbounded INPUT prompt arena. New RepeatedPromptVramTests (Playwright + nvidia-smi sampler) verifies peak ≤ 5000 MiB; all 25 integration tests passing.
