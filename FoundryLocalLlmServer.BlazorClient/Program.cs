using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FoundryLocalLlmServer.BlazorClient;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Point at the SAME Foundry Local proxy server the React/TypeScript SPA uses. This Blazor WASM client
// runs on its own origin and calls the one server over CORS — proving multi-client compatibility
// against a single Foundry Local server process.
const string serverBase = "http://localhost:5537";
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(serverBase) });

await builder.Build().RunAsync();
