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
