# Apoc — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript)
- **Aspire:** `FoundryLocalLlmServer.AppHost` — run with `dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj`
- **Aspire config:** `aspire.config.json`
- **CI:** `dotnet test ./FoundryLocalLlmServer.sln` — integration tests use `UseStubResponses=true` (no GPU needed)
- **User:** Jeffrey Palermo

## Key Learnings (Summarized)

### 2026-05-11 — opencode integration (phi-4 via Foundry Local proxy)

- **opencode version:** 1.14.31 (installed via Chocolatey)
- **Config location:** `C:\Users\JeffreyPalermo\.config\opencode\opencode.json`
- **Provider format:** `@ai-sdk/openai-compatible` npm package; config key is `"provider"`, NOT `"providers"`. Each entry requires `npm`, `name`, `options.baseURL`, and `models`.
- **`/v1/models` endpoint required:** added to `Program.cs` returning phi-4 in OpenAI list format.
- **CLI invocation:** `opencode run --model foundry-local/phi-4 "prompt"` works without API key.
- **Foundry Local GPU service:** Must be running on port 5273 for real inference. `UseStubResponses=true` env var bypasses for testing.

### 2026-05-11/2026-05-12 — Error handling and validation

- **Bug fix (commit 396b3ee):** `Program.cs` wrapped `proxyClient.SendAsync` in try/catch for `HttpRequestException`. Returns **503 ProblemDetails** with clear message when Foundry Local unreachable.
- **Frontend:** `App.tsx` parses ProblemDetails JSON and surfaces `detail`/`title` as UI error message.
- **Verified:** Config endpoint, model discovery, stub response, error handling all working.
- **Live test (2026-05-12):** Playwright passed — clicked "Send Prompt", got real LLM response in ~1.78s via phi-4-mini CUDA.

### 2026-06-03 — Foundry Local phi-4-mini GPU root-cause (Decision #4)

**Summary of extensive investigation:**
- **Symptom:** `Phi-4-mini-instruct-cuda-gpu:5` (worked 2026-05-12) stopped listing/downloading/running.
- **Layer 1 (catalog parse crash):** `foundry model list` fails on empty `promptTemplate` field in first catalog entry — Microsoft acknowledged (GitHub #752/#757), winget stuck at 0.8.119.
  - **Workaround:** seed `%USERPROFILE%\.foundry\cache\models\foundry.modelinfo.json` with known-good snapshot.
- **Layer 2 (runtime/artifact gap):** phi-4-mini:5 produces constant token id 0 (`!!!!`) on 0.8.119. Artifacts were rebuilt for Foundry Local v1.2.0 (ORT 1.26.0); 0.8.119 runtime incompatible. **No upgrade path** (v1.0.0/v1.1.0/v1.2.0 have zero GitHub release assets).
- **Decision:** Track real fix when v1.2.0+ becomes installable.

### 2026-06-16 — CUDA execution provider config (Tank contribution)

- Tank added `FoundryLocal:PreferredExecutionProvider` (default `"CUDA"`) to FoundryLocalOptions and appsettings files.
- EP registration now prefers configured provider first; other discovered NVIDIA GPU EPs as fallbacks. Never silently drops to CPU.
- RTX 4060 targeted via CUDA default; TensorRT-RTX available as config alternative.

### 2026-06-16 — AppHost GPU run: DECISION #4 RESOLVED (end-to-end success)

**Mission:** Run AppHost clean, drive frontend with Playwright, get coherent reply, prove RTX 4060 doing inference.

**Three bugs fixed:**
1. **SelectGpuVariant was GPU-agnostic** — picked first `DeviceType==GPU`, which selected OpenVINO (Intel GPU) instead of NVIDIA. Symptoms: nvidia-smi flat (24 MiB), proxy "Model not loaded" mismatch. **Fix:** made variant selection EP-aware — prefer variant matching configured preferred EP (CUDA), else any NVIDIA GPU EP (deliberately exclude "openvino"), else any GPU. Threaded `preferredEp` into `EnsureModelLoadedAsync` and made `ResolveModelAsync` prefer NVIDIA match so proxy + loader agree.
2. **Config drift vs Decision #6:** appsettings, FoundryLocalOptions, and Program.cs all said `NvTensorRtRtx` instead of `CUDA`. Restored `CUDA` everywhere.
3. **Aspire console error:** stale socket files in `%USERPROFILE%\.aspire\cli\backchannels\` (PID reuse by protected `svchost`). **Fix:** deleted orphan sockets; clean restart → zero error lines.

**RTX 4060 GPU proof (baseline 24 MiB / 0% util):**
- qwen2.5-0.5b CUDA inference: VRAM peak **1305–1561 MiB**, util **77–82%**, in `nvidia-smi --query-compute-apps`.

**Decision #4 re-evaluation (KEY FINDING):**
- `phi-4-mini-instruct-cuda-gpu:5` (8-bit MatMulNBits artifact) STILL token-0 on in-process 1.2.3 (FRESH re-download; NOT corrupt cache).
- `qwen2.5-0.5b-instruct-cuda-gpu:4` (int4-rtn-block-32) produces **COHERENT** output on 1.2.3 (was word-salad on 0.8.119).
- **Conclusion:** In-process 1.2.3 WinML runtime **RESOLVES Layer-2 token-0/word-salad for compatible artifacts.** phi-4-mini:5's 8-bit build remains incompatible exception.

**Action:** Switched `FoundryLocal:Model` (both appsettings files) from `phi-4-mini` to `qwen2.5-0.5b` (proven coherent on GPU).

**Playwright result:** Navigated to Vite URL, clicked "Send Prompt", assistant reply rendered: **2586 chars**, coherent, on-topic. `p.error` count = 0.

**CI safety:** Build 0 errors, UnitTests 2/2 pass, stub mode + 503 paths untouched, win-x64 RID kept, no Ollama.

**Files changed:** `Program.cs`, `FoundryLocalOptions.cs`, `appsettings.json`, `appsettings.Development.json`.
