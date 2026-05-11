# Tank — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, OpenAI-compatible API
- **Server project:** `FoundryLocalLlmServer.Server`
- **Foundry Local:** `http://127.0.0.1:5273`, model `phi-4` by default (override via `FoundryLocal:Model`, `FoundryLocal:Endpoint`)
- **Integration tests:** `UseStubResponses=true` in `FoundryLocalLlmServer.IntegrationTests` — fully automated, no GPU required
- **Test command:** `dotnet test ./FoundryLocalLlmServer.sln`
- **User:** Jeffrey Palermo

## Learnings
