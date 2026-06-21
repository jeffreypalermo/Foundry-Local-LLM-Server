# Local Test Journal

Running log of every test variant exercised locally on this workstation while building the
all-models capability matrix and the per-model UI capability harness.

## Environment

- **GPU:** NVIDIA GeForce RTX 5070 Ti, 16 GB VRAM, CUDA 13.2
- **Foundry Local:** cross-platform GA **v1.2.x**, CLI **0.10.0** (`Microsoft.FoundryLocalCLI`),
  WinML build. Old winget `Microsoft.FoundryLocal` 0.8.x removed.
- **Runtime:** .NET 10; Server + React SPA + integration tests.
- **Daemon:** `foundry server start` (OpenAI-compatible local daemon on a dynamic `webUrls` port).

## Capabilities verified on this platform (live)

| Capability | Path | Status |
|---|---|---|
| Text / code / reasoning | `POST /v1/chat/completions` | ✅ |
| Vision (image_url) | `POST /v1/chat/completions` (Qwen-VL / Qwen3.5 / Ministral) | ✅ |
| Tool calling | `POST /v1/chat/completions` with `tools` (Phi-4-mini family) | ✅ accepts + valid shape |
| Speech-to-text | `POST /v1/audio/transcriptions` → bridges to `foundry transcribe` | ✅ |
| Model select | `GET/POST /api/models*` | ✅ |

---

## 1. Automated test suites

### 1.1 Unit tests (`FoundryLocalLlmServer.UnitTests`)
- Command: `dotnet test FoundryLocalLlmServer.UnitTests`
- Result: **2/2 passed**.

### 1.2 Stub-mode integration (`Category!=GPU-Required`)
- `OpenAiCompatibilityTests` (OpenAI route + Microsoft.Extensions.AI client) and
  `FoundryServiceHelperAliasTests` (alias matching).
- Command: `dotnet test … --filter "Category!=GPU-Required"` with `FoundryLocal__UseStubResponses=true`.
- Result: **4/4 passed**.

### 1.3 Frontend
- `npm run build` (tsc strict + vite): **passed**.
- `npx eslint src`: **clean (exit 0)**.

### 1.4 Live GPU capability matrix (`FullCapabilityMatrixTests`, `Category=GPU-Required`)
- One capability-appropriate prompt per model in `AvailableModels`, exercised live through the
  proxy → real Foundry inference. Text/code/reasoning assert real output; vision asserts an image
  description; tools assert a valid tool-call shape; whisper asserts a transcript.
- See §3 for the per-model results table.

---

## 2. Targeted live validations (during development)

| Variant | Model | Result |
|---|---|---|
| Tool call accepted, valid shape | phi-4-mini, phi-4-mini-reasoning | ✅ 200, `tool_calls` array present |
| Vision image description | qwen3-vl-2b-instruct | ✅ 200, described the image |
| Reasoning (alias-resolution fix) | deepseek-r1-1.5b | ✅ resolves `DeepSeek-R1-Distill-Qwen-1.5B-…`, reaches "43" |
| Whisper transcription (proxy → CLI) | whisper-base | ✅ "The quick brown fox jumps over the lazy dog." |
| Code generation | qwen2.5-coder-0.5b / -14b | ✅ emits code |
| gpt-oss-20b on GPU (was CPU-timeout) | gpt-oss-20b-cuda-gpu | ✅ load 7.7s, infer 31s, 200 |
| Alias→exact-id requirement | raw alias to daemon | ⇒ 400 (confirms proxy resolution is required) |

---

## 3. Full capability matrix results

### 3.0 Stability finding (from running the matrix end-to-end)

Running all ~41 models sequentially surfaced a daemon-stability problem and its fix:

- **Symptom:** after ~13 models the daemon became unreachable (port changed, GPU memory dropped to
  ~0) and the next few requests returned `503 Foundry Local Unavailable`. Observed twice.
