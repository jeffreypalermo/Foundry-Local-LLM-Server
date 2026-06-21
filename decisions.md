# Decisions

## 2026-06-20 (later)

### Migrate to Foundry Local v1.2.x; Live Capability Matrix; UI Capability Harness

**Date:** 2026-06-20
**Author:** Claude (Opus 4.8)
**Status:** Accepted
**Requested by:** Jeffrey Palermo

#### Context

Two requests: (1) integration tests that exercise the **full capabilities** of **every** model
with no skipping, and (2) a UI that, on model selection, shows a capability-specific panel to
exercise that model (tools, vision, text, etc.). On the then-current Foundry Local **0.8.119**,
vision models failed to load (onnxruntime-genai too old for VL `genai_config.json`) and Whisper
had no invocation path — so "all capabilities" was not achievable. The user then directed us to
the **cross-platform GA Foundry Local v1.2.x** line.

#### Decision

1. **Upgrade to cross-platform Foundry Local v1.2.x (CLI 0.10.0).** Installed via the GitHub
   release `.msix` (`Microsoft.FoundryLocalCLI`), old winget `Microsoft.FoundryLocal` (0.8.x)
   removed. This **unblocked vision** (VL models now load and answer image prompts) and **Whisper**
   (`foundry transcribe` CLI produces transcripts). All capabilities now work on-device.
2. **Adapt to the new CLI surface.** `service`→`server`; URL via `server status -o json`
   `webUrls`; `model load/download` lost `--device/--ttl`. The daemon does not auto-load and
   `model load <alias>` may pick a non-GPU variant, so the proxy resolves alias→**cuda/gpu id**
   (via `/v1/models` heuristic, falling back to `model info -o json` for families like
   deepseek/mistral/whisper) and pre-loads that exact id before forwarding.
3. **Add `POST /v1/audio/transcriptions`** (OpenAI-shaped) that bridges multipart audio to the
   `foundry transcribe` CLI, since the daemon exposes no HTTP audio route.
4. **Live capability matrix** (`FullCapabilityMatrixTests`, `Category=GPU-Required`, run by
   `privatebuild.ps1`): every `AvailableModels` entry loads on the GPU and processes a
   capability-appropriate prompt — text/code/reasoning assertions, vision image description,
   tool-call shape, and Whisper transcription (asserts the known clip transcribes). No `[Skip]`.
5. **UI capability harness:** model picker (capability badges) + per-capability panels (Text,
   Vision image upload, Tools, Speech-to-text upload). `/api/models` returns per-model capability
   flags (`ModelCapabilities.cs`) to drive the panels. Playwright selectors preserved.

#### Rationale

The platform version was the gating constraint; v1.2.x removes it, so the honest, complete
deliverable (real tests + real UI for all capabilities) became possible rather than documenting
blockers.

## 2026-06-20

### Include All GPU-Compatible Foundry Local Models; MAI Stays Cloud-Only

**Date:** 2026-06-20
**Author:** Claude (Opus 4.8)
**Status:** Accepted
**Requested by:** Jeffrey Palermo

#### Context

The host GPU is an **NVIDIA GeForce RTX 5070 Ti (16 GB VRAM)**, Blackwell, CUDA 13.2.
Foundry Local's service registers both `NvTensorRTRTXExecutionProvider` and
`CUDAExecutionProvider`. The request was to investigate which Foundry Local models run on
this hardware — especially Microsoft's newly released **MAI** models — and plan inclusion
of all compatible ones.

Findings:

- **MAI models (MAI-Thinking-1, MAI-Image-2/-Efficient, MAI-Voice-1, MAI-Transcribe-1) are
  cloud-only.** They ship in Microsoft Foundry (Azure), "sold directly by Azure," and on
  Fireworks/Baseten/OpenRouter. They are absent from the Foundry Local on-device catalog
  (`foundry model list` shows none) and have no downloadable ONNX/CUDA weights, so they
  cannot run locally on this GPU.
