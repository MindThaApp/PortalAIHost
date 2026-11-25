using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class RemoteSettingsTests
{
    private static async Task<(Rig Rig, SettingsViewModel Vm)> StartAsync()
    {
        var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task OtherDevicesStartOffWithNoCodeAndNoDevices()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.False(vm.RemoteEnabled);
        Assert.False(vm.PairingOpen);
        Assert.True(vm.NoDevices);
        Assert.Empty(vm.Devices);
        Assert.False((await rig.Host.GetSettingsAsync()).Remote.Enabled);
    }

    [Fact]
    public async Task TheRelayStatusLineFollowsTheSwitches()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.True(vm.RemoteRelay); // on by default
        Assert.Equal("", vm.RelayStatusText); // nothing to say while other devices are off

        vm.RemoteEnabled = true;
        await vm.RefreshRemoteAsync();
        Assert.Equal("Vm_RelayAway", vm.RelayStatusText);
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.Relay);

        vm.RemoteRelay = false;
        Assert.False((await rig.Host.GetSettingsAsync()).Remote.Relay);
        await vm.RefreshRemoteAsync();
        Assert.Equal("Vm_RelayHomeOnly", vm.RelayStatusText);
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.Enabled); // the other setting is kept
    }

    [Fact]
    public async Task TurningItOnSavesTheSettingAndKeepsItWhenOtherSettingsChange()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RemoteEnabled = true;
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.Enabled);
        Assert.Equal(RemoteApi.DefaultPort, (await rig.Host.GetSettingsAsync()).Remote.Port);

        vm.IdleIndex = SettingsViewModel.FiveMinutesIndex;
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.Enabled);

        vm.RemoteEnabled = false;
        Assert.False((await rig.Host.GetSettingsAsync()).Remote.Enabled);
    }

    [Fact]
    public async Task PairingNeedsTheSwitchOnThenShowsTheCodeAndTheQrText()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        await vm.PairDeviceCommand.ExecuteAsync(null);
        Assert.False(vm.PairingOpen);
        Assert.True(vm.HasMessage);
        Assert.Equal(SettingsViewModel.MessageError, vm.MessageKind);

        vm.RemoteEnabled = true;
        await vm.PairDeviceCommand.ExecuteAsync(null);
        Assert.True(vm.PairingOpen);
        Assert.Equal("ABCD-EFGH-JKMN", vm.PairingCode);
        Assert.StartsWith("portalai://pair?", vm.PairingQrText);
        Assert.Contains("c=ABCDEFGHJKMN", vm.PairingQrText);

        await vm.CancelPairingCommand.ExecuteAsync(null);
        Assert.False(vm.PairingOpen);
        Assert.Equal("", vm.PairingCode);
    }

    [Fact]
    public async Task APairedDeviceAppearsAndTheCodeCloses()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RemoteEnabled = true;
        await vm.PairDeviceCommand.ExecuteAsync(null);

        rig.Host.CompletePairing("Kitchen tablet");
        await Task.Delay(50);

        Assert.False(vm.PairingOpen);
        Assert.True(vm.HasDevices);
        Assert.False(vm.NoDevices);
        var row = Assert.Single(vm.Devices);
        Assert.Equal("Kitchen tablet", row.Name);
        Assert.Equal("Vm_RemoteNeverSeen", row.LastSeenText);
    }

    [Fact]
    public async Task RemovingADeviceAsksFirst()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RemoteEnabled = true;
        rig.Host.CompletePairing("Phone");
        await Task.Delay(50);
        var id = Assert.Single(vm.Devices).DeviceId;

        rig.Dialogs.Answer = false;
        await vm.RemoveDeviceCommand.ExecuteAsync(id);
        Assert.Single(vm.Devices);
        Assert.Equal(1, rig.Dialogs.Asked);

        rig.Dialogs.Answer = true;
        await vm.RemoveDeviceCommand.ExecuteAsync(id);
        Assert.Empty(vm.Devices);
        Assert.True(vm.NoDevices);
    }

    [Fact]
    public async Task ARequestFromADeviceIsNamedWithTheDevice()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        var pending = new PendingApproval { RequestId = "ar_1", AppId = "x", AppName = "AI Portal", DeviceName = "Kitchen tablet" };
        Assert.Equal("Vm_AppOnDevice:AI Portal|Kitchen tablet", ApprovalText.DisplayName(rig.Strings, pending));
        var local = new PendingApproval { RequestId = "ar_2", AppId = "x", AppName = "Voice Portal" };
        Assert.Equal("Voice Portal", ApprovalText.DisplayName(rig.Strings, local));
        await Task.CompletedTask;
    }

    [Fact]
    public void FingerprintIsShownInGroups()
    {
        Assert.Equal("AbCd EfGh IjKl", SettingsViewModel.Short("AbCdEfGhIjKlMnOpQrStUv"));
        Assert.Equal("short", SettingsViewModel.Short("short"));
    }

    [Fact]
    public async Task ModelControlIsOffByDefaultAndTheSwitchReachesTheHost()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RemoteEnabled = true;
        rig.Host.CompletePairing("Laptop");
        await Task.Delay(50);
        var row = Assert.Single(vm.Devices);
        Assert.False(row.ModelControl);
        Assert.False(row.HasLoaded);

        await vm.SetModelControlAsync(row.DeviceId, true);
        await Task.Delay(50);
        Assert.True(Assert.Single(vm.Devices).ModelControl);
        Assert.True(Assert.Single((await rig.Host.GetRemoteAsync()).Devices).ModelControl);

        await vm.SetModelControlAsync(row.DeviceId, false); // revoke
        await Task.Delay(50);
        Assert.False(Assert.Single(vm.Devices).ModelControl);
    }

    [Fact]
    public async Task LoadedByShowsTheDeviceAndTakeBackClearsIt()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RemoteEnabled = true;
        rig.Host.CompletePairing("Laptop");
        var id = Assert.Single(await rig.Host.GetRemoteAsync().ContinueWith(t => t.Result.Devices)).DeviceId;
        rig.Host.LoadedModels.Add(new LoadedModelInfo { Kind = "chat", ModelId = "alpha", DisplayName = "Alpha", DeviceId = id, DeviceName = "Laptop", Placement = "gpu" });
        await vm.RefreshRemoteAsync();
        await Task.Delay(50);

        var row = Assert.Single(vm.Devices);
        Assert.True(row.HasLoaded);
        Assert.Equal("Vm_LoadedBy:Laptop|Alpha|Vm_PlacementGpu", row.LoadedByText);

        await vm.TakeBackCommand.ExecuteAsync(row.TakeBackKind);
        await Task.Delay(50);
        Assert.Equal(1, rig.Host.TakeBacks);
        Assert.False(Assert.Single(vm.Devices).HasLoaded);
    }

    [Fact]
    public async Task DevicesMayReplaceModelsIsOffByDefaultAndIsSaved()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.False(vm.AllowReplaceModels);
        vm.RemoteEnabled = true;
        vm.AllowReplaceModels = true;
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.AllowReplaceModels);
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.Enabled); // the other settings are kept
        vm.AllowReplaceModels = false;
        Assert.False((await rig.Host.GetSettingsAsync()).Remote.AllowReplaceModels);
    }
}
