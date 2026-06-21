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

## Foundry Local version (cross-platform v1.2.x / CLI 0.10)

This project targets the **cross-platform GA Foundry Local (v1.2.x line, CLI 0.10)** — not the
older 0.8.x winget package. Install the CLI `.msix` from the GitHub release and remove the old
winget package so `foundry` resolves to the new CLI:

```powershell
# from https://github.com/microsoft/Foundry-Local/releases (cli-preview-0.10.0)
Add-AppxPackage .\foundry-0.10.0-win-x64-winml.msix
Get-AppxPackage Microsoft.FoundryLocal | Remove-AppxPackage   # remove old 0.8.x
foundry --version    # 0.10.0
```

The new CLI restructured commands: `foundry server start` / `foundry server status -o json`
(was `service`), and `model load`/`download` auto-select the variant (the `--device`/`--ttl`
flags were removed). The Server, test helper, and `privatebuild.ps1` all use the new commands.

## Run Foundry Local

```bash
foundry server start            # starts the daemon; the Server auto-discovers its URL
```

The Server auto-discovers the daemon URL via `foundry server status` and loads the GPU variant
of the selected model on demand. Override the endpoint/model in
`FoundryLocalLlmServer.Server/appsettings*.json` or environment variables.

## Models & capabilities

This host targets an **NVIDIA GeForce RTX 5070 Ti (16 GB)**. Every GPU variant in the Foundry
Local catalog fits in 16 GB VRAM, so the full GPU-compatible catalog is included.
`FoundryLocal:AvailableModels` lists the curated selectable aliases. The proxy resolves each
alias to its CUDA/GPU variant and pre-loads that exact variant (the daemon does not auto-load).

Verified working on v1.2.x: **text, code, reasoning, vision** (Qwen-VL / Qwen3.5 / Ministral via
`/v1/chat/completions` image input), **tool calling** (Phi-4-mini family), and **speech-to-text**
(Whisper). Whisper has no daemon HTTP route, so the Server bridges transcription to the
`foundry transcribe` CLI.

Endpoints:

- `GET /api/models` — `{ current, available[] }`; each model carries its capability flags
  (`text`/`code`/`reasoning`/`vision`/`audio`/`tools`) for the SPA's per-model panels.
- `POST /api/models/select` — `{ "model": "<alias>" }` switches the active model.
- `GET /v1/models` — OpenAI-format list proxied live from Foundry.
- `POST /v1/chat/completions` — chat (text + `image_url` vision + `tools`).
- `POST /v1/audio/transcriptions` — OpenAI-style multipart speech-to-text (bridges to the CLI).

## Web UI

The SPA shows a **model picker** (with capability badges) and renders a capability-specific test
panel for the selected model — **Text chat**, **Vision** (image upload), **Tools**
(`get_weather` demo), and **Speech-to-text** (audio upload) — so you can exercise the full
capabilities of each model from the browser.

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
- Model: `phi-4-mini` (or any alias from `GET /api/models`)

For example, configure your tool with `base_url=http://localhost:5057/v1` and `model=phi-4-mini` when the server is running on port `5057`.

## Tests

```bash
dotnet test ./FoundryLocalLlmServer.sln          # stub-mode (no GPU) — CI-safe
./privatebuild.ps1                               # full build incl. the live GPU matrix
```

The OpenAI-compatibility tests run with `FoundryLocal:UseStubResponses=true` and need no GPU.
The **live capability matrix** (`FullCapabilityMatrixTests`, tagged `Category=GPU-Required`)
exercises every model in `AvailableModels` against real Foundry inference — text/code/reasoning
prompts, vision image description, tool calls, and Whisper transcription — and is run by
`privatebuild.ps1`. CI filters out `Category=GPU-Required`.
