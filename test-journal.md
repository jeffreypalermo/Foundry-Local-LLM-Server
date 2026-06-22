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

---

## 7. Per-model demo harness (≥5 scenarios per capability)

Each capability panel now offers scenario chips (inspired by the Microsoft Foundry Local C# samples:
chat-assistant, document-summarizer, tool-calling, voice-to-text, vision):

| Capability | Scenarios |
|---|---|
| Text (6) | Q&A, summarize, translate, classify, extract-JSON, rewrite |
| Code (5) | generate, explain, find-bug, write-tests, port-language |
| Reasoning (5) | word-problem, logic, plan, compare, estimate |
| Vision (6) | describe, OCR, shape/color, count, visual-Q&A, upload |
| Tools (5) | get_weather, calculate, two-tools, forced tool_choice, full tool loop |
| Audio (5) | pangram clip, numbers clip, two language-hint clips, upload |

Full-system Playwright suite `frontend/tests/demo-scenarios.spec.ts`:
- **Structure (7):** chips render per capability; chips set prompt/preview/clip; empty-input disables Send.
- **Behavior (11, live):** representative scenario per capability + OCR + upload + multi-turn tool loop
  + model-switch-clears-conversation, all run end-to-end against real inference.

## 8. Exploratory testing — bug → failing test → fix → pass

Sustained creative exploration of the running app (UI + API). Each bug was first captured by a test
that failed, then fixed until it passed.

| # | Bug found | Exposing test | Fix |
|---|---|---|---|
| 1 | `ApplyContextBounds` was dead code — no prompt-trim / max_tokens cap (VRAM-OOM risk) | `ContextBoundsTests` (2) | Wired into handler + `X-Context-*` headers |
| 2 | Malformed JSON body → **500** | `ExploratoryApiTests.MalformedJsonBody…` | Catch parse error → **400** |
| 3 | Switching same-kind models left the previous model's transcript on screen | demo-scenarios "switching models clears…" | `onSelectModel` clears conversation |
| 4 | `/api/models/select` malformed JSON → **500** | `ExploratoryApiTests.SelectModel_MalformedJson…` | Catch parse error → **400** |
| 5 | Non-array `messages` → **500** (AsArray threw) | `ExploratoryApiTests.…MessagesNotArray…` | Validate up front → **400** |
| 6 | Send buttons clickable on empty input (silent no-op) | demo-scenarios "Send Prompt is disabled…" | Disable when prompt empty |

Probes that found **no** bug (kept as coverage / verified good behavior): multi-turn `role:tool`
loop (daemon accepts it); forced `tool_choice`; vision model on plain-text chat; **live**
context-trimming (60-msg conversation → 50 dropped, max_tokens→2048, input→~7.5k); non-audio upload
→ clean **502**; missing audio file → **400**; capability classification correct for all 41 models.

---

## 9. Exhaustive demo capture — every scenario for every model

`tests/demo-capture.spec.ts` drives the real SPA via Playwright across the full matrix: for each
model, select it → for each applicable capability → click each scenario chip → run → validate the
output → screenshot. Resumable per-scenario (KINDS/MODELS filters), screenshots + `results.json`
written to `docs/demo-captures/`. The C# `tools/DemoDocBuilder` compiles `docs/DEMO.md` from them.

- **Result: 41/41 models · 291 scenario runs · 291 produced output · 0 errors · 291 screenshots.**
- Bug found & fixed during capture: single-capability models render no mode-tabs → the harness's
  unconditional tab-click hung; made the tab-click conditional on the tab existing.
- The two largest **thinking** models (`deepseek-r1-14b`, `qwen3.5-9b`) generate full 2048-token
  reasoning per scenario, taking 12–15+ min each near the 16 GB VRAM limit — impractical to capture
  all of at full length. Their longest scenarios were captured with the server's `MaxResponseTokens`
  temporarily set to 512 (shorter but complete demonstrations); the server was restored to 2048 after.
- See `docs/DEMO.md` for the full per-model walkthrough with embedded screenshots.

---

## 10. Multi-client proof — Blazor WASM client

Built a second client app (`FoundryLocalLlmServer.BlazorClient`, Blazor WebAssembly) that mirrors the
React SPA feature-for-feature (model picker, capability tabs, all scenario chips incl. multi-turn,
vision/tools/audio panels). It points at the **same** Foundry Local proxy server (`:5537`) the React
app uses; the Server now enables CORS so the cross-origin browser client can call it.

- Server CORS verified: `OPTIONS /api/models` with a foreign `Origin` → `Access-Control-Allow-Origin: *`.
- `frontend/tests/blazor-client.spec.ts` (Playwright against `:5180`) — **4/4 pass**:
  catalog loads cross-origin from the shared server; text chat round-trips; multi-turn conversation
  retains context (recalls the name/pet); speech-to-text returns the transcript.