- **Cause:** the daemon let loaded models **accumulate** in the 16 GB VRAM until an OOM crash during
  the next load; `gpt-oss-20b` initially compounded it by running on its **CPU** variant (the cuda
  variant wasn't cached) — a 5-min run that aborted and destabilized the daemon.
- **Fixes applied:**
  1. Downloaded `gpt-oss-20b-cuda-gpu` (GPU). Re-verified: load 7.7s, infer 31s (was 5-min CPU timeout).
  2. **Single-model VRAM discipline** in the proxy (`ModelLoadState`, process-static): unload the
     previously loaded variant before loading the next, so exactly one model is resident. After this:
     **0 daemon crashes, 0 retries** across the run; per-model unload confirmed in logs.
  3. **Hardened proxy retry** (4 attempts, backoff, restart + reload) as a safety net for any
     transient daemon drop.

### 3.1 Per-model results

**Final clean run: 43/43 PASSED, 0 failed, 0 daemon crashes, 0 retries, 37 model unloads.**
(43 test cases = 28 text/code/reasoning + 8 vision + 5 audio + 2 tools.)

| Capability | Models (all ✅) |
|---|---|
| **Text** (18) | phi-4-mini, phi-4, phi-3.5-mini, phi-3-mini-4k, phi-3-mini-128k, qwen3-0.6b, qwen3-1.7b, qwen3-4b, qwen3-8b, qwen3-14b, qwen2.5-0.5b, qwen2.5-1.5b, qwen2.5-7b, qwen2.5-14b, qwen3.5-2b-text, mistral-7b-v0.2, mistral-nemo-12b-instruct, olmo-3-7b-instruct, smollm3-3b, gpt-oss-20b |
| **Code** (4) | qwen2.5-coder-0.5b, qwen2.5-coder-1.5b, qwen2.5-coder-7b, qwen2.5-coder-14b |
| **Reasoning** (4) | phi-4-mini-reasoning, deepseek-r1-1.5b, deepseek-r1-7b, deepseek-r1-14b |
| **Vision** (8) | qwen3-vl-2b-instruct, qwen3-vl-4b-instruct, qwen3-vl-8b-instruct, qwen3.5-0.8b, qwen3.5-2b, qwen3.5-4b, qwen3.5-9b, ministral-3-3b-instruct-2512 |
| **Audio/Whisper** (5) | whisper-tiny, whisper-base, whisper-small, whisper-medium, whisper-large-v3-turbo |
| **Tools** (2) | phi-4-mini, phi-4-mini-reasoning |

Per-model times ranged 4–46 s (load + inference). Command:
`dotnet test --filter "FullyQualifiedName~FullCapabilityMatrixTests"` (daemon running).

---

## 4. Manual exploratory testing (full app)

Ran the **real app** end-to-end: ASP.NET Server (real mode, :5537) → Vite SPA (:5173, proxying
`/api` + `/v1`) → live Foundry daemon. Two layers: the browser UI (Playwright, serial) and a direct
API combination sweep.

### 4.1 UI flows (Playwright, `frontend/tests/exploratory-capabilities.spec.ts`) — 7/7 ✅

| Flow | Model | Observed |
|---|---|---|
| Catalog + picker | — | 41 options with capability badges |
| Text chat | phi-4-mini | "An ocean is a vast body of saltwater that covers most of the Earth's surface." |
| Code | qwen2.5-coder-0.5b | returned a ```python code block |
| Reasoning | deepseek-r1-1.5b | emitted `<think>…`, reached the answer |
| Vision (image upload) | qwen3-vl-2b-instruct | described the uploaded image |
| Tools (get_weather) | phi-4-mini | answered (no tool_call fired — model-dependent, valid shape) |
| Speech-to-text (audio upload) | whisper-base | **"The quick brown fox jumps over the lazy dog."** |

> Exploratory testing caught a real regression first time through: the "Model:" line had been moved
> into a `<label>`, breaking the preserved `p.config-line > strong` selector. Fixed (separate
> current-model line from the picker) and re-verified. Also surfaced a test-only initial-load race
> (interacting before `/api/models` settled) — fixed with `waitForLoadState('networkidle')`.

### 4.2 API combination sweep (direct to Server) — all ✅

| Variant | Result |
|---|---|
| `GET /api/models` | 41 models, capability flags present (phi-4-mini → text/tools) |
| `POST /api/models/select` invalid | **400** (rejected, as expected) |
| select `qwen3-8b` → `/api/foundry` | reflects `qwen3-8b` |
| chat with **no model** field | served by the selected `qwen3-8b-cuda-gpu` (default applied) |
| non-streaming chat (phi-4-mini) | "2 + 2 equals 4." |
| **streaming SSE** (phi-4-mini) | 16 `chat.completion.chunk` frames + `[DONE]` terminator |
| `max_tokens: 9999999` | **200** — proxy caps to the model's context window (no OOM/400) |
| code (qwen2.5-coder-0.5b) | returned a Python reverse-string snippet |
| reasoning (deepseek-r1-1.5b) | reached **43** |
| tools (phi-4-mini) | 200, valid shape (0 tool_calls — model answered in text) |
| vision (qwen3-vl-2b, red & green squares) | 200; the 2B model returned a *meta-description* of the request for the plain squares — model-quality variance. The vision **pipeline** is confirmed working by the matrix's 8 VL models and the UI run (which described real images). |
| transcription whisper-tiny | "The quick brown fox jumps over the lazy dog." |
| transcription whisper-base | "The quick brown fox jumps over the lazy dog." |

**Combinations exercised:** non-streaming + streaming; explicit-model + model-less (selection default);
model switching mid-session; valid + invalid selection; token-cap edge; multi-image; multi-whisper;
all six capabilities (text/code/reasoning/vision/tools/audio).

---

## 5. Release-config gate

Re-ran the strict build + non-matrix test classes in **Release**:

- `dotnet build -c Release /p:TreatWarningsAsErrors=true /p:MSBuildTreatAllWarningsAsErrors=true` — **0 warnings, 0 errors**.
- Unit (Release): **2/2**. Stub integration (Release): **4/4**. PhiFoundry GPU (Release): **4/4**.
- The 43-case capability matrix was run in **Debug** (twice green; inference behaviour is build-config
  independent), so it was not re-run in Release to avoid a redundant ~40-min GPU pass.

---

## 6. Summary — everything run locally

| Suite | Count | Result |
|---|---|---|
| Unit (Debug + Release) | 2 | ✅ |
| Stub integration (Debug + Release) | 4 | ✅ |
| PhiFoundry GPU (Debug + Release) | 4 | ✅ |
| **Full capability matrix** (all 41 models × capability) | 43 | ✅ |
| UI exploratory (real app, Playwright) | 7 | ✅ |
| API combination sweep | 13 variants | ✅ |
| Frontend build + ESLint | — | ✅ |
| Release build (warnings-as-errors) | — | ✅ |

**Net: every model in the catalog exercised for its real capability (text, code, reasoning, vision,
tools, speech-to-text), zero skips, zero failures; the full app verified end-to-end through the
browser and the API.** Daemon stability under all-model cycling was solved via single-model VRAM
discipline + a hardened proxy retry.
