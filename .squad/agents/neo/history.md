# Neo — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript), OpenAI-compatible `/v1/chat/completions` API
- **What it does:** Forwards OpenAI-format chat completion requests to Microsoft Foundry Local (local LLM runtime). Hosts a React SPA alongside the API.
- **Key model:** `phi-4` on RTX 5090-class hardware (port 5273 by default)
- **Solution layout:**
  - `FoundryLocalLlmServer.AppHost` — Aspire orchestrator
  - `FoundryLocalLlmServer.Server` — ASP.NET Core API + SPA hosting
  - `frontend/` — React SPA
  - `FoundryLocalLlmServer.UnitTests` — unit tests
  - `FoundryLocalLlmServer.IntegrationTests` — integration tests (UseStubResponses=true for CI)
- **User:** Jeffrey Palermo

## Learnings

- **2026-06-02T22:26:49.967-05:00:** Built and validated backend (`dotnet build`, `dotnet test`) and confirmed core server endpoints locally on `http://localhost:5537` (`/health`, `/api/foundry`, `/v1/models`, `/v1/chat/completions`). Added environment-gated integration behavior: OpenAI compatibility tests now run in stub mode, while live Foundry/opencode/Playwright tests skip when prerequisites are absent.
- **2026-06-02T22:56:05.612-05:00:** Squad model routing is now explicit and role-based in `.squad/config.json`: Neo/Trinity/Tank/Switch use `opus48` (`claude-opus-4.8`), while Apoc/Scribe/Ralph use `gemma4_ollama` (`ollama/gemma4`), and charters must reference these exact keys to avoid ambiguity.