- Confirms **two distinct client apps (React + Blazor) against one Foundry Local server process**.

---

## 11. Fresh exploratory round — request-validation hardening (two clients + server)

A second adversarial pass over the proxy's request-handling surface, both clients (React `:5173`,
Blazor `:5180`) live against the one server (`:5537`). Every malformed shape that produced a 500 (or
a confusing 404/200) was captured as a failing `ExploratoryApiTests` case first, then fixed, then
re-run green — and finally re-verified against the **live** server.

| # | Bug found | Test (failed → passes) | Fix |
|---|-----------|------------------------|-----|
| 7 | `messages:[]` / missing `messages` → forwarded to daemon (404 "model not found") or answered by stub (200) | `…EmptyOrMissingMessages…` (×2) | Validate non-empty array → **400** |
| 8 | `POST /api/models/select` with non-string `model` (e.g. `123`) → **500** (`GetValue<string>` threw) | `…SelectModel_NonStringModel…` | Safe `TryGetValue<string>` → **400** |
| 9 | Chat with non-string `model` → **500** | `…ChatCompletions_NonStringModel_DoesNotCrash` | Falls back to selected default (no throw) |
| 10 | `messages:[123]` (non-object element) → **500** (`m["role"]` indexed a `JsonValue`) | `…NonObjectMessageElement…` | Per-element validation → **400** |
| 11 | `messages:[{"role":5,…}]` (non-string role) → **500** | `…NonStringRole…` | Per-element validation → **400** |
| 12 | `content:123` (numeric) → **500** (`MessageText`/`ExtractLatestUserPrompt` called `GetValue<string>`) | `…NumericContent_DoesNotCrash` | Helpers tolerate non-string content/part-text → no throw |

`OpenAiChatHelpers.ExtractLatestUserPrompt` and `MessageText` were made defensive throughout
(`TryGetValue`, `JsonArray`/`JsonObject` pattern checks) so no malformed node can ever crash the
bounding/prompt path. Result: **16/16 non-GPU + 2 unit green.**

Live re-verification against `:5537` (all as expected, fast validation paths short-circuit before
inference): select unknown model → **400**; select non-string → **400**; select valid → **200**;
chat empty messages → **400**; `messages:[123]` → **400**; numeric role → **400**; transcriptions
no file → **400**; `GET` on the chat endpoint → **405**. Real chat (`qwen3-0.6b`) → **200** with
content (no regression); numeric `content` live → **200** (defensive handling held, no 500).

Probes that found **no** bug (kept as verified-good): `max_tokens` as a string or ≤ 0 (already
guarded by `TryGetValue<int>` + `> 0` → falls back to default); unknown `model` on select (already a
clean 400 with the available list); malformed-JSON body on both endpoints (already 400).

---

## 12. Fresh exploratory round — inference-path & security hardening

A third adversarial pass, this time on the paths that touch inference (streaming, context bounds) and
the one place user input reaches a subprocess (transcription). Same loop: failing test → fix → green
→ live re-verify.

| # | Bug found | Test (failed → passes) | Fix |
|---|-----------|------------------------|-----|
| 13 | A non-boolean `stream` (`"yes"`, `1`) → **500** (`GetValue<bool>()` threw) at all three isStreaming sites; even once guarded, the invalid value was forwarded to the daemon → **404** | `…NonBooleanStream_DoesNotCrash` (`"yes"`/`1`/`null`) | `WantsStreaming()` helper (bool-only, never throws) + **normalize** `stream` to a real boolean in the payload before forwarding |
| 14 | `max_tokens` as a string/float would throw in `GetValue<int>()` if `MaxResponseTokens` bounding were disabled | covered by the same suite | `PositiveInt()` helper (safe positive-int read) at both `max_tokens` sites |
| 15 | **(security)** `/v1/audio/transcriptions` interpolated the user-supplied `model` form field into the `foundry` CLI argument string **unvalidated** — argument injection (e.g. `whisper-base -f C:\victim.wav` injects an extra `-f`). `UseShellExecute=false` rules out a *shell*, but not extra-arg injection into `foundry.exe`. `language` was already regex-validated; `model` was not. | `Transcription_ModelNotInAllowlist_Returns400` | Allow-list `model` against `AvailableModels` before it reaches the process (mirrors `/api/models/select`) |

Result: **20/20 non-GPU + 2 unit green.**

Live re-verification against `:5537`: `stream:"yes"` → **200 application/json** (normalized to
non-streaming); `stream:true` → **200 text/event-stream** (real SSE intact); transcription with an
injected `-f` argument → **400** (rejected before any process spawn); legit `whisper-base` clip →
**200** `{"text":"The quick brown fox jumps over the lazy dog.",…}` (allow-list doesn't break the
happy path).

