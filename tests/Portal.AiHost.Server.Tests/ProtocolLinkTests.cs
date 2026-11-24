using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

public sealed class ProtocolLinkTests
{
    [Theory]
    [InlineData("portal-ai-host:settings/performance", "settings/performance")]
    [InlineData("PORTAL-AI-HOST:Settings/Performance", "settings/performance")]
    [InlineData("portal-ai-host://settings/performance/", "settings/performance")]
    [InlineData("\"portal-ai-host:models\"", "models")]
    [InlineData("portal-ai-host:apps", "apps")]
    public void Page_links_open_the_window_on_that_page(string arg, string page)
    {
        Assert.Equal(LaunchMode.Open, LaunchArgs.Parse([arg]));
        Assert.Equal(page, LaunchArgs.PageFor([arg]));
        Assert.True(LaunchArgs.IsProtocol([arg]));
    }

    [Theory]
    [InlineData("portal-ai-host:start")]
    [InlineData("portal-ai-host:open")]
    [InlineData("portal-ai-host:settings")]
    [InlineData("portal-ai-host:settings/performance?x=1")]
    [InlineData("portal-ai-host:settings/../models")]
    [InlineData("portal-ai-host:run?cmd=calc.exe")]
    [InlineData("--page=models")]
    public void Other_text_names_no_page(string arg)
    {
        Assert.Null(LaunchArgs.PageFor([arg]));
    }

    [Fact]
    public void Unknown_links_stay_a_plain_start()
    {
        Assert.Equal(LaunchMode.Start, LaunchArgs.Parse(["portal-ai-host:settings/performance?x=1"]));
        Assert.Equal(LaunchMode.Start, LaunchArgs.Parse(["portal-ai-host:run?cmd=calc.exe"]));
    }
}
