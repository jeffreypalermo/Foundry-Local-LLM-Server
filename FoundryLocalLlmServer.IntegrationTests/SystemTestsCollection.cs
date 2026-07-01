using Xunit;

namespace FoundryLocalLlmServer.IntegrationTests;

// Separate from "ServerTests": those tests assume a pre-started stub server on a fixed port
// (see build.ps1's Start-StubServer). System tests boot their own AppHost-orchestrated server on a
// dynamically assigned port and must never run concurrently with another AppHost instance.
[CollectionDefinition("SystemTests", DisableParallelization = true)]
public class SystemTestsCollection;
