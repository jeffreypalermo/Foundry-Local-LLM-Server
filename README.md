# Foundry-Local-LLM-Server

.NET 10 Aspire solution with a React single-page app and an OpenAI-compatible `/v1/chat/completions` endpoint that forwards to Microsoft Foundry Local.

## Solution layout

- `FoundryLocalLlmServer.AppHost` - Aspire orchestrator (Visual Studio/AppHost format)
- `FoundryLocalLlmServer.Server` - ASP.NET Core OpenAI-format endpoint + SPA hosting
- `frontend` - React SPA
- `FoundryLocalLlmServer.UnitTests` - unit tests
- `FoundryLocalLlmServer.IntegrationTests` - integration tests (including Microsoft.Extensions.AI chat abstraction)

## Foundry Local default model

The server defaults to `phi-4-mini` (`FoundryLocal:Model`).

## Run Foundry Local

```bash
foundry model run phi-4-mini --port 5273
```

The app is configured to use:

- Foundry endpoint: `http://127.0.0.1:5273`
- Model: `phi-4-mini`

You can override in `FoundryLocalLlmServer.Server/appsettings*.json` or environment variables.

## Models

This host targets an **NVIDIA GeForce RTX 5070 Ti (16 GB)**. Every GPU variant in the
Foundry Local catalog fits in 16 GB VRAM, so the full GPU-compatible catalog is included.
`FoundryLocal:AvailableModels` lists the curated selectable aliases (chat, reasoning,
coder, vision-language, and Whisper speech-to-text). Foundry's alias resolver prefers the
CUDA/TensorRT-RTX GPU build of each model over NPU/CPU automatically.

Model endpoints:

- `GET /api/models` — `{ current, available[] }`, the selectable set and active model.
- `POST /api/models/select` — `{ "model": "<alias>" }` switches the active model; Foundry
  loads it lazily on the next chat request. Only aliases in `AvailableModels` are accepted.
- `GET /v1/models` — OpenAI-format list proxied live from Foundry (what is loaded/cached).

Adding or removing selectable models is a config-only change to `AvailableModels`.

> **MAI models are out of scope (cloud-only).** Microsoft's MAI family — MAI-Thinking-1,
> MAI-Image-2 / -Efficient, MAI-Voice-1, MAI-Transcribe-1 — runs only in **Microsoft
> Foundry (Azure)**, not Foundry Local. There are no on-device ONNX/CUDA weights, so they
> cannot run on this GPU. This server is local-only; reaching MAI would require a separate
> Azure Foundry cloud route.

## Run the Aspire app

```bash
dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj
```

## Open code CLI compatibility

Point your OpenAI-compatible CLI/tooling to this app's server endpoint and model:

- Base URL: `http://localhost:<server-port>/v1`
- Model: `gemma4`

For example, configure your tool with `base_url=http://localhost:5057/v1` and `model=gemma4` when the server is running on port `5057`.

## Tests

```bash
dotnet test ./FoundryLocalLlmServer.sln
```

The OpenAI-compatibility integration tests run with `FoundryLocal:UseStubResponses=true` and do not require a GPU. Live Foundry/opencode/Playwright integration tests are skipped automatically when prerequisites are unavailable.
