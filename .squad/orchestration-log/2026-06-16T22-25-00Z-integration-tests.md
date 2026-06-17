# Orchestration Log — integration-tests

**Session:** Multi-Model Support (2026-06-16)  
**Agent:** integration-tests (Switch)  
**Status:** Completed (background)

## Summary

Switch added a parameterized per-model integration test harness that sources models from the server's committed configuration, making test coverage automatically scale when models are added/removed. Fixed precondition-gated tests to SKIP (not FAIL) without GPU.

## Deliverables

### New Files

- `FoundryLocalLlmServer.IntegrationTests/SupportedModelData.cs` — test matrix source of truth; reads `FoundryLocal:AvailableModels` from `appsettings.json`
- `FoundryLocalLlmServer.IntegrationTests/ParameterizedModelIntegrationTests.cs` — parameterized tests with factories and skip guards
- Added `Xunit.SkippableFact 1.5.61` NuGet package

### Capability Matrix

| Model | In `AvailableModels` | Coherence | Tool Calling | Expected VRAM |
|-------|----------------------|-----------|--------------|---------------|
| qwen2.5-1.5b | ✅ | ✅ asserted | ✅ proper OpenAI `tool_calls` + `finish_reason=tool_calls` | ~2.5 GB |
| qwen2.5-0.5b | ✅ | ✅ asserted | ❌ asserted prose-only (non-empty plain text) | ~1.8 GB |

### Test Categories

**Structural (CI, stub mode, no GPU, no `[Trait]`):**
- `GetModels_StubMode_ReturnsConfiguredModelsAndValidActive` — GET `/api/models` lists configured set + valid active
- `SelectModel_StubMode_ReturnsSuccessShape` `[Theory]` per model — POST `/api/models/select` stub response
- `SelectModel_StubMode_UnknownModel_Returns400` — config-driven allow-list guard

**GPU-Required (`[SkippableTheory]` + `[Trait("Category","GPU-Required")]`, per model):**
- `SelectModel_Gpu_LoadsModelOnGpu` — unload+load; assert `device=="GPU"`, `loaded==true`
- `ChatCompletion_Gpu_ReturnsCoherentReply` — `/v1/chat/completions` coherent reply
- `ToolCalling_Gpu_IsModelAware` — capability-flag-driven assertion (tool-calling only for qwen2.5-1.5b)

### Fixed Precondition-Gated Tests (Assert → Skip)

Three pre-existing integration tests hard-asserted external preconditions and FAILED without GPU. Converted to `[SkippableFact]` + `Skip.If/IfNot`:

- `AspireGenerationIntegrationTests.OpenCode_GeneratesCodeResponse` — `[Theory]`→`[SkippableTheory]`, updated to use `SupportedGenerationModels` (reads config instead of hardcoded phi-4-mini)
- `OpenCodeIntegrationTests.OpenCodeCli_RunsAgainstServer_ReturnsValidResponse` — now uses `SupportedModelData.DefaultModel` (qwen2.5-1.5b instead of phi-4-mini)
- `PlaywrightIntegrationTests.AppHost_SendPrompt_ReturnsAssistantResponse_UsingGemma4Gpu`
- `PhiFoundryGpuIntegrationTests` (4 tests) — `[Fact]`→`[SkippableFact]` + proper skip guards

Skip guard chain: `Skip.If(foundryUrl is null)` → `IfNot(IsRunningAsync)` → `IfNot(opencode-on-PATH)` → `IfNot(EnsureGpuModelReadyAsync)`.

## Verification

- Build: 0 errors, 0 warnings
- `--filter "Category!=GPU-Required"` → Unit 2 ✅, Integration 8 ✅, 0 failed / 0 skipped
- `--filter "Category!=GPU-Required&Category!=Integration"` → Unit 2 ✅, Integration 4 ✅, 0 failed
- `--filter "Category=GPU-Required"` → 14 skipped, 0 failed (no failures on non-GPU environment)

## Key Design Decisions

- **Configuration-driven model set:** Adding a model to `appsettings.json` automatically includes it in parameterized tests; no test code changes needed.
- **Capability matrix separate from config:** Tool-calling support is hardcoded as empirical truth from Apoc's live verification, not pulled from config (since config doesn't express capability).
- **Skip.If/IfNot pattern:** Tests gracefully skip without GPU instead of failing, keeping CI GREEN without a dev GPU. Full coverage preserved on a GPU dev machine.

## Decision Record

Decision #12 (`switch-parameterized-model-tests.md`)
