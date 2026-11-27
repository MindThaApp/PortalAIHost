using PortalAIHost.App.Services;

namespace PortalAIHost.App.Tests;

public class UiDirectionRuleTests
{
    [Theory]
    [InlineData("ar")]
    [InlineData("ar-SA")]
    [InlineData("ar-EG")]
    [InlineData("AR-sa")]
    public void Arabic_is_right_to_left(string language) => Assert.True(UiDirectionRule.IsRightToLeft(language));

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("he-IL")]
    [InlineData("arn-CL")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Others_are_left_to_right(string? language) => Assert.False(UiDirectionRule.IsRightToLeft(language));
}
