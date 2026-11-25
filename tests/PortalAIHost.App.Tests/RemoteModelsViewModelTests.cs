using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

/// <summary>The laptop's "Models on the PC" section and the per-app PC model (docs/client-mode-v1.md 4.7).</summary>
public class RemoteModelsViewModelTests
{
    private static async Task<(Rig Rig, RemoteModelsViewModel Vm)> StartAsync(bool client = true, bool grant = true)
    {
        var rig = new Rig(FakeScenario.Loaded);
        rig.Host.ClientMode = client;
        rig.Host.ModelControlGranted = grant;
        var vm = new RemoteModelsViewModel(rig.Shell, rig.Ui, rig.Strings);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task InServerModeTheSectionStaysHidden()
    {
        var (rig, vm) = await StartAsync(client: false);
        using var _ = rig;
        Assert.False(vm.IsClientMode);
        Assert.Empty(vm.Rows);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.IsLoading);
        Assert.DoesNotContain("list", rig.Host.RemoteCalls);
        vm.Dispose();
    }

    [Fact]
    public async Task WithoutTheGrantTheEmptyStateNamesThePcAndNoListIsAsked()
    {
        var (rig, vm) = await StartAsync(grant: false);
        using var _ = rig;
        Assert.True(vm.IsClientMode);
        Assert.True(vm.ShowEmpty);
        Assert.Equal("Vm_RemAskOwner:Desk PC", vm.EmptyText);
        Assert.Equal("Vm_RemTitle:Desk PC", vm.Title);
        Assert.Empty(vm.Rows);
        Assert.DoesNotContain("list", rig.Host.RemoteCalls);
        vm.Dispose();
    }

    [Fact]
    public async Task WithTheGrantEveryInstalledModelIsListedWithLoadOffered()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.False(vm.ShowEmpty);
        Assert.True(vm.HasControl);
        Assert.Equal(["Alpha", "Beta"], vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => { Assert.True(r.CanLoad); Assert.False(r.CanUnload); });
        Assert.Equal(1.0, vm.ListOpacity);
        vm.Dispose();
    }

    [Fact]
    public async Task LoadingAndUnloadingUpdatesTheRowFromThePcsAnswer()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        var alpha = vm.Rows.Single(r => r.Id == "alpha");
        alpha.PlacementIndex = 2; // System memory

        await vm.LoadAsync(alpha);

        Assert.Contains("load:alpha:cpu", rig.Host.RemoteCalls);
        Assert.False(alpha.CanLoad);
        Assert.True(alpha.CanUnload);
        Assert.Equal("Vm_RemByLaptop", alpha.LoadedByText);
        Assert.Equal("Vm_RemStateLoaded", alpha.StateText);
        Assert.False(alpha.Busy);

        await vm.UnloadAsync(alpha);

        Assert.True(alpha.CanLoad);
        Assert.False(alpha.CanUnload);
        Assert.Equal("", alpha.LoadedByText);
        vm.Dispose();
    }

    [Fact]
    public async Task ModelLoadedByTheOwnerOrAnotherDeviceCannotBeUnloadedHere()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        rig.Host.RemoteModels[0].State = "loaded";
        rig.Host.RemoteModels[0].LoadedBy = "pc";
        rig.Host.RemoteModels[1].State = "loaded";
        rig.Host.RemoteModels[1].LoadedBy = "dev_other";

        await vm.RefreshAsync(default);

        Assert.Equal("Vm_RemByPc", vm.Rows[0].LoadedByText);
        Assert.Equal("Vm_RemByOther", vm.Rows[1].LoadedByText);
        Assert.All(vm.Rows, r => { Assert.False(r.CanUnload); Assert.False(r.CanLoad); });
        vm.Dispose();
    }

    [Fact]
    public async Task AConflictShowsThePlainMessageAndKeepsTheList()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        rig.Host.NextRemoteError = (ErrorCodes.ModelConflict, ModelConflictReasons.InUseByHost);

        await vm.LoadAsync(vm.Rows[0]);

        Assert.True(vm.HasMessage);
        Assert.Equal("The PC said no.", vm.Message);
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.Rows[0].CanLoad);
        Assert.False(vm.Rows[0].Busy);

        await vm.LoadAsync(vm.Rows[0]);
        Assert.False(vm.HasMessage); // A later good answer clears it.
        vm.Dispose();
    }

    [Fact]
    public async Task GrantRevokedMidUseTurnsTheListIntoTheEmptyState()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        rig.Host.ModelControlGranted = false;

        await vm.LoadAsync(vm.Rows[0]);

        Assert.True(vm.ShowEmpty);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task BurstsOfChangePushesBecomeOneRefreshAndStopWhenThePageIsLeft()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        var before = rig.Host.RemoteCalls.Count(c => c == "list");

        for (var i = 0; i < 6; i++) rig.Host.CompletePairingNoop();
        await Task.Delay(900);
        Assert.Equal(before + 1, rig.Host.RemoteCalls.Count(c => c == "list"));

        vm.Dispose();
        rig.Host.CompletePairingNoop();
        await Task.Delay(600);
        Assert.Equal(before + 1, rig.Host.RemoteCalls.Count(c => c == "list")); // Left the page: nothing runs.
    }

    [Fact]
    public async Task PerAppModelIsOfferedOnlyWithTheGrantAndIsSaved()
    {
        var (rig, _) = await StartAsync(grant: false);
        using var _r = rig;
        var apps = new AppModelsViewModel(rig.Shell, rig.Ui, rig.Strings);
        await apps.LoadAsync();
        Assert.False(apps.Available);
        Assert.Empty(apps.Rows);

        rig.Host.ModelControlGranted = true;
        await apps.RefreshAsync(default);
        Assert.True(apps.Available);
        Assert.Equal(["Vm_AppModelPcChoice", "Alpha", "Beta"], apps.Choices);
        Assert.Equal(2, apps.Rows.Count);
        var row = apps.Rows.Single(r => r.AppId == "voice-portal");
        Assert.Equal(0, row.SelectedIndex);

        row.SelectedIndex = 2; // Beta
        await Task.Delay(100);
        Assert.Equal("beta", rig.Host.AppModelChoices["voice-portal"]);

        row.SelectedIndex = 0; // PC's choice
        await Task.Delay(100);
        Assert.Empty(rig.Host.AppModelChoices);

        rig.Host.AppModelChoices["voice-portal"] = "alpha";
        await apps.RefreshAsync(default);
        Assert.Equal(1, apps.Rows.Single(r => r.AppId == "voice-portal").SelectedIndex); // Shown from what is saved, without saving again.
        Assert.Equal(2, rig.Host.RemoteCalls.Count(c => c.StartsWith("appmodel:")));
        apps.Dispose();
    }
}
