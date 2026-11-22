using Portal.AiHost.Core.Abstractions;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>What the host sends the app for an access prompt: only a matched Portal package is a Portal app.</summary>
public class ApprovalIdentityTests
{
    private static AccessRequestInfo Request(CallerIdentity caller, string name) =>
        new("ar_1", "files-portal", name, null, null, caller, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2));

    [Fact]
    public void PortalPackageIsSentAsAPortalApp()
    {
        var caller = new CallerIdentity(1, "MindtheApp.FilesPortal_24e29cj9741rt", null, Verified: true, SameUser: true, PortalApp: true);
        var p = ControlHandler.ToPending(Request(caller, "Files Portal"));
        Assert.True(p.CallerVerified);
        Assert.True(p.CallerPortalApp);
    }

    [Fact]
    public void MicrosoftStorePackageIsNotSentAsAPortalApp()
    {
        var caller = new CallerIdentity(2, "Microsoft.PowerShell_8wekyb3d8bbwe", null, Verified: true, SameUser: true);
        var p = ControlHandler.ToPending(Request(caller, "PowerShell"));
        Assert.True(p.CallerVerified);
        Assert.False(p.CallerPortalApp);
        Assert.Equal("Microsoft.PowerShell_8wekyb3d8bbwe", p.CallerIdentity);
    }

    [Fact]
    public void UnpackagedCallerClaimingAPortalNameIsNotSentAsAPortalApp()
    {
        var caller = new CallerIdentity(3, null, @"C:\Tools\tool.exe", Verified: false, SameUser: true, PortalApp: true);
        var p = ControlHandler.ToPending(Request(caller, "Files Portal"));
        Assert.False(p.CallerVerified);
        Assert.False(p.CallerPortalApp);
        Assert.Null(p.Publisher);
    }
}
