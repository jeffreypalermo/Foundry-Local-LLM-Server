# Session Log — Multi-Model Support Feature

**Date:** 2026-06-16  
**Session ID:** multi-model-support  
**Duration:** Orchestration + decision consolidation

## Feature Summary

Foundry-Local-LLM-Server now supports runtime model switching via a new REST API and React dropdown UI. Integration testing is parameterized over the supported model set, automatically scaling coverage as models are added or removed.

## Manifest

Five agent teams executed in parallel:

1. **research-models** — Researched candidate models, produced ranked shortlist
2. **backend-model-api (Tank)** — Added `GET /api/models` + `POST /api/models/select` REST endpoints, config-driven `FoundryLocal:AvailableModels`
3. **ui-dropdown (Trinity)** — Implemented React model-selector dropdown with live status indicators and error handling
4. **download-test-models (Apoc)** — Live GPU testing on RTX 4060 (8 GB); determined authoritative supported model set
5. **integration-tests (Switch)** — Parameterized per-model integration test harness + fixed precondition-gated tests to SKIP (not FAIL) on non-GPU environments

## Verified Supported Model Set

Live GPU testing on Jeffrey Palermo's RTX 4060 determined:

- **qwen2.5-1.5b** (default): coherent ✅, proper OpenAI `tool_calls` ✅, ~2.5 GB VRAM
- **qwen2.5-0.5b** (fallback): coherent ✅, prose-only, ~1.8 GB VRAM

Excluded:
- phi-4-mini (token-0 degenerate output)
- 7B class models (95% VRAM on 8 GB → OOM-risk)
- qwen2.5-coder-3b (does not exist in catalog)

## Configuration

```json
"FoundryLocal": {
  "Model": "qwen2.5-1.5b",
  "AvailableModels": [ "qwen2.5-1.5b", "qwen2.5-0.5b" ]
}
```

Updated in both `appsettings.json` and `appsettings.Development.json`.

## Build & Test Results

- **Build:** 0 errors, 0 warnings (2 pre-existing unused-var warnings unrelated)
- **Unit Tests:** 2/2 passed
- **Integration Tests (stub mode):** 8/8 passed
- **CI Filter (GPU-free):** `--filter "Category!=GPU-Required"` → 10 passed, 0 failed
- **GPU-Required Category:** 14 skipped, 0 failed (graceful skip pattern enabled)

## Design Decisions Recorded

- Decision #9: Supported Foundry Local Models — Authoritative Determination (Apoc)
- Decision #10: API Contract — Model Listing & Switching (Tank)
- Decision #11: Model Picker Dropdown + Hot-Swap (Trinity)
- Decision #12: Parameterized Per-Model Integration Test Harness (Switch)

## Next Steps

- Ratify Decision #11 (model picker dropdown) in squad consensus
- Deploy multi-model configuration to production
- Monitor model switching behavior under opencode + MCP workloads
