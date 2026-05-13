# Tank — Project History

## Project Context

- **Project:** Foundry-Local-LLM-Server
- **Stack:** .NET 10 Aspire, ASP.NET Core, OpenAI-compatible API
- **Server project:** `FoundryLocalLlmServer.Server`
- **Foundry Local:** `http://127.0.0.1:5273`, model `phi-4` by default (override via `FoundryLocal:Model`, `FoundryLocal:Endpoint`)
- **Integration tests:** `OpenAiCompatibilityTests` uses real Foundry Local with `[SkippableFact]` — skips gracefully when service not running. `UseStubResponses` removed from test config.
- **Test command:** `dotnet test ./FoundryLocalLlmServer.sln`
- **User:** Jeffrey Palermo

## Learnings

### 2026-05-12 — Real-mode integration tests with SkippableFact

- Removed `UseStubResponses=true` from `ServerFactory` config; tests now proxy to actual Foundry Local.
- `ServerFactory` implements `IAsyncLifetime` to call `FoundryServiceHelper.GetServiceUrlAsync()` before `ConfigureWebHost` runs; URL cached in `_foundryUrl`, injected as `FoundryLocal:Endpoint`.
- `IAsyncLifetime.DisposeAsync()` (returns `Task`) and `IAsyncDisposable.DisposeAsync()` (returns `ValueTask`) coexist without conflict on a `WebApplicationFactory<T>` subclass via explicit interface implementation.
- Assertions changed from prompt-echo string checks to structural: non-null + `Length > 0` — real LLMs don't echo the prompt back.
- `Xunit.SkippableFact` v1.4.13 was already in the project; `[SkippableFact]` + `Skip.If(url == null, ...)` is the correct pattern for environment-gated integration tests.
