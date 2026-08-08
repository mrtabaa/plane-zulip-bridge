using Xunit;

public sealed class BridgeConfigurationTests
{
    [Fact]
    public void ExpandTaskUrlTemplate_ReplacesWorkspaceSlugPlaceholder()
    {
        var result = BridgeConfiguration.ExpandTaskUrlTemplate(
            "https://pms.example.com/{PLANE_WORKSPACE_SLUG}/browse/{projectIdentifier}-{sequenceId}/",
            "hallboard team");

        Assert.Equal(
            "https://pms.example.com/hallboard%20team/browse/{projectIdentifier}-{sequenceId}/",
            result);
    }

    [Fact]
    public void ExpandTaskUrlTemplate_AllowsTemplateWithoutWorkspacePlaceholder()
    {
        const string template =
            "https://pms.example.com/team/browse/{projectIdentifier}-{sequenceId}/";

        var result = BridgeConfiguration.ExpandTaskUrlTemplate(template, "team");

        Assert.Equal(template, result);
    }
}
