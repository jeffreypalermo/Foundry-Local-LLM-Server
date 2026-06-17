# Apoc — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript)
- **Aspire:** `FoundryLocalLlmServer.AppHost` — run with `dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj`
- **Aspire config:** `aspire.config.json`
- **CI:** `dotnet test ./FoundryLocalLlmServer.sln` — integration tests use `UseStubResponses=true` (no GPU needed)
- **User:** Jeffrey Palermo

## Key Learnings (Summary Timeline)

### 2026-05-11/2026-05-12 — opencode integration + error handling

- opencode 1.14.31 via Chocolatey; config at `C:\Users\JeffreyPalermo\.config\opencode\opencode.json`.
- Added `/v1/models` endpoint for model discovery.
- Wrapped proxy in try/catch, return **503 ProblemDetails** when Foundry Local unreachable.
- Live test: Playwright passed, real phi-4-mini CUDA reply in ~1.78s.

### 2026-06-03 — phi-4-mini regression diagnosis (Decision #4)

- Layer 1: `foundry model list` fails on empty `promptTemplate` field (GitHub #752/#757). Workaround: seed `~/.foundry/cache/models/foundry.modelinfo.json`.
- Layer 2: phi-4-mini produces constant token-0 (degenerate) on 0.8.119. Runtime/artifact version gap; no upgrade path.
- Decision: Track real fix once v1.2.0+ installable.

### 2026-06-16 — End-to-end GPU success (three bugs fixed)

1. **SelectGpuVariant GPU-agnostic** → picked OpenVINO instead of NVIDIA. Made selection EP-aware (prefer CUDA, else any NVIDIA).
2. **Config drift:** appsettings incorrectly said `NvTensorRtRtx`. Restored `CUDA` everywhere.
3. **Aspire stale sockets** in `~/.aspire/cli/backchannels/` (PID reuse). Deleted orphans; clean restart.

Result: qwen2.5-0.5b CUDA inference on RTX 4060 peak **1305–1561 MiB**, util **77–82%**. phi-4-mini still token-0; qwen coherent.

### 2026-06-16 — Supported-model sweep (Decision #7)

Tested qwen2.5-1.5b, qwen2.5-0.5b, phi-4-mini, qwen2.5-coder-7b, qwen2.5-7b on RTX 4060.

**Confirmed working:** qwen2.5-1.5b (coherent + tool_calls + 2.5 GB) **[default]**, qwen2.5-0.5b (coherent + no tool_calls + 1.8 GB).
**Excluded:** phi-4-mini (token-0), coder-7b (95% VRAM), 7b (95% VRAM), coder-3b (not in catalog).

Key finding: tool_calls ≠ size. Only qwen2.5-1.5b Instruct returns OpenAI-compatible `tool_calls`.

### 2026-06-16 — Integration test rework: in-process HTTP-driven (Decision #12)

- Removed all `foundry`/`opencode` CLI usage from tests.
- **FoundryServiceHelper:** HTTP-driven discovery (`GET /api/foundry`, `POST /api/models/select`).
- **ServerFixture:** Starts Server EXE once on :5537 with stubs OFF, publishes frontend, waits for ready, kills on dispose. Prefers `win-x64` RID copy.
- **Per-test model switching** via `/api/models/select`; reuses cached models (no large re-downloads).
- **All tests pass or fail** (zero skips per Decision #11). Structural stub tests + per-model GPU theory tests.
- Bug hunt: typo in hand-written curl JSON body (missing `}`), not a server bug. Program.cs business logic unchanged. Two test-side fixes: tool_calls awareness for 0.5b, guard for empty choices[] in SSE.

**Final result:** Build 0 errors, **24/24 tests PASS**, zero skips, VRAM back to 24 MiB idle.

### 2026-06-16 — VRAM leak diagnosis & fix (Decision #13)

**Root cause:** CUDA KV-cache arena sized to INPUT prompt length, never released (high-water-mark). UI resends full transcript with no max_tokens → unbounded input → OOM.

**Evidence:** 10-iter repro: VRAM climbed **2601→7867 MiB**, loadedModels stayed 1, late iterations 175–190s. Model switch only dropped 7851→5913 (no reclaim). Isolated short-context flat at ~2361 MiB. Input dominates arena size: ~500 tok→3249, ~2000 tok→6331, ~5000 tok→saturates.

**Fix:** Server-side context bounding.
- New `FoundryLocalOptions.MaxPromptTokens` (1024), `MaxResponseTokens` (2048).
- New `OpenAiChatHelpers.ApplyContextBounds(...)`: caps max_tokens, trims old turns, head-truncates oversized messages.
- Hardened `EnsureModelLoadedAsync` idempotent (IsLoadedAsync guard, unload-others-before-reload).
- New `RepeatedPromptVramTests` (Playwright + nvidia-smi sampler): peak ≤ 5000 MiB, growth < 2500 MiB.

**Before/after:** peak 7867→3259 MiB, latency 175–190s→≤16s.

**Final result:** `dotnet build` 0/0, Unit 8/8 PASS, Integration 25/25 PASS (+1 new VRAM test, +6 ApplyContextBounds tests), VRAM 24 MiB idle.

**Build gotcha:** `ServerFixture` prefers `win-x64` RID copy. Plain `dotnet build` only updates non-RID folder; must use `-r win-x64`.

---
