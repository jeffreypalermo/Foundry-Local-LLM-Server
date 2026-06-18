using Xunit;

namespace FoundryLocalLlmServer.IntegrationTests;

[CollectionDefinition("ServerTests")]
public class ServerTestsCollection : ICollectionFixture<ServerFixture>;
