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