Context-bounds verified **end-to-end** live (the helpers I hardened feed `ApplyContextBounds`): a
62-message conversation → **40 dropped**, input trimmed to ~7.8k under the 8192 budget, leading
system message preserved, **200**; a 40-message / 7.5k conversation → **0 dropped** (correctly fits).
`X-Context-Dropped-Messages` / `-Max-Tokens` / `-Input-Tokens` all emit correctly.

Probes that found **no** bug (verified-good): `content` as an object or vision parts array (helpers
return empty / extract text, never throw); `stream:null` (treated as non-streaming); concurrent
two-client requests (serialized by `_foundryRequestGate`); `foundry` invoked with
`UseShellExecute=false` (no shell — only the arg-injection vector above, now closed).

---

## 13. Two-client end-to-end validation against the hardened server

After rounds §11–§12, re-ran both client suites against the live hardened Server (`:5537`) to confirm
the added 400s / stream-normalization / allow-list didn't regress real usage (the clients only send
well-formed requests).

- **Blazor WASM (`:5180`) — 6/6 pass** (2.4 min): catalog cross-origin load, text chat, multi-turn
  context, tools panel (C# `tool_calls` serialization), full tool loop (1.5 min), speech-to-text.
- **React SPA (`:5173`) — 22/22 after a flake fix** (first run 21/22, 13 min): all structure +
  multi-turn + behavior scenarios (text/code/reasoning/vision/tools/audio) green.

**Flaky test found & fixed (not a server bug):** `vision: upload your own` timed out at the 6-min
cap. Reproduced the *exact* request directly via the API → **200 in 29 s** with `max_tokens=64`
(path healthy). Root cause: `green-circle.png` is a trivial abstract image, so the tiny
`qwen3-vl-2b-instruct` (2B) **rambles to the full 2048-token cap** with no early stop — legitimately
~5.5–6.5 min on this GPU (the built-in describe/OCR images stop naturally in ~30 s). That worst case
straddles the 6-min timeout → flaky. Fix: a vision-specific `VLONG = 12 min` timeout for the three
full-length vision-generation tests (`describe` / `OCR` / `upload`). Re-run of the failing test →
**pass (5.5 min)**.

Confirms the multi-client contract — **two distinct client apps against one hardened Foundry Local
server** — still holds after all the request-handling hardening.

---

## 14. Multi-round testing pass — run rounds until one is clean

Directive: run up to 10 complete testing rounds; if a round finds & fixes a bug, start a fresh round;
stop only when a complete round finds nothing. **Outcome: 6 rounds — 3 found bugs (all fixed
test-first), then 3 consecutive clean rounds.**

| Round | Dimension | Finding → fix |
|------:|-----------|---------------|
| 1 | Full GPU matrix + cancellation | **2 bugs.** `AppHost_SendPrompt` Playwright test timed out at 120 s on cold-start full-length generation → widened to 240 s. Client disconnect made the request token fire and was logged as *"Unhandled exception"* + took the 500 path → now caught as `OperationCanceledException` → Information log, early return. (Matrix models tested before stopping the run all passed.) |
| 2 | HTTP/JSON body shape | **1 bug.** A non-object body (`[1,2,3]`, `42`, `"hi"`, `true`) made `["messages"]` / `["model"]` index a `JsonArray`/`JsonValue` → 500. Now require a JSON object up front → 400 on both chat + select. |
| 3 | Transcription / multipart | **1 bug.** `ReadFormAsync` was unguarded; a malformed/truncated multipart body threw `InvalidDataException`/`IOException` → 500. Now → 400. |
| 4 | Content/value shapes | **Clean.** 10 new probes (vision `image_url` parts object + bare-string, 1 MB single message → head-truncation, `content:null`, 2000-message trim, `max_tokens` 999999999/-5/0, unicode+control chars, system-only). All non-500. |
| 5 | Concurrency + cancellation (live) | **Clean.** 5 concurrent same-model → all 200 (gate serializes); 2 concurrent different-model → both 200 (exclusive load, no OOM); client abort mid-request → post-cancel request 200 in 18 s (**gate released**, no deadlock) and cancellation logged as info (1), not unhandled error (0) — the §1 fix verified live. |
| 6 | Comprehensive confirmation | **Clean.** Full solution build (0 warn/0 err), unit 2/2, non-GPU integration **39/39**, frontend `npm run build` ✓, live smoke text/tools/audio all 200 (Whisper returns the correct transcript). |

Each fix landed test-first (failing → green) and was re-verified live. The non-GPU regression suite
grew from 11 → **39** tests over the pass. Stopped per the rule after three consecutive clean rounds.
