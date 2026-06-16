# Squad Decisions

## Active Decisions

### 1. Opus 4.8 vs Gemma4 Routing by Squad Role

**Date:** 2026-06-02  
**Author:** Neo  
**Status:** Accepted

**Context:** The squad needs explicit, unambiguous model routing so high-stakes reasoning work consistently uses the strongest model, while operational/support roles stay on a fast local model.

**Decision:**
- Add explicit model registry entries in `.squad/config.json`:
  - `opus48` -> `claude-opus-4.8`
  - `gemma4_ollama` -> `ollama/gemma4`
- Route members by role:
  - **Opus 4.8:** Neo, Trinity, Tank, Switch
  - **Gemma4 (Ollama):** Apoc, Scribe, Ralph
- Align each member charter model section to reference the exact config key and concrete backing model.

**Rationale:** Lead architecture, implementation, and test strategy require stronger long-chain reasoning and higher confidence under ambiguity. Logging, monitoring, and routine infra workflows are mostly procedural/summarization-heavy and benefit from local low-latency throughput.

**Consequences:**
- Routing is now deterministic across config and charters.
- The squad reduces premium-model usage where it provides limited marginal value.
- Escalation remains possible through documented fallback paths for exceptional complexity.

---

### 2. Proxy Error Handling for Foundry Local Unavailability

**Date:** 2026-05-11  
**Author:** Apoc  
**Status:** Implemented (commit 396b3ee)

**Context:** During live exploratory testing with Playwright, Foundry Local was not running on port 5273. The `/v1/chat/completions` proxy endpoint had no error handling for network failures.

**Decision:**
- Return 503 Service Unavailable (not 500) when upstream Foundry Local is unreachable.
- Include a user-readable `detail` field: "Could not reach Foundry Local at {endpoint}. Ensure the service is running."
- Frontend parses ProblemDetails and displays the detail/title field in error paragraph.

**Rationale:** 503 is semantically correct for upstream dependency failures. Surfacing the endpoint URL immediately tells developers where to look. No change to happy path or stub mode.

---

### 3. opencode uses foundry-local provider for phi-4

**Date:** 2026-05-11  
**Author:** Apoc  
**Status:** Accepted

**Context:** The project runs an ASP.NET Core proxy server at port 5537 that forwards OpenAI-compatible requests to Microsoft Foundry Local (GPU inference engine). opencode can use any OpenAI-compatible endpoint.

