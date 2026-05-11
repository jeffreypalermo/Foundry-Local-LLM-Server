# Squad Decisions

## Active Decisions

### 1. Proxy Error Handling for Foundry Local Unavailability

**Date:** 2026-05-11  
**Author:** Apoc  
**Status:** Implemented (commit 396b3ee)

**Context:** During live exploratory testing with Playwright, Foundry Local was not running on port 5273. The `/v1/chat/completions` proxy endpoint had no error handling for network failures.

**Decision:**
- Return 503 Service Unavailable (not 500) when upstream Foundry Local is unreachable.
- Include a user-readable `detail` field: "Could not reach Foundry Local at {endpoint}. Ensure the service is running."
- Frontend parses ProblemDetails and displays the detail/title field in error paragraph.

**Rationale:** 503 is semantically correct for upstream dependency failures. Surfacing the endpoint URL immediately tells developers where to look. No change to happy path or stub mode.

---

### 2. opencode uses foundry-local provider for phi-4

**Date:** 2026-05-11  
**Author:** Apoc  
**Status:** Accepted

**Context:** The project runs an ASP.NET Core proxy server at port 5537 that forwards OpenAI-compatible requests to Microsoft Foundry Local (GPU inference engine). opencode can use any OpenAI-compatible endpoint.

**Decision:**
- Configure opencode to use the local Foundry Local LLM server as a custom provider named `foundry-local`, targeting `phi-4` at `http://localhost:5537/v1`.
- Add `GET /v1/models` endpoint to `Program.cs` (required by opencode's SDK for model discovery).
- Add `foundry-local` provider entry to `~/.config/opencode/opencode.json` (user-level global config).

**Consequences:**
- `opencode run --model foundry-local/phi-4 "prompt"` works without any API key.
- Foundry Local GPU service must be running on port 5273 for real inference.
- opencode config is user-specific (not committed to repo).

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
