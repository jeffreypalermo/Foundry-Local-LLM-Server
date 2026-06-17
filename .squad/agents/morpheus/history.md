# Project Context

- **Owner:** Jeffrey Palermo
- **Project:** Foundry Local for a demo
- **Stack:** .NET 10 backend, ASP.NET Core, React + TypeScript, npm, Foundry Local, Ollama
- **Created:** 2026-06-02T22:26:49.967-05:00

## Learnings

### 2026-06-02T22:51:23.154-05:00 — Team Model Preferences: Local Gemma4 via Ollama

- Established member-level model preferences in `.squad/config.json` to route lightweight planning/triage/monitoring work (Morpheus, Scribe, Ralph) to local gemma4 via Ollama, while keeping critical coding paths (Neo, Tank, Trinity) on standard/premium tier models.
- Extended squad config schema with `models` registry and `member_preferences` routing map. No runtime code changes required; preferences are durable and discoverable for future tooling.
- Decision documented in `.squad/decisions/inbox/morpheus-gemma4-ollama.md` with full rationale for tier assignment and model selection criteria.
- All member charters updated with model preference sections that reference the decision and config.json entry points. Existing Squad behavior unchanged; preferences enable future coordination agent optimization.

### 2026-06-02T22:26:49.967-05:00 — Release-readiness gate outcome

- The solution builds successfully (`dotnet restore`, `dotnet build -c Release`), but full solution tests are not release-green without local Foundry because `PlaywrightIntegrationTests` currently fails instead of skipping.
- Backend API smoke checks pass in stub mode (`/api/foundry`, `/v1/models`, `/v1/chat/completions`), while direct server hosting can still miss UI readiness when `wwwroot` is absent.
- For demo sign-off, require both: environment-gated live tests and deterministic frontend serving behavior in the local run path.

### 2026-06-02T22:26:49.967-05:00 — Post-fix validation state

- Integration suite now passes in non-Foundry environments with explicit skips (`7 total: 4 passed, 3 skipped`) after environment-gating updates.
- Local end-to-end dev topology is validated: backend (`FoundryLocal__UseStubResponses=true`) + frontend Vite proxy returns healthy UI and chat responses.
- Final demo readiness still depends on live Foundry-path evidence and packaged static asset (`wwwroot`) verification.
