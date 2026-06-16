# Orchestration Log — download-test-models

**Session:** Multi-Model Support (2026-06-16)  
**Agent:** download-test-models (Apoc)  
**Status:** Completed (background)

## Summary

Apoc ran live end-to-end GPU testing on Jeffrey Palermo's RTX 4060 laptop to determine which candidate models actually load coherently and support tool calling for opencode integration.

## Test Methodology

- In-process Foundry Local via `Microsoft.AI.Foundry.Local.WinML` 1.2.3 (NOT the broken 0.8.119 CLI)
- Each candidate downloaded via `POST /api/models/select` (EP-aware CUDA variant selection)
- Two probes per model:
  1. **Coherence:** plain prompt; reply must be >10 chars and not degenerate
  2. **Tool Calling:** request with `tools` array (`get_weather(location)`), `tool_choice="auto"`; PASS only if proper OpenAI `tool_calls` object returned
- VRAM measured with `nvidia-smi` (idle baseline = 24 MiB; total = 8188 MiB)

## Verified Supported Set

| Model | Coherence | Tool Calling | VRAM | Verdict |
|-------|-----------|--------------|------|---------|
| **qwen2.5-1.5b** | ✅ | ✅ proper `tool_calls` | ~2.5 GB | **SUPPORTED — best for opencode** |
| **qwen2.5-0.5b** | ✅ | ❌ prose-only | ~1.8 GB | **SUPPORTED — lightweight fallback** |
| phi-4-mini | ❌ token-0 `!!!!` | n/a | ~5.8 GB | EXCLUDED (incompatible `:5` artifact) |
| qwen2.5-coder-7b | ✅ | ❌ XML text, not `tool_calls` | **95% VRAM (7791/8188)** | EXCLUDED (no real tool calling + OOM-risk) |
| qwen2.5-7b | unverified | unverified | ~7.8 GB expected | EXCLUDED (CDN throttled + 7B class = 95% VRAM) |
| qwen2.5-coder-3b | n/a (not in catalog) | n/a | n/a | EXCLUDED (does not exist) |

## Configuration Committed

```json
"FoundryLocal": {
  "Model": "qwen2.5-1.5b",
  "AvailableModels": [ "qwen2.5-1.5b", "qwen2.5-0.5b" ]
}
```

Updated both `appsettings.json` and `appsettings.Development.json`.

## Decision Record

Decision #9 (`apoc-supported-models.md`)
