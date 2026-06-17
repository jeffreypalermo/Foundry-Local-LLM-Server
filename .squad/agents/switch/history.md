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

**2026-06-16: Parameterized Per-Model Integration Tests (model-switch API + coherence + model-aware tool calling)**
- **Files added:**
  - `FoundryLocalLlmServer.IntegrationTests/SupportedModelData.cs` — single source of truth for the test matrix.
  - `FoundryLocalLlmServer.IntegrationTests/ParameterizedModelIntegrationTests.cs` — the parameterized tests + two `WebApplicationFactory` factories (`StubModelServerFactory`, `GpuModelServerFactory`).
  - Added package `Xunit.SkippableFact 1.5.61` to the IntegrationTests csproj (no Skip mechanism existed before — prior "GPU" tests used `Assert.*` and therefore FAILED rather than skipped without a GPU).
- **Model set sourcing (auto-adapting):** `SupportedModelData.AvailableModels` reads `FoundryLocal:AvailableModels` from the server's committed `appsettings.json`, located by walking up from the test bin dir to the `.sln` root. Adding/removing a model in appsettings automatically expands/contracts every `[Theory]`. Static fallback `["qwen2.5-1.5b","qwen2.5-0.5b"]` only if appsettings can't be read.
- **Capability matrix (data-driven, not scattered ifs):** tool-calling capability is NOT config — it's Apoc's empirical RTX-4060 truth. Encoded as `ToolCallingCapableAliases = { "qwen2.5-1.5b" }`. `SupportedModels()` MemberData yields `(string alias, bool supportsToolCalls)`; the tool-calling test branches on that flag: capable → assert proper OpenAI `tool_calls` (name `get_weather` + JSON args + `finish_reason=tool_calls`); prose-only → assert NO tool_calls but non-empty plain text.
- **CI vs GPU split:**
  - Structural tests (GET `/api/models` listing, POST `/api/models/select` stub success shape per model, unknown-model 400) run under `UseStubResponses=true`, NO category trait → part of default CI run, GPU-free.
  - GPU tests (real load device=GPU/loaded=true, real inference coherence, real tool-calling output) are `[SkippableTheory]` + `[Trait("Category","GPU-Required")]`. Skip guard `RequireGpuModelAsync` uses `FoundryServiceHelper.GetServiceUrlAsync/IsRunningAsync/EnsureGpuModelReadyAsync` and calls `Skip.If/IfNot` so they SKIP (not fail) with no GPU.
- **Verification:** `dotnet build` 0 errors/0 warnings. CI path `dotnet test --filter "Category!=GPU-Required&Category!=Integration"` → Unit 2/2, Integration 8/8 GREEN. GPU-only run → 6 SKIPPED, 0 failed.
- **IMPORTANT CI-filter learning:** The literal `--filter "Category!=GPU-Required"` alone is NOT green — the pre-existing `[Trait("Category","Integration")]` tests (Playwright, OpenCode, AspireGeneration) use `Assert.NotNull` on the Foundry URL and FAIL (not skip) without a GPU. The true green CI filter (per Tank) is `"Category!=GPU-Required&Category!=Integration"`. My new structural tests are intentionally untagged so they DO run in CI; my GPU tests skip properly.

**2026-06-16: Closing the CI-RED gap — precondition-gated Integration tests now SKIP, opencode test aligned to supported set**
- **Problem:** The documented CI command `dotnet test ./FoundryLocalLlmServer.sln --filter "Category!=GPU-Required"` was RED — 3 pre-existing `[Trait("Category","Integration")]` tests FAILED (not skipped) because they hard-asserted external preconditions (`Assert.NotNull(foundryUrl)`, `Assert.True(IsRunningAsync())`, opencode on PATH, running Aspire app). `AspireGenerationIntegrationTests` was also stale: its `Rtx5090CompatibleModels` MemberData hard-coded the now-excluded `phi-4-mini`.
- **Tests made skippable (Assert→Skip + `[Trait("Category","GPU-Required")]` added):**
  - `AspireGenerationIntegrationTests.OpenCode_GeneratesCodeResponse` — converted `[Theory]`→`[SkippableTheory]`.
  - `OpenCodeIntegrationTests.OpenCodeCli_RunsAgainstServer_ReturnsValidResponse` — `[Fact]`→`[SkippableFact]`.
  - `PlaywrightIntegrationTests.AppHost_SendPrompt_ReturnsAssistantResponse_UsingGemma4Gpu` — `[Fact]`→`[SkippableFact]`; the wwwroot-missing and Playwright-install failures now `Skip` too.
  - **Bonus (same anti-pattern, found during GPU-Required run):** `PhiFoundryGpuIntegrationTests` (4 tests) were `[Fact]` + `Assert.Fail` in `RequireGpuFoundryAsync` despite the doc-comment claiming SkippableFact — they FAILED when phi-4-mini was unavailable. Converted all 4 to `[SkippableFact]` and swapped `Assert.Fail`→`Skip.If/IfNot`. They now skip cleanly (phi-4-mini is permanently excluded as degenerate per apoc-supported-models.md).
