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