**Decision:**
- Configure opencode to use the local Foundry Local LLM server as a custom provider named `foundry-local`, targeting `phi-4` at `http://localhost:5537/v1`.
- Add `GET /v1/models` endpoint to `Program.cs` (required by opencode's SDK for model discovery).
- Add `foundry-local` provider entry to `~/.config/opencode/opencode.json` (user-level global config).

**Consequences:**
- `opencode run --model foundry-local/phi-4 "prompt"` works without any API key.
- Foundry Local GPU service must be running on port 5273 for real inference.
- opencode config is user-specific (not committed to repo).

### 4. Foundry Local phi-4-mini GPU Root-Cause — Catalog Regression on Stuck Build 0.8.119

**Date:** 2026-06-03  
**Author:** Apoc (DevOps / Infra)  
**Status:** Diagnosed — partial mitigation applied; full fix blocked on upstream (Microsoft)  
**Requested by:** Jeffrey Palermo (standing directive below)

**Context:** `Phi-4-mini-instruct-cuda-gpu:5` served real GPU completions via Foundry Local on 2026-05-12. As of 2026-06-03 the model can no longer be listed, downloaded, or run—a regression, not a setup mistake. Environment is healthy (RTX 4060 VRAM verified, CUDA/TensorRT EPs auto-registered).

**Root Cause (two layers):**
1. **Catalog parse crash:** `foundry model list` fails with `"The input does not contain any JSON tokens"` on an empty `promptTemplate` field in the first unfiltered catalog entry (`Phi-4-reasoning-generic-cpu:1`), blocking list/download/run for all models (GitHub issues #752, #757).
2. **Runtime/artifact version gap:** With Layer 1 worked around, models load and execute on GPU (VRAM 5267→7385 MiB, 93–95% CUDA util), but completions degenerate to constant token id 0. Artifacts were rebuilt for Foundry Local v1.2.0 (released 2026-05-28) using ONNX Runtime 1.26.0; the frozen 0.8.119 runtime is incompatible. No upgrade path exists (winget/GitHub releases stuck at 0.8.119).

**Decision:**
1. Mitigate Layer 1 by seeding a valid local catalog snapshot (`foundry.modelinfo.json`) into `%USERPROFILE%\.foundry\cache\models\`, then manually finalize hung downloads.
2. Do NOT treat degenerate output as an app bug or reintroduce Ollama—Layer 2 is upstream Microsoft runtime/artifact incompatibility.
3. Track real fix: Foundry Local v1.2.0+ must become installable. Re-test GPU serving once available.

**Consequences:** Catalog-parse blocker mitigated; GPU models can be downloaded/loaded/executed again. Coherent chat completions remain blocked until v1.2.0+ ships—independent of any change in this codebase. Application's Foundry-Local-only proxy is correct as-is.

---

### 5. User Directive — Foundry Local Only, No Ollama, GPU-Bound

**Date:** 2026-06-03T21:38:34-05:00  
**Author:** Jeffrey Palermo (via Copilot)  
**Status:** Active / Standing

**Directive:** This codebase must NOT use Ollama. Models must run in-process via Foundry Local (downloaded and served by the Foundry Local runtime), running on the local RTX 4060 GPU—as described in the Foundry Local docs. Supersedes any prior Ollama-fallback approach.

---

### 6. CUDA as the Explicit, Config-Driven GPU Execution Provider

**Date:** 2026-06-16  
**Author:** Tank (Backend Dev)  
**Status:** Proposed

**Context:** Target hardware is the onboard NVIDIA RTX 4060 (Ada Lovelace). Server hosts Foundry Local in-process via `Microsoft.AI.Foundry.Local.WinML`. Latest published stable is 1.2.3 (already pinned). Previously GPU EP selection was hardcoded; preferred provider was not explicit.

**Decision:**
- Add config key `FoundryLocal:PreferredExecutionProvider` (default `"CUDA"`), surfaced in `FoundryLocalOptions`, `appsettings.json`, and `appsettings.Development.json`.
- EP registration prefers the configured provider (CUDA for RTX 4060) and orders it first, while registering other discovered NVIDIA GPU EPs (e.g., `NvTensorRtRtx`) as fallbacks so inference never silently drops to CPU.
- CUDA chosen as default over TensorRT-RTX for broad compatibility; switchable via config without code changes.

**Respected Constraints:**
- `RuntimeIdentifiers=win-x64` pinning kept.
- No Ollama/external-engine fallback; existing 503 ProblemDetails on Foundry unreachable.
- Stub mode (`UseStubResponses=true`) for CI untouched.

**Consequences:** GPU acceleration target is now explicit and configurable per environment. Operators can flip CUDA ↔ NvTensorRtRtx via config without rebuild.

---

### 7. Remove Ollama Fallback — Restore In-Process Foundry Local Integration

**Date:** 2026-06-03  
**Author:** Tank (Backend Dev)  
**Status:** Implemented  
**Requested by:** Jeffrey Palermo (standing directive #5)

**Context:** A prior session added an Ollama fallback to `/v1/chat/completions`: when Foundry Local returned an error or was unreachable, the server re-routed requests to Ollama `/api/chat` and converted between OpenAI and Ollama wire formats. This violates the project's charter—the model must run **in-process via Foundry Local** on the local RTX 4060 GPU, exactly as Microsoft Foundry Local documentation describes. No external inference services.

**Decision:** Remove all Ollama coupling and restore a clean, Foundry-Local-only proxy.

**Removed:**
- `FoundryLocalLlmServer.Server/OllamaFallbackOptions.cs` (deleted).
- DI registration in `Program.cs`.
- Ollama routing in `/v1/chat/completions` (proxy, streaming, OpenAI↔Ollama conversions).
- `OllamaFallback` section in `appsettings.json`.
- Ollama references in tests.

**Preserved / Restored:**
1. Dynamic endpoint discovery via `foundry service start` with HTTP port regex.
2. Model alias resolution against `GET /v1/models`, preferring GPU/CUDA variant.
3. `max_tokens` capped to model's context window.
4. Straight-through proxy of `/v1/chat/completions` and `/v1/models` (streaming + non-streaming), preserving OpenAI contract.
5. When Foundry Local unreachable after retry: return **503 ProblemDetails** with helpful detail—never fallback.
6. Stub mode (`UseStubResponses=true`) for CI/tests preserved.
7. `SemaphoreSlim(1,1)` request gate preserved (Foundry Local crashes on concurrent streaming).

**Consequences:** Server has exactly one inference backend: Foundry Local. Failures surface as errors (or 503), making misconfiguration obvious instead of silently masked. Build succeeds (0 errors); unit tests pass (2/2); stub-based integration tests pass. Grep for "ollama"/"Ollama" in app/test code returns zero matches.

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
