# Team Decisions

## 2026-06-02: Team Model Preferences: Local Gemma4 via Ollama

**Date:** 2026-06-02  
**Author:** Morpheus  
**Status:** Accepted  

### Context

The team uses a range of models for different work types. Planning, triage, logging, and monitoring tasks are typically lightweight and I/O-bound rather than token-intensive. The project now runs Ollama locally, making a lightweight model (gemma4) available for these non-critical-path tasks.

### Decision

Establish member-level model preferences in `.squad/config.json`:

1. **Planning/Triage/Monitoring roles** (Morpheus, Scribe, Ralph) use **gemma4 via Ollama** by default.
   - Rationale: Lower latency, acceptable quality for coordination work, preserves premium models for critical coding/architecture tasks.

2. **Backend (Tank)** remains on **standard/auto tier** by default.
   - Note: Can use gemma4 for side diagnostics or drafts when drafting approaches.
   - Rationale: ASP.NET Core implementation and API contract work require strong code generation.

3. **Lead (Neo)** remains on **premium tier (auto)** for architecture decisions.
   - Note: Can use gemma4 for side diagnostics.
   - Rationale: Architectural correctness has high stakes.

4. **Frontend (Trinity)** remains on **standard tier (auto)** for component work.
   - Note: Can use gemma4 for side drafts.
   - Rationale: UI implementation must align with backend contracts.

### Implementation

- Config schema extended at `.squad/config.json` with `models` (registry) and `member_preferences` (routing).
- Each member's charter.md updated with model preference section.
- No runtime code changes required — preferences documented for future tooling or Copilot agent configuration.
- Existing Squad behavior unchanged.

### Consequences

- Planning and coordination work will run lighter, freeing premium model capacity for critical code paths.
- Team decisions are now durable in version control (`.squad/config.json`).
- Future coordination tooling can read config to auto-route agents.

---

## 2026-06-02T22:26:49.967-05:00: Release readiness blocked for local demo sign-off

**Author:** Morpheus

### Decision

Do not grant final demo release sign-off yet. Grant only a **conditional local-dev pass**: build/test/stub-runtime are healthy, but live Foundry demo readiness is unproven in this environment.

### Why

- `dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` now pass (7 total: 4 passed, 3 skipped due environment prerequisites).
- Local runtime smoke succeeds in dev topology (backend on `5058` with `FoundryLocal__UseStubResponses=true`, frontend Vite on `5174`): `/`, `/api/foundry`, and `/v1/chat/completions` all return expected responses.
- `foundry` CLI is not available in this machine context, so all live Foundry-dependent coverage is skipped and true demo-path inference is not verified.
- Direct backend hosting still reports missing `wwwroot`; without the frontend dev server or published static assets, `/` is not demo-ready.

### Required follow-up owners

- **Tank:** Run and capture a true live-mode validation pass in an environment with Foundry Local available (no stub fallback).
- **Trinity:** Provide/verify deterministic static asset publish flow for backend-only hosting (`wwwroot`) used in demo mode.
- **Neo:** Keep README acceptance criteria explicit about two modes: dev topology (Vite proxy) vs packaged/demo topology (server serves static assets).

---

## 2026-06-02T22:26:49.967-05:00: Gate live integration tests by environment

**By:** Neo

### Decision

OpenAI compatibility integration tests run with `FoundryLocal:UseStubResponses=true`. Live integration tests that depend on Foundry Local/opencode/Playwright use SkippableFact and skip when dependencies are missing.

### Why

Keeps backend validation dependable in local/CI environments without GPU or external CLIs while preserving live end-to-end coverage when prerequisites are present.

---

## 2026-06-02T22:26:49.967-05:00: Local smoke path validation via frontend dev server

**By:** Tank

### Decision

For local functional smoke checks, validate the UI through the frontend dev server (`npm run dev`) with `SERVER_HTTP` pointing to the backend server, not by hitting `FoundryLocalLlmServer.Server` root directly.

### Why

`FoundryLocalLlmServer.Server` returns `404` at `/` unless `wwwroot` is published (AppHost publish path), while API routes (`/api/*`, `/v1/*`) are available directly. Frontend runtime checks are therefore reliable via Vite + proxy, and backend API checks remain direct.

---

## 2026-06-03: GPU-Required Phi/Foundry integration tests

**Author:** Switch (Tester)  
**Status:** For team review

### What I did

Added `FoundryLocalLlmServer.IntegrationTests/PhiFoundryGpuIntegrationTests.cs` validating that
**Phi 4 mini runs through Foundry Local on the current GPU** (primary, non-fallback path):

- Non-streaming completion (OpenAI envelope, non-empty content, `model` contains `phi`)
- Streaming completion (SSE `text/event-stream`, `chat.completion.chunk` frames, `[DONE]`, assembled content)
- OpenAI-schema validation (object/id/created/model/choices/message.role/finish_reason)
- Error case: unavailable model errors out **without** Ollama fallback

All tests are `[Trait("Category", "GPU-Required")]` + `SkippableFact`.

### Decisions the team should be aware of

1. **Ollama fallback is disabled in the test factory** (`OllamaFallback:Enabled=false`,
   `FoundryLocal:UseStubResponses=false`). This is required to prove the Foundry-only path and
   the "no silent fallback" guarantee. If anyone changes fallback wiring, keep a config path that
   lets a test force the Foundry-only route.

2. **GPU variant guard.** Tests use `FoundryServiceHelper.IsGpuModelAvailableAsync` so they refuse
   to "pass" on a CPU/NPU variant. A GPU test passing on CPU would be a false green.

3. **CI must exclude this category.** Run GPU-free CI with
   `dotnet test --filter "Category!=GPU-Required"` (tests also self-skip when Foundry is unreachable).

### Recommended follow-up tests / gaps

- **No GPU-free proof of real CUDA inference exists.** Consider a lightweight device-report assertion
  (e.g., surface the loaded variant id `phi-4-...-cuda-gpu` in test output) — already partially done
  via the GPU guard, but we cannot assert utilization without a GPU present.
- **503 vs 500 contract.** The unavailable-model case currently accepts 503/500/404/400. Recommend
  Tank pin down a single documented status for "Foundry model not available, fallback disabled" so the
  test can assert exactly one code.
- **Concurrency.** Program.cs serializes Foundry requests via a gate (`_foundryRequestGate`) because
  concurrent streaming crashes Foundry. A future GPU test should assert two overlapping streaming
  requests still both complete (serialized), not crash.
- **`foundry service start` auto-start** in `FoundryServiceHelper.DiscoverUrlAsync` can hang on CI
  (see Switch history 2026-05-12). A discovery-only / no-auto-start mode would make these skips cleaner.
