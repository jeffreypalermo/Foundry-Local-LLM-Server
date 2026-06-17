namespace FoundryLocalLlmServer.IntegrationTests;

public class FoundryServiceHelperAliasTests
{
    [Fact]
    public void ModelIdMatchesAlias_AcceptsCompactAliasForDashedModelFamily()
    {
        var matches = FoundryServiceHelper.ModelIdMatchesAlias("gemma-4-cuda-gpu:1", "gemma4");
        Assert.True(matches);
    }

    [Fact]
    public void ModelIdMatchesAlias_AcceptsDashedAliasForCompactModelFamily()
    {
        var matches = FoundryServiceHelper.ModelIdMatchesAlias("gemma4-cuda-gpu:1", "gemma-4");
        Assert.True(matches);
    }
}
