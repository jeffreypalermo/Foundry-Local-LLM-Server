# Skill: Environment-Gated Integration Tests with SkippableFact

## When to Use
When an integration test requires an external service (e.g., GPU inference engine, database, external API) that may not be present in all CI environments.

## Pattern

### 1. Add `Xunit.SkippableFact` to the test project
```xml
<PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />
```

### 2. Detect service availability at test start
```csharp
[SkippableFact]
public async Task MyTest()
{
    var serviceUrl = MyServiceHelper.GetServiceUrlAsync().GetAwaiter().GetResult();
    Skip.If(serviceUrl == null, "Service not running — skipping");
    // ... test body
}
```

### 3. Async fixture setup with IAsyncLifetime
When `WebApplicationFactory<T>` needs an async-discovered URL injected into config:

```csharp
public sealed class ServerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string? _serviceUrl;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _serviceUrl = await MyServiceHelper.GetServiceUrlAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
    // WebApplicationFactory's own IDisposable.Dispose() handles cleanup.

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var config = new Dictionary<string, string?> { ["MySection:Model"] = "my-model" };
            if (_serviceUrl != null)
                config["MySection:Endpoint"] = _serviceUrl;
            cfg.AddInMemoryCollection(config);
        });
    }
}
```

### 4. Structural (not echo) assertions for real LLMs
```csharp
// ❌ Don't assert on prompt echo — real LLMs won't mirror the input
Assert.Contains("my prompt text", responseContent);

// ✅ Assert structural validity instead
Assert.NotNull(responseContent);
Assert.True(responseContent.Length > 0);
```

## Key Notes
- `IAsyncLifetime.DisposeAsync()` returns `Task`; `IAsyncDisposable.DisposeAsync()` returns `ValueTask` — they coexist on `WebApplicationFactory<T>` subclass without conflict.
- `ConfigureWebHost` is called lazily on first `CreateClient()`, which is after `IAsyncLifetime.InitializeAsync()` completes — so the cached URL is available.
- xUnit's `[SkippableFact]` reports skipped tests as "skipped" (not failed) in test runners.
