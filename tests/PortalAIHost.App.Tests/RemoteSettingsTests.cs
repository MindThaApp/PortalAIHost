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
        Assert.Equal("123456", vm.PairingCode);
        Assert.StartsWith("portalai://pair?", vm.PairingQrText);
        Assert.Contains("c=123456", vm.PairingQrText);

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
}
