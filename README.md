# Foundry-Local-LLM-Server

.NET 10 Aspire solution with a React single-page app and an OpenAI-compatible `/v1/chat/completions` endpoint that forwards to Microsoft Foundry Local.

## Solution layout

- `FoundryLocalLlmServer.AppHost` - Aspire orchestrator (Visual Studio/AppHost format)
- `FoundryLocalLlmServer.Server` - ASP.NET Core OpenAI-format endpoint + SPA hosting
- `frontend` - React SPA
- `FoundryLocalLlmServer.UnitTests` - unit tests
- `FoundryLocalLlmServer.IntegrationTests` - integration tests (including Microsoft.Extensions.AI chat abstraction)

## Foundry Local model choice for RTX 5090

The server defaults to `phi-4` (`FoundryLocal:Model`), a high-capability model documented in Foundry Local getting-started guidance and suitable for high-end NVIDIA hardware such as RTX 5090-class GPUs.

## Run Foundry Local

```bash
foundry model run phi-4 --port 5273
```

The app is configured to use:

- Foundry endpoint: `http://127.0.0.1:5273`
- Model: `phi-4`

You can override in `FoundryLocalLlmServer.Server/appsettings*.json` or environment variables.

## Run the Aspire app

```bash
dotnet run --project ./FoundryLocalLlmServer.AppHost/FoundryLocalLlmServer.AppHost.csproj
```

## Open code CLI compatibility

Point your OpenAI-compatible CLI/tooling to this app's server endpoint and model:

- Base URL: `http://localhost:<server-port>/v1`
- Model: `phi-4`

For example, configure your tool with `base_url=http://localhost:5057/v1` and `model=phi-4` when the server is running on port `5057`.

## Tests

```bash
dotnet test ./FoundryLocalLlmServer.sln
```

Integration tests run with `FoundryLocal:UseStubResponses=true` so they are fully automated and do not require a GPU during CI.
