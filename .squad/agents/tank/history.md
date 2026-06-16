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

### 2026-06-02 — Build/test/smoke validation pass

- `dotnet build .\FoundryLocalLlmServer.sln` succeeds; `dotnet test .\FoundryLocalLlmServer.sln` passes with environment-gated skips when Foundry Local is not running (7 total, 4 passed, 3 skipped).
- Frontend checks pass with `npm run lint` and `npm run build`; `npm ci` may fail on Windows with an `EPERM` unlink against `@rolldown` native binary when the file is locked.
- Running `FoundryLocalLlmServer.Server` directly serves APIs but returns `404` at `/` without published `wwwroot`; for local end-to-end smoke, run frontend dev server with `SERVER_HTTP` pointed at backend and validate `/api/foundry` plus `/v1/chat/completions` through Vite proxy.
