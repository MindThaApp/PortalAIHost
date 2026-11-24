using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class NotificationSettingsTests
{
    private static async Task<(Rig Rig, SettingsViewModel Vm)> StartAsync()
    {
        var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task Every_notification_starts_on_and_nothing_is_saved_as_muted()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.True(vm.NotifyDownloads);
        Assert.True(vm.NotifyApprovals);
        Assert.True(vm.NotifyProblems);
        Assert.Null((await rig.Host.GetSettingsAsync()).MutedNotifications);
    }

    [Fact]
    public async Task Turning_one_off_saves_only_that_kind_and_back_on_clears_it()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.NotifyDownloads = false;
        var saved = await rig.Host.GetSettingsAsync();
        Assert.Equal([NotificationKinds.Downloads], saved.MutedNotifications);
        vm.NotifyDownloads = true;
        Assert.Null((await rig.Host.GetSettingsAsync()).MutedNotifications);
    }

    [Fact]
    public async Task Reset_turns_all_back_on()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.NotifyApprovals = false;
        vm.NotifyProblems = false;
        vm.ResetNotificationsCommand.Execute(null);
        Assert.True(vm.NotifyApprovals && vm.NotifyProblems && vm.NotifyDownloads);
        Assert.Null((await rig.Host.GetSettingsAsync()).MutedNotifications);
    }

    [Fact]
    public async Task Other_settings_keep_the_muted_choice()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.NotifyProblems = false;
        vm.IdleIndex = SettingsViewModel.FiveMinutesIndex;
        Assert.Equal([NotificationKinds.Problems], (await rig.Host.GetSettingsAsync()).MutedNotifications);
    }
}