- **Every GPU variant in the Foundry Local catalog fits in 16 GB** (largest ~9.8 GB:
  `deepseek-r1-14b`). So "all compatible models" is effectively the entire GPU catalog —
  chat, reasoning, coder, vision-language (Qwen-VL / Ministral), and Whisper STT.
- The server already prefers GPU variants in alias resolution, but `AvailableModels` was
  empty and the documented `GET /api/models` + `POST /api/models/select` endpoints were
  never implemented. The default model was also inconsistent (`gemma4` / `phi`), and
  `gemma4` is not even in the catalog.

#### Decision

1. **Local-only; MAI explicitly out of scope.** Documented in README. No Azure cloud route
   added (would be a separate integration).
2. **Curated `AvailableModels`** populated with the full GPU-compatible alias set in
   `appsettings.json`. Growing/shrinking the selectable set is config-only.
3. **Implemented the documented endpoints:** `GET /api/models` (current + available) and
   `POST /api/models/select` (validates against `AvailableModels`, flips the runtime
   default; Foundry loads lazily on next request). `/api/foundry` now reports the live
   selection; model-less chat requests default to it.
4. **Fixed the default-model mismatch** to `phi-4-mini` (a real catalog model) across
   `appsettings.json`, `appsettings.Development.json`, and README.
5. **Pre-downloaded** the missing GPU-compatible models so they are available without a
   first-request stall.

#### Follow-ups (not in this change)

- Frontend model-picker dropdown wired to `/api/models` + `/api/models/select`.
- Optional Azure Foundry passthrough for MAI, if cloud access is ever desired.

## 2026-06-16

### Chat UI Refresh: Newest-on-Top + Explicit Dark Theme

**Date:** 2026-06-16T14:24:38-05:00  
**Author:** Trinity (Frontend Dev)  
**Status:** Accepted  
**Requested by:** Jeffrey Palermo

#### Context

The main web page (`frontend/src/App.tsx` + `App.css`) rendered the chat
transcript oldest-first and used pale pastel message cards over an OS-dependent
background (`color-scheme: light dark`), which produced weak, inconsistent
contrast and forced scrolling to find the latest reply.

#### Decision

1. **Newest exchange on top.** Group the flat `chat[]` (stored user-then-assistant)
   into `{ user, assistant }` exchange pairs with a pure render-time helper
   (`groupExchanges`), render the pairs, then reverse — so the most recent
   exchange is at the top while the User message stays directly above its own
   Assistant reply within each pair. State storage is unchanged; the view is
   derived in render. The in-flight case (latest user prompt, no reply yet)
   shows a dedicated "Generating response…" assistant bubble at the top while
   `busy`.

2. **Explicit, self-contained dark theme.** Define a cohesive token palette on
   `.app-shell` (slate-blue surfaces, indigo accent) instead of depending on the
   OS light/dark scheme. User = blue-tinted card with blue left border;
   Assistant = violet-tinted card with violet left border — clear visual
   distinction with WCAG AA body-text contrast. Buttons, textarea, config lines,
   and the error state get matching states (hover/focus/disabled/busy).

#### Rationale

- Newest-on-top matches user expectation for a running console and removes the
  need to scroll for the latest answer; grouping (not a flat reverse) keeps each
  prompt paired with its own response.
- An explicit theme guarantees readable, attractive contrast regardless of the
  viewer's OS appearance setting.

#### Constraints Respected

- Frontend-only; no backend/Aspire/test changes.
- All Playwright-protected selectors preserved: `button[type='submit']`
  ("Send Prompt"/"Running..."), `article.message.user`,
  `article.message.assistant p`, `p.error`, `p.config-line > strong` ("Model:").
- TypeScript strict; no implicit any. `npm run build` passes; `npx eslint src`
  clean. (Two pre-existing lint errors live only in `frontend/tests/*` spec
  files and are out of scope.)
