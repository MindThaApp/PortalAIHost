using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;

namespace Portal.AiHost.Core.Tests.Auth;

public class PortalPublisherTests
{
    private const string FilesPortal = "MindtheApp.FilesPortal_24e29cj9741rt";
    private const string StorePowerShell = "Microsoft.PowerShell_8wekyb3d8bbwe";

    [Fact]
    public void PublisherIdMatchesWindows()
    {
        Assert.Equal("24e29cj9741rt", PortalPublisher.Id);
        Assert.Equal("8wekyb3d8bbwe",
            PortalPublisher.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
    }

    [Fact]
    public void StoreInstalledPortalPackageIsAPortalApp()
    {
        Assert.True(PortalPublisher.IsPortalPackage(FilesPortal, installedFromStore: true));
        Assert.True(PortalPublisher.IsPortalPackage("MindtheApp.VoicePortal_24E29CJ9741RT", installedFromStore: true));
    }

    [Fact]
    public void MicrosoftSignedStorePackageIsNotAPortalApp() =>
        Assert.False(PortalPublisher.IsPortalPackage(StorePowerShell, installedFromStore: true));

    [Fact]
    public void UnpackagedOrUnsignedCallerIsNotAPortalApp()
    {
        Assert.False(PortalPublisher.IsPortalPackage(null, installedFromStore: false));
        Assert.False(PortalPublisher.IsPortalPackage("", installedFromStore: true));
    }

    [Fact]
    public void SpoofedPortalNamesAreNotPortalApps()
    {
        // Portal-looking package name under another publisher.
        Assert.False(PortalPublisher.IsPortalPackage("MindtheApp.FilesPortal_8wekyb3d8bbwe", installedFromStore: true));
        Assert.False(PortalPublisher.IsPortalPackage("PortalApps.FilesPortal_8wekyb3d8bbwe", installedFromStore: true));

        // Portal publisher id outside the publisher slot, or with extra parts.
        Assert.False(PortalPublisher.IsPortalPackage("24e29cj9741rt", installedFromStore: true));
        Assert.False(PortalPublisher.IsPortalPackage("_24e29cj9741rt", installedFromStore: true));
        Assert.False(PortalPublisher.IsPortalPackage("Evil_24e29cj9741rt_x", installedFromStore: true));
        Assert.False(PortalPublisher.IsPortalPackage("Evil.App_24e29cj9741rtx", installedFromStore: true));

        // Developer Mode can register a package under any publisher, so only a Store install counts.
        Assert.False(PortalPublisher.IsPortalPackage(FilesPortal, installedFromStore: false));
    }

    [Fact]
    public void OnlyPortalCallersReachTheControlPipeAsPortalApps()
    {
        var now = DateTimeOffset.UnixEpoch;
        AccessRequestInfo Request(CallerIdentity caller, string name = "Files Portal") =>
            new("ar_1", "files-portal", name, null, null, caller, now, now.AddMinutes(2));

        var portal = new CallerIdentity(1, FilesPortal, @"C:\Program Files\WindowsApps\x\FilesPortal.exe",
            Verified: true, SameUser: true, PortalApp: true);
        var pwsh = new CallerIdentity(2, StorePowerShell, @"C:\Program Files\WindowsApps\y\pwsh.exe",
            Verified: true, SameUser: true);
        var unsigned = new CallerIdentity(3, null, @"C:\Tools\tool.exe", Verified: false, SameUser: true);
        var claimsPortal = new CallerIdentity(4, null, @"C:\Tools\tool.exe", Verified: false, SameUser: true, PortalApp: true);

        Assert.True(ApprovalPrompts.ToControl(Request(portal)).CallerPortalApp);
        PendingApproval p = ApprovalPrompts.ToControl(Request(pwsh, "PowerShell"));
        Assert.True(p.CallerVerified);
        Assert.False(p.CallerPortalApp);
        Assert.False(ApprovalPrompts.ToControl(Request(unsigned)).CallerPortalApp);

        // A Portal flag without a package identity is never passed on.
        Assert.False(ApprovalPrompts.ToControl(Request(claimsPortal)).CallerPortalApp);
    }
}
