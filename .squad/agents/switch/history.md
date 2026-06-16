# Switch — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, React (TypeScript)
- **Test projects:**
  - `FoundryLocalLlmServer.UnitTests` — unit tests
  - `FoundryLocalLlmServer.IntegrationTests` — integration tests (`UseStubResponses=true` for CI, no GPU needed)
- **Test command:** `dotnet test ./FoundryLocalLlmServer.sln`
- **User:** Jeffrey Palermo
- **Recent Work:** Proxy resiliency improvements (503 error handling) and opencode local-provider setup (commit 396b3ee)

## Test Suite Composition

**Integration Tests (4 files, 29 total):**
1. **OpenAiCompatibilityTests** (2 tests) — Uses `ServerFactory` with `UseStubResponses=true`. No GPU needed. ✅ **PASS**
2. **AspireGenerationIntegrationTests** (25 tests) — `SkippableTheory` with GPU dependency. Auto-skips if Foundry Local not running. ✅ Correct behavior.
3. **OpenCodeIntegrationTests** (1 test) — `SkippableFact` with GPU dependency. Auto-skips if Foundry Local not running. ✅ Correct behavior.
4. **PlaywrightIntegrationTests** (1 test) — Launches Chromium browser; requires Foundry Local + wwwroot/index.html built. ⚠️ Hangs in CI without proper skip.

## Learnings

**2026-05-12: Automated Test Resumption — ROOT CAUSE FOUND**
- Unit tests (2/2): **PASS** ✅ (20ms)
- OpenAI compatibility integration tests (2/2): **PASS** ✅ (291ms, uses stub responses)
- Full suite (`dotnet test`): **FAILS or HANGS** ❌
- **ROOT CAUSE:** `FoundryServiceHelper.DiscoverUrlAsync()` line 374-407 calls `foundry service start` (line 380), which:
  - Attempts to START Foundry Local via CLI if `foundry` command is available
  - On CI without GPU, this hangs or fails because there's no actual GPU service to connect to
  - Tests don't skip; they fail/hang after service startup attempt fails or times out
- **Design Issue:** Skippable tests should not trigger service startup. Need environment flag or discovery-only mode.
- **Proxy 503 error handling** (commit 396b3ee): No integration test coverage yet — feature added but untested
- **opencode local provider** setup: No integration test coverage yet — feature added but untested
- **Recommendation:** Add environment variable to disable auto-start in FoundryServiceHelper, or create CI-safe test configuration

**2026-06-03: Phi 4 mini Foundry Local GPU integration tests (primary path)**
- Added `PhiFoundryGpuIntegrationTests.cs` covering the Foundry-only GPU path for `phi-4-mini`.
- Tests: non-streaming completion, streaming SSE, OpenAI-schema validation, and an error case for an unavailable model.
- All tagged `[Trait("Category", "GPU-Required")]` and use `SkippableFact` so CI (stub-only, no GPU) can filter or self-skip.
- **Key design choice:** new `PhiFoundryServerFactory` sets `FoundryLocal:UseStubResponses=false` AND `OllamaFallback:Enabled=false`. Disabling Ollama is the only reliable way to prove the error case never silently falls back — with fallback on, a Foundry failure is masked by an Ollama completion.
- **GPU assertion:** `FoundryServiceHelper.IsGpuModelAvailableAsync` is used as a guard so a GPU test can't accidentally pass on a CPU/NPU variant.
- **Proxy behavior confirmed (Program.cs):** with fallback disabled, a non-2xx Foundry response returns HTTP 503 "Foundry Local Unavailable"; the error case asserts non-success + body is not a `chat.completion`.
- **Coverage gap remaining:** these tests are human-attended (require GPU + Foundry running). No GPU-free way exists to validate real CUDA inference. The `foundry service start` auto-start in `FoundryServiceHelper` (noted 2026-05-12) still applies — these tests inherit that and rely on Skip when discovery fails.
- Build verified: `dotnet build` on the IntegrationTests project succeeds (0 warnings, 0 errors).

**2026-06-16: Frontend Chat UI Refresh — Newest-on-Top + Dark Theme**
- Trinity redesigned `frontend/src/App.tsx`, `App.css`, and `index.css` for improved UX.
- **Chat ordering:** Newest exchange now on top via `groupExchanges` helper; user stays paired with its own assistant reply. In-flight pending bubble shows at top while busy.
- **Theme:** Explicit, self-contained dark palette (slate-blue surfaces, indigo accent) replaces OS-dependent pastels. User = blue-tinted with blue border; Assistant = violet-tinted with violet border. WCAG AA contrast.
- **Button/input styling:** Consistent hover/focus/disabled/busy states throughout.
- **Playwright selectors:** All protected: `button[type='submit']` (Send/Running), `article.message.user/assistant`, `p.error`, `p.config-line > strong`.
- Build: `npm run build` ✅, `npx eslint src` ✅ (2 pre-existing errors in spec files only, out of scope).
- ⚠️ **Note for future ordering assertions:** Newest exchange is now `.article.message.assistant p` `.First == newest reply, not oldest.
