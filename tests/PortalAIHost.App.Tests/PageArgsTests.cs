using PortalAIHost.App.Services;

namespace PortalAIHost.App.Tests;

public class PageArgsTests
{
    [Theory]
    [InlineData("--page=models", "models")]
    [InlineData("\"C:\\x\\PortalAIHost.exe\" --page=settings/performance", "settings/performance")]
    [InlineData("--page=", null)]
    [InlineData("--tray-popup=1,2", null)]
    [InlineData(null, null)]
    public void Finds_the_page_in_a_command_line(string? line, string? expected)
    {
        Assert.Equal(expected, PageArgs.Find(line));
    }

    [Theory]
    [InlineData("settings/performance", "settings", "performance")]
    [InlineData("settings", "settings", null)]
    [InlineData("Models", "models", null)]
    [InlineData("apps/", "apps", null)]
    [InlineData("nonsense/performance", "home", null)]
    public void Splits_a_page_and_its_section(string tag, string page, string? section)
    {
        Assert.Equal((page, section), PageArgs.Split(tag));
    }
}
