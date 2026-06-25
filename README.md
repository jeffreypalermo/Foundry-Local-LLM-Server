# Foundry-Local-LLM-Server

.NET 10 Aspire solution with a React single-page app and an OpenAI-compatible `/v1/chat/completions` endpoint that forwards to Microsoft Foundry Local.

## Solution layout

- `FoundryLocalLlmServer.AppHost` - Aspire orchestrator (Visual Studio/AppHost format)
- `FoundryLocalLlmServer.Server` - ASP.NET Core OpenAI-format endpoint + SPA hosting (CORS enabled)
- `frontend` - React/TypeScript SPA (Vite)
- `FoundryLocalLlmServer.BlazorClient` - Blazor WebAssembly client (mirrors the React SPA)
- `tools/DemoDocBuilder` - C# tool that compiles `docs/DEMO.md` from the Playwright demo captures
- `tools/DemoVideoBuilder` - C# tool that transcodes the Playwright walkthrough recording to MP4 (ffmpeg)
- `tools/DemoNarrator` - C# tool that synthesizes a narration WAV per demo with the built-in Windows TTS engine (System.Speech)
- `tools/DemoNarratedVideoBuilder` - C# tool that muxes each narration over its clip and concatenates them into one narrated MP4 (ffmpeg)
- `FoundryLocalLlmServer.UnitTests` - unit tests
- `FoundryLocalLlmServer.IntegrationTests` - integration tests (including Microsoft.Extensions.AI chat abstraction)

## Two clients, one server (multi-client proof)

Both the **React SPA** and the **Blazor WebAssembly client** are independent client apps that talk to the
*same* Foundry Local proxy server process — proving multi-client compatibility against a single
Foundry Local daemon. The Server enables CORS so browser clients on other origins can call it.

```bash
foundry server start                                                   # 1. the one daemon
dotnet run --project ./FoundryLocalLlmServer.Server   # 2. the one server (:5537)
# 3a. React client:
cd frontend && set SERVER_HTTP=http://localhost:5537&& npm run dev      # http://localhost:5173
# 3b. Blazor client (same server):
dotnet run --project ./FoundryLocalLlmServer.BlazorClient --urls http://localhost:5180
```

Open both `:5173` and `:5180` — identical demo harness, one backend. The Blazor client's server base
URL is set in its `Program.cs` (`http://localhost:5537`).

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

## Demonstration video

A captioned **~2.85 h walkthrough (1280×720, H.264, 2× speed)** that demonstrates **every mode of
every model** — for all 41 catalog models it selects the model, then for each capability tab the SPA
renders (text / code / reasoning / vision / tools / speech) runs every scenario and scrolls the
fresh result into view. It is generated, not hand-edited: Playwright drives the real browser against
the live server and a C# tool transcodes/concatenates with ffmpeg. The rendered MP4 is large, so it
is **not committed** (regenerate with the steps below; the spec, config, and tool are in the repo).

`tests/demo-video.spec.ts` is DOM-driven (covers exactly what the app exposes) and idle-aware (waits
for the busy state to clear before each model switch / scenario, so nothing is skipped). An optional
`MODELS=a,b,c` env var restricts the run to a subset (used to record segments). `DemoVideoBuilder`
concatenates all `frontend/video-segments/*.webm` (in filename order) and applies an optional speed
factor.

```bash
# with the daemon + Server(:5537) + Vite(:5173) running (lower MaxResponseTokens, e.g. 384, to keep clips tight):
cd frontend && npx playwright test --config=playwright.video.config.ts   # records test-results-video/…/video.webm
# (move each run's video.webm into frontend/video-segments/ as 01-….webm, 02-….webm to keep segments)
cd .. && dotnet run --project tools/DemoVideoBuilder -- frontend/video-segments docs/foundry-local-demo.mp4 2.0
#                                                       └ segments dir         └ output            └ 2× speed (needs ffmpeg)
```

### Narrated walkthrough of the fast demos (Blazor client)

A second video focuses on the **Blazor WASM client** and every demo scenario that runs in **30 seconds
or less** (the model's recorded per-response time × the scenario's request count — i.e. the figure the
UI advertises on each run button). Each demo is recorded as its own clip and gets a spoken **explanatory
paragraph** narrated over it.

Because the server hosts only Whisper (speech-**to**-text), narration — text-**to**-speech — is produced
locally by the **built-in Windows TTS engine** (`System.Speech`) in `tools/DemoNarrator`; no cloud, no
API keys. `tools/DemoNarratedVideoBuilder` then muxes each narration over its clip (freezing the last
frame when the narration runs longer than the demo) and concatenates everything into one MP4. Demos that
turn out to run far past 30 s on a particular prompt (so they don't qualify) are dropped, not shown blank.

```bash
# daemon + Server(:5537, MaxResponseTokens=2048) + Blazor client (:5180, `dotnet run`) running:
cd frontend && npx playwright test tests/blazor-demo-video.spec.ts --config=playwright.video.blazor.config.ts
#   -> one clip-NNN.webm per demo in frontend/blazor-demo-clips/ + manifest.json (clip → model/scenario + paragraph)
#   PATCH=1 re-records only the demos that failed to capture; MODELS=a,b restricts to a subset
cd .. && dotnet run --project tools/DemoNarrator                  # manifest → narration/clip-NNN.wav (System.Speech; NARRATION_RATE env, default +2)
        dotnet run --project tools/DemoNarratedVideoBuilder       # clips + narration → docs/blazor-under30-narrated-demo.mp4
```

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