- **Precondition-skip pattern used (consistent with `ParameterizedModelIntegrationTests.RequireGpuModelAsync`):**
  `Skip.If(foundryUrl is null, ...)` → `Skip.IfNot(await IsRunningAsync(), ...)` → `Skip.IfNot(await IsCommandAvailableAsync("opencode"), ...)` → `Skip.IfNot(await EnsureGpuModelReadyAsync(alias), ...)`. Real assertions downstream are UNCHANGED so they still provide full coverage on a GPU dev box.
- **AspireGeneration model-set alignment (kept, not deleted):** Removed the stale `Rtx5090CompatibleModels` (`phi-4-mini`) MemberData; replaced with `SupportedGenerationModels => SupportedModelData.SupportedModels()`, which reads `FoundryLocal:AvailableModels` from appsettings (now `qwen2.5-1.5b`, `qwen2.5-0.5b`) — auto-adapting. Kept the file (not deleted) because it covers the DISTINCT end-to-end **opencode-CLI-through-the-proxy-exe** path, whereas `ParameterizedModelIntegrationTests` covers the in-process `/v1/chat/completions` + tool-calling path — no duplication. `OpenCodeIntegrationTests` now uses `SupportedModelData.DefaultModel` instead of hard-coded `phi-4-mini`.
- **Filter behaviour:** With GPU-Required tags added, the documented `--filter "Category!=GPU-Required"` now EXCLUDES these 3 (so 0 FAILED). When the GPU-Required lane IS run on a GPU-free box they SKIP (not fail).
- **Final per-filter counts (build: 0 errors / 0 warnings):**
  - `--filter "Category!=GPU-Required"` → Unit **2 passed / 0 failed / 0 skipped**, Integration **8 passed / 0 failed / 0 skipped**. GREEN.
  - `--filter "Category!=GPU-Required&Category!=Integration"` → Unit **2 passed**, Integration **8 passed**, 0 failed. GREEN (pure fast lane).
  - `--filter "Category=GPU-Required"` → Integration **14 SKIPPED / 0 failed / 0 passed**. No FAILED state anywhere.


