# Session Log: opencode Configuration
**Timestamp:** 2026-05-11T23:13:40Z

## Summary
Configured opencode v1.14.31 to use local Foundry LLM server with phi-4 model. Added `GET /v1/models` endpoint support. Updated user-level opencode config to reference local proxy at http://localhost:5537/v1.

## Impact
Enables local LLM development workflows: `opencode run --model foundry-local/phi-4 "prompt"` now works without external API keys.
