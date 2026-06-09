# Foundry-Local-LLM-Server

.NET 10 Aspire solution with a React single-page app and an OpenAI-compatible `/v1/chat/completions` endpoint that forwards to Microsoft Foundry Local.

## Solution layout

- `FoundryLocalLlmServer.AppHost` - Aspire orchestrator (Visual Studio/AppHost format)
- `FoundryLocalLlmServer.Server` - ASP.NET Core OpenAI-format endpoint + SPA hosting
- `frontend` - React SPA
- `FoundryLocalLlmServer.UnitTests` - unit tests
- `FoundryLocalLlmServer.IntegrationTests` - integration tests (including Microsoft.Extensions.AI chat abstraction)

## In-process Foundry Local runtime (v1.2 SDK)

The server hosts Foundry Local **in-process** via the `Microsoft.AI.Foundry.Local.WinML` v1.2 .NET SDK — no separate `foundry` CLI/service is required. On startup, a background bootstrapper:

1. Initializes the Foundry Local runtime (`FoundryLocalManager`).
2. Downloads and registers **only** the configured execution provider (default `CUDAExecutionProvider`). The first run downloads the CUDA EP (hundreds of MB) — be patient; the per-attempt timeout defaults to 90 minutes.
3. Downloads (if needed) and loads the configured model (default `phi-4-mini`), preferring the GPU variant. GPU is mandatory by default (`FoundryLocal:RequireGpu`) — there is no silent CPU fallback.
4. Starts the SDK's embedded OpenAI-compatible web service that the proxy forwards to.

Until initialization completes, `/v1/chat/completions` and `/v1/models` return `503` with a ProblemDetails payload describing progress. Poll `GET /api/foundry/status` for detailed state (phase, EP/model download percent, last error).

Key settings (`FoundryLocal` section in `FoundryLocalLlmServer.Server/appsettings*.json` or environment variables):

- `Model` — model alias to load (default `phi-4-mini`)
- `ExecutionProvider` — ONNX Runtime EP to register (default `CUDAExecutionProvider`)
- `RequireGpu` — fail startup if no GPU variant exists (default `true`)
- `InitializationTimeoutMinutes` / `MaxInitializationAttempts` — first-run download resilience

> Note: the server targets `net10.0-windows` (WinML requirement) and runs on Windows x64.

## Run the Aspire app

```bash
dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj
```

## Open code CLI compatibility

Point your OpenAI-compatible CLI/tooling to this app's server endpoint and model:

- Base URL: `http://localhost:<server-port>/v1`
- Model: `phi-4-mini`

For example, configure your tool with `base_url=http://localhost:5057/v1` and `model=phi-4-mini` when the server is running on port `5057`.

## Tests

```bash
dotnet test ./FoundryLocalLlmServer.sln
```

Integration tests run with `FoundryLocal:UseStubResponses=true` so they are fully automated and do not require a GPU during CI.