- Trinity redesigned `frontend/src/App.tsx`, `App.css`, and `index.css` for improved UX.
- **Chat ordering:** Newest exchange now on top via `groupExchanges` helper; user stays paired with its own assistant reply. In-flight pending bubble shows at top while busy.
- **Theme:** Explicit, self-contained dark palette (slate-blue surfaces, indigo accent) replaces OS-dependent pastels. User = blue-tinted with blue border; Assistant = violet-tinted with violet border. WCAG AA contrast.
- **Button/input styling:** Consistent hover/focus/disabled/busy states throughout.
- **Playwright selectors:** All protected: `button[type='submit']` (Send/Running), `article.message.user/assistant`, `p.error`, `p.config-line > strong`.
- Build: `npm run build` ✅, `npx eslint src` ✅ (2 pre-existing errors in spec files only, out of scope).
- ⚠️ **Note for future ordering assertions:** Newest exchange is now `.article.message.assistant p` `.First == newest reply, not oldest.

## Learnings — No-Skip Test Directive (2026-06-16)

**Directive (Jeffrey Palermo, authoritative):** "I don't want to skip tests under any conditions. I'd rather they fail if they can't pass." NO test may skip — ever. This SUPERSEDES the prior Decision #12 skippable-tests approach.

**Conversion done across FoundryLocalLlmServer.IntegrationTests:**
- `[SkippableFact]` -> `[Fact]` (PlaywrightIntegrationTests).
- All `Skip.If(...)` -> `Assert.False(...)`; all `Skip.IfNot(...)` -> `Assert.True(...)`.
- Playwright browser-install `catch` block `Skip.If(true, ...)` -> `Assert.Fail(...)` (no longer swallows into a skip).
- Files changed: PlaywrightIntegrationTests.cs, AspireGenerationIntegrationTests.cs, PhiFoundryGpuIntegrationTests.cs, ParameterizedModelIntegrationTests.cs (doc comments only — guards already threw). OpenCodeIntegrationTests.cs already used hard `throw` (no skip).
- `Xunit.SkippableFact` package: confirmed NOT referenced in any csproj — fixed the original CS0246 with zero new packages. No `using Xunit.Sdk;` leftovers.
- Stale doc comments mentioning SkippableFact/SkippableTheory updated to state these are real GPU tests that run and must pass when Foundry GPU is available, else fail. `[Trait("Category",...)]` traits kept (categorize only, do not skip). Parameterized per-model coverage and SupportedModelData config sourcing kept intact.

**Verification (CURRENT_DATETIME 2026-06-16):**
- `dotnet build .\FoundryLocalLlmServer.sln -c Debug` -> Build succeeded, 0 Errors, 0 Warnings (CS0246 gone).
- Grep for `Skip.` / `SkippableFact` / `SkippableTheory` / `Assert.Skip` in IntegrationTests -> ZERO matches.
- Full suite `dotnet test` (NO filter): **Skipped = 0**. UnitTests: 2 passed. IntegrationTests: 8 passed, 14 failed, 0 skipped.

**The 14 failing integration tests and the precondition each is missing (this machine, this run):**
- PlaywrightIntegrationTests.AppHost_SendPrompt_ReturnsAssistantResponse_UsingGemma4Gpu — live Foundry Local GPU service / gemma-4 GPU model not available.
- PhiFoundryGpuIntegrationTests (4): PhiOnFoundryGpu_NonStreaming_ReturnsOpenAiCompletion, _Streaming_ReturnsSseChunks, _Response_MatchesOpenAiSchema, _ModelNotAvailable_ErrorsWithoutFallback — "Failed to download GPU variant of 'phi-4-mini'" (no live Foundry GPU service; phi-4-mini artifact excluded/degenerate).
- ParameterizedModelIntegrationTests (6): SelectModel_Gpu_LoadsModelOnGpu, ChatCompletion_Gpu_ReturnsCoherentReply, ToolCalling_Gpu_IsModelAware — each x (qwen2.5-0.5b, qwen2.5-1.5b) — live Foundry Local GPU service not running.
- OpenCodeIntegrationTests.OpenCodeCli_RunsAgainstServer_ReturnsValidResponse — live Foundry service / opencode CLI not present.
- AspireGenerationIntegrationTests.OpenCode_GeneratesCodeResponse (2): qwen2.5-0.5b, qwen2.5-1.5b — live Foundry service / opencode CLI not present.

All 14 failures are honest precondition failures (no GPU Foundry service / opencode CLI on this run), exactly as the user prefers. No skip was re-introduced to make them green.

**2026-06-16 — Apoc's final integration test rework:** In-process server HTTP-driven architecture is the final state. Full suite 24/24 green, 0 skips, zero CLI dependencies on RTX 4060. No-skip directive fully implemented.

**2026-06-16 — Apoc VRAM leak fix + context-bounding:** Server now caps per-request context with FoundryLocalOptions.MaxPromptTokens (default 1024) and MaxResponseTokens (default 2048) via OpenAiChatHelpers.ApplyContextBounds in /v1/chat/completions. Fixed VRAM leak (peak 7867→3259 MiB, latency 175–190s→≤16s) caused by unbounded INPUT prompt arena. New RepeatedPromptVramTests (Playwright + nvidia-smi sampler) verifies peak ≤ 5000 MiB; all 25 integration tests passing.
