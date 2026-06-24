var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.FoundryLocalLlmServer_Server>("server")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithReference(server)
    .WaitFor(server);

// Second client: the standalone Blazor WebAssembly SPA. It calls the SAME proxy server the React
// SPA uses (hardcoded to the server's http endpoint, localhost:5537) over CORS, demonstrating
// multi-client compatibility. Running it under the AppHost means it shows up in the Aspire dashboard
// alongside the React frontend instead of having to be launched by hand.
var blazorclient = builder.AddProject<Projects.FoundryLocalLlmServer_BlazorClient>("blazorclient")
    .WaitFor(server);

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();
