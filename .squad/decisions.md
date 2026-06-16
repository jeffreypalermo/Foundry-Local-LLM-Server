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
**Status:** **Resolved (2026-06-16)** — in-process WinML 1.2.3 delivers coherent GPU inference for compatible artifacts  
**Requested by:** Jeffrey Palermo (standing directive below)

**Context:** `Phi-4-mini-instruct-cuda-gpu:5` served real GPU completions via Foundry Local on 2026-05-12. As of 2026-06-03 the model could no longer be listed, downloaded, or run—a regression, not a setup mistake. Environment is healthy (RTX 4060 VRAM verified, CUDA/TensorRT EPs auto-registered).

**Root Cause (two layers):**
1. **Catalog parse crash:** `foundry model list` fails with `"The input does not contain any JSON tokens"` on an empty `promptTemplate` field in the first unfiltered catalog entry (`Phi-4-reasoning-generic-cpu:1`), blocking list/download/run for all models (GitHub issues #752, #757).
2. **Runtime/artifact version gap:** With Layer 1 worked around, models load and execute on GPU, but completions degenerate to constant token id 0 depending on artifact build/runtime compatibility.

**Original Decision (2026-06-03):**
1. Mitigate Layer 1 by seeding a valid local catalog snapshot (`foundry.modelinfo.json`) into `%USERPROFILE%\.foundry\cache\models\`, then manually finalize hung downloads.
2. Do NOT treat degenerate output as an app bug or reintroduce Ollama—Layer 2 is upstream Microsoft runtime/artifact incompatibility.
3. Track real fix: Foundry Local v1.2.0+ must become installable. Re-test GPU serving once available.

**2026-06-16 Update (Apoc end-to-end live run):**
The in-process `Microsoft.AI.Foundry.Local.WinML` 1.2.3 runtime (already pinned in project) resolves the token-0 degeneration **for compatible artifacts**:
- `qwen2.5-0.5b-instruct-cuda-gpu:4` (int4-rtn-block-32): **coherent** output; same model was word-salad on 0.8.119 CLI
- `Phi-4-mini-instruct-cuda-gpu:5` (8-bit MatMulNBits): STILL token-0 (`!!!!`) on 1.2.3; artifact remains incompatible

**End-to-End Proof (2026-06-16):**
- AppHost launched, zero console errors (after clearing stale Aspire orphan sockets)
- Playwright clicked "Send Prompt" → received 2586-char coherent reply (`p.error` = 0, `finish_reason=stop`)
- RTX 4060 proven via nvidia-smi: VRAM 24→1561 MiB, util 77–82%, server.exe in compute-apps, Foundry log shows "Loaded model (device=GPU)"

**Consequences:** 
- Layer 2 blocker is no longer universal; in-process 1.2.3 serves GPU completions for compatible models.
- phi-4-mini's 8-bit build is the exception (likely waiting on compatible artifact release from Microsoft).
- Recommended default model: `qwen2.5-0.5b` (proven coherent on GPU).
- Application's Foundry-Local-only proxy, Decision #6 CUDA default, and EP-aware variant selection all confirmed working live.

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

### 8. Chat UI — Newest Exchange On Top + Explicit Dark Theme

**Date:** 2026-06-16  
**Author:** Trinity (Frontend Dev)  
**Requested by:** Jeffrey Palermo  
**Status:** Implemented

**Context:** The main page rendered the chat transcript oldest-first with an OS-dependent pastel scheme (`color-scheme: light dark`), giving weak contrast and burying the most recent response below older turns.

**Decision:**
- Render the transcript newest-first while keeping each User message directly above its own Assistant reply: a render-time `groupExchanges(chat)` folds the flat `chat[]` into `{ user, assistant }` pairs, which are mapped then reversed (not a naive flat reverse). State storage is unchanged. While busy, a "Generating response…" pending bubble shows at the top.
- Replace the OS-dependent palette with an explicit, self-contained dark theme: bg `#0f172a`, surfaces `#1e293b`/`#273449`, border `#3b4a63`, text `#f1f5f9` (muted `#b6c2d6`), indigo accent `#6366f1`/`#818cf8`. User cards are blue-tinted with a blue left border; assistant cards violet-tinted with a violet left border. Body text targets WCAG AA.

**Constraints respected:** All Playwright-critical selectors preserved (`button[type='submit']` "Send Prompt"/"Running...", `article.message.user`, `article.message.assistant p`, `p.error`, `p.config-line > strong` "Model:"). Frontend-only; `npm run build` passes (tsc + vite, 0 errors), `eslint src` clean.

**Note for testers (Switch):** With newest-on-top, `article.message.assistant p` `.First` now resolves to the MOST RECENT assistant reply (previously oldest). Future ordering assertions should account for this.

---

### 9. Supported Foundry Local Models — Authoritative Determination (RTX 4060, 8 GB)

**Date:** 2026-06-16  
**Author:** Apoc (DevOps / Infra)  
**Status:** Determined — live GPU verification on Jeffrey Palermo's RTX 4060 Laptop (8 GB)  
**Requested by:** Jeffrey Palermo

**Context:** Multi-model support requires an authoritative list of models that load coherently on the target GPU and support tool calling for opencode integration.

**Decision:**
In-process Foundry Local via `Microsoft.AI.Foundry.Local.WinML` 1.2.3 was tested against candidate models on RTX 4060 8 GB. Each model was downloaded via `POST /api/models/select` (EP-aware CUDA variant selection), then two probes were run: coherence (>10 chars, not degenerate) and tool calling (OpenAI `tool_calls` object, not text).

**Verified Supported Set:**
- **qwen2.5-1.5b**: coherent ✅, proper OpenAI `tool_calls` ✅, ~2.5 GB VRAM, **default model & best for opencode**
- **qwen2.5-0.5b**: coherent ✅, prose-only (no tool calling), ~1.8 GB VRAM, lightweight fallback

**Excluded Candidates:**
- **phi-4-mini** (`Phi-4-mini-instruct-cuda-gpu:5`): degenerate output (token-0 `!!!!`), incompatible 8-bit artifact on 1.2.3
- **qwen2.5-coder-7b**: XML-wrapped tool calls (not parsed), 95% VRAM (7791/8188 MiB), OOM-risk headroom
- **qwen2.5-7b**: 7B class ~7.8 GB (95% VRAM); unverified due to CDN throttling
- **qwen2.5-coder-3b**: does not exist in catalog

**Configuration Committed:**
```json
"FoundryLocal": {
  "Model": "qwen2.5-1.5b",
  "AvailableModels": [ "qwen2.5-1.5b", "qwen2.5-0.5b" ]
}
```

**Consequences:** Multi-model backend/UI can be tested against an authoritative, live-verified set. Tool calling via opencode is now reliably supported. Tank and Switch can assume this exact set.

---

### 10. API Contract — Model Listing & Switching (Backend)

**Date:** 2026-06-16  
**Author:** Tank (Backend Dev)  
**Status:** Implemented — backend ready for Trinity (UI) and Switch (tests)  
**Requested by:** Jeffrey Palermo

**Context:** The multi-model feature requires runtime model switching without redeploying the server.

**Decision:**
- Add `GET /api/models` — returns configured candidate models with live state (`loaded`, `cached`, `active`). Response shape is `{ object: "list", active: alias, data: [{ id, loaded, cached, active }] }`.
- Add `POST /api/models/select` — switches the active model. Request body is `{ model: alias }`. Response returns `{ active, id (full variant name), device, executionProvider, loaded }` on success, or RFC7807 ProblemDetails (400/503) on error.
- Configuration `FoundryLocal:AvailableModels` drives the selectable set; adding an alias to the array is all that is needed for expansion.
- In-memory active model state (`_activeModel`) initialized from `FoundryLocal:Model` at startup. Mutations guarded by `_foundryRequestGate` (prevents races with in-flight chat).
- Stub mode returns configured list with `loaded=false`, `device="stub"`, `executionProvider=null`.

**Constraints Respected:** Foundry-Local-only (no Ollama), 503 ProblemDetails on unavailability, CUDA EP-aware variant selection, CI stub mode, `win-x64` RID.

**Verification:** Build 0 errors, unit tests 2/2 passed, integration tests 4/4 passed. `--filter "Category!=GPU-Required&Category!=Integration"` GREEN.

---

### 11. Model Picker Dropdown + Hot-Swap (Frontend)

**Date:** 2026-06-16T15:35:27-05:00  
**Author:** Trinity (Frontend Dev)  
**Status:** Implemented (proposed for ratification)  
**Requested by:** Jeffrey Palermo

**Context:** UI needs a user-facing way to switch models at runtime, leveraging Tank's new backend endpoints.

**Decision:**
- Add labeled `<select id="model-select">` in a new `div.model-picker` directly under the config line. Preserve `p.config-line > strong` (Playwright-critical).
- Populate from `GET /api/models` on mount; annotate options with ` • loaded` / ` • cached` status indicators.
- On selection change: `POST /api/models/select { model }`. During the in-flight switch, disable select/textarea/Send button, set `aria-busy`, and show `span.switching-status` "Switching model… (unloading + loading on GPU)".
- On success: update `activeModel`, `models[].active` flags, and `config.model` (config line follows). Subsequent chat sends `model: activeModel ?? config.model`.
- On error: parse RFC7807 ProblemDetails into existing `p.error` style; select reverts to still-active model (no optimistic mutation).

**Constraints Respected:** Frontend-only, all Playwright-critical selectors preserved, TypeScript strict, `npm run build` 0 errors, `npx eslint src` clean.

---

### 12. Parameterized Per-Model Integration Test Harness

**Date:** 2026-06-16  
**Author:** Switch (Tester)  
**Status:** Implemented  
**Requested by:** Jeffrey Palermo

**Context:** Integration tests must verify both Tank's model endpoints and per-model coherence/tool-calling, but without hardcoding a single model.

**Decision:**
- Model set is **configuration-driven**: read `FoundryLocal:AvailableModels` from `appsettings.json` in `SupportedModelData.cs`; static fallback `["qwen2.5-1.5b","qwen2.5-0.5b"]` if unreadable.
- **Capability matrix** (empirical, not config): tool-calling support is encoded as `ToolCallingCapableAliases = { "qwen2.5-1.5b" }`. `[MemberData]` source `SupportedModels()` yields `(alias, supportsToolCalls)` rows.
- **Structural tests** (CI, stub mode, no GPU): GET `/api/models` lists configured set; POST `/api/models/select` returns stub shape per model; unknown model returns 400.
- **GPU-Required tests** (`[SkippableTheory]`, `[Trait("Category","GPU-Required")]`): per-model load, coherence, tool-calling assertions.
- **Precondition-gated tests now SKIP (not FAIL):** Three pre-existing integration tests (`AspireGenerationIntegrationTests`, `OpenCodeIntegrationTests`, `PlaywrightIntegrationTests`, plus 4 `PhiFoundryGpuIntegrationTests`) converted from `[Fact]`→`[SkippableFact]` / `[Theory]`→`[SkippableTheory]`, guard with `Skip.If/IfNot`. `AspireGenerationIntegrationTests` now targets `SupportedGenerationModels` (reads config) instead of hardcoded phi-4-mini; `OpenCodeIntegrationTests` uses `SupportedModelData.DefaultModel` (qwen2.5-1.5b).

**Verification:** Build 0 errors. `--filter "Category!=GPU-Required"` → Unit 2 passed, Integration 8 passed, 0 failed. `--filter "Category=GPU-Required"` → 14 skipped, 0 failed.

**Consequences:** Adding a model to `appsettings.json` automatically includes it in integration tests; no test code changes needed. CI is now GREEN without GPU. GPU dev box retains full coverage.

---

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
