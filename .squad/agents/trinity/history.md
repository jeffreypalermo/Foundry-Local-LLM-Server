# Trinity — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript), OpenAI-compatible `/v1/chat/completions` API
- **What it does:** Forwards OpenAI-format chat completion requests to Microsoft Foundry Local. Hosts a React SPA.
- **Frontend:** `frontend/` — React SPA, `npm run lint` + `npm run build` for validation
- **API it talks to:** `/v1/chat/completions` on the ASP.NET Core server
- **User:** Jeffrey Palermo

## Learnings

- **2026-06-02T22:26:49.967-05:00:** `npm ci` currently fails in `frontend/` because `package-lock.json` is out of sync (`@emnapi/core` and `@emnapi/runtime` missing). `npm install --include=dev` followed by `npm run lint` and `npm run build` succeeds.
- **2026-06-02T22:26:49.967-05:00:** Local frontend validation succeeded with Vite proxying to backend (`SERVER_HTTP`). Happy path (`/api/foundry` + `/v1/chat/completions`) worked with stub responses, and error path returned 503 with clear ProblemDetails text (`Foundry Local Unavailable` / `Could not reach Foundry Local`), which matches UI error parsing behavior.
- **2026-06-16T17:08:43Z:** Playwright end-to-end flow verified live with RTX 4060 GPU inference. Launched AppHost (zero console errors), drove Vite frontend with Playwright, clicked "Send Prompt" button, received 2586-char coherent reply (qwen2.5-0.5b GPU model). `p.error` count = 0, `finish_reason=stop`. UI parsed and displayed the reply correctly. nvidia-smi confirmed RTX 4060 performed the inference (VRAM +1537 MiB, util 77–82%, Foundry log "device=GPU").
- **2026-06-16T14:24:38-05:00 — Chat UI refresh (newest-on-top + theme):**
  - **Newest-on-top grouping:** Added a pure `groupExchanges(chat: ChatTurn[]): Exchange[]` helper that folds the flat user-then-assistant `chat[]` into `{ user?, assistant? }` pairs, keeping each user message directly above its matching assistant reply. Render maps the grouped exchanges then `.reverse()` so the most recent exchange sits at the TOP while order WITHIN each pair stays user→assistant. A trailing user turn with no reply yet renders at top with an `article.message.assistant.pending` "Generating response…" state (only while `busy`). State storage was left unchanged — the reversed/grouped view is derived in render.
  - **Protected selectors preserved:** `button[type='submit']` ("Send Prompt"/"Running..."), `article.message.user`, `article.message.assistant p`, `p.error`, `p.config-line > strong` with "Model:" all intact. Pending bubble uses `message assistant pending` so it still matches `.message.assistant`.
  - **New explicit theme tokens (set on `.app-shell`, self-contained — no longer relies on OS `color-scheme`):** `--bg:#0f172a`, `--surface:#1e293b`, `--surface-raised:#273449`, `--border:#3b4a63`, `--text:#f1f5f9`, `--text-muted:#b6c2d6`, `--accent:#6366f1`, `--accent-hover:#818cf8`, `--user-bg:#1d3a5f` / `--user-border:#3b82f6`, `--assistant-bg:#2b2150` / `--assistant-border:#a78bfa`, `--error-bg:#3f1d23` / `--error-border:#f87171` / `--error-text:#fecaca`, `--code-bg:#0b1220`. Body background also pinned to `#0f172a` in `index.css` to avoid a light gutter. User vs assistant distinguished by colored left border + tinted surface (blue vs violet). All body text targets WCAG AA on the dark surfaces.
  - **Lint note:** `npm run build` passes. `npm run lint` reports 2 pre-existing errors ONLY in `frontend/tests/e2e.spec.ts` and `frontend/tests/exploratory.spec.ts` (unused vars) — untouched test files, outside the frontend-SPA scope and the "don't touch tests" constraint. `npx eslint src` is clean (exit 0).
- **2026-06-16T15:35:27-05:00 — Model dropdown + hot-swap UX:**
  - **Dropdown/switch UX:** Added a labeled `<select>` (`label[for=model-select]` "Model:") in a new `div.model-picker`, placed right under the existing `p.config-line` "Model:" line. Fetches `GET /api/models` in a second `useEffect` on mount; options list each model id annotated with ` • loaded` / ` • cached`. The currently-active model is the `<select value>`. Changing the selection calls `switchModel(next)` → `POST /api/models/select` `{ model }`. During the in-flight switch the select (and textarea + Send button) are disabled, `aria-busy` is set, and a `span.switching-status` (role=status, aria-live=polite) shows "Switching model… (unloading + loading on GPU)" with a pulsing dot. On success: update `activeModel`, remap `models[].active`, and patch `config.model` so the protected `p.config-line > strong` reflects the new model. On error: parse ProblemDetails `detail` then `title` (same pattern as chat) into `p.error`; the select value snaps back to the still-`activeModel` automatically (no manual revert needed since we never optimistically changed it).
  - **New state:** `models: ModelInfo[]`, `activeModel: string | null`, `switching: boolean`. New types `ModelInfo`, `ModelsResponse`, `SelectModelResponse`. Chat body now sends `model: activeModel ?? config.model` so subsequent prompts follow the live selection.
  - **Selectors added (non-conflicting):** `div.model-picker`, `select#model-select`, `label[for=model-select]`, `span.switching-status`. All protected Playwright selectors preserved untouched — `button[type='submit']` ("Send Prompt"/"Running..."), `article.message.user`, `article.message.assistant p`, `p.error`, and `p.config-line > strong` with "Model:" remain intact.
  - **Lint/build:** `npm run build` (tsc + vite) passes with 0 errors; `npx eslint src` clean (exit 0). Pre-existing 2 unused-var errors in `frontend/tests/*.spec.ts` left untouched.

- **2026-06-16 — Supported model set finalized: qwen2.5-1.5b (default) + qwen2.5-0.5b**
  - **Per Apoc's live GPU verification on RTX 4060:**
   - **Default model:** `qwen2.5-1.5b` (coherent + proper OpenAI `tool_calls`; ~2.5 GB VRAM)
   - **Fallback:** `qwen2.5-0.5b` (coherent, prose-only; ~1.8 GB VRAM)
  - The model dropdown now sources from `GET /api/models` and switches via `POST /api/models/select`. This authoritative set reflects Apoc's live verification and replaces the previous hardcoded default.
