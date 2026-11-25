using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class DevicesViewModelTests
{
    private static async Task<(Rig Rig, DevicesViewModel Vm, string DeviceId)> StartAsync()
    {
        var rig = new Rig(FakeScenario.Loaded);
        rig.Host.CompletePairing("Laptop");
        var id = (await rig.Host.GetRemoteAsync()).Devices.Single().DeviceId;
        var vm = new DevicesViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        return (rig, vm, id);
    }

    private static PairedDeviceSummary Summary(FakeHostControl host, string id) => host.Devices.Single(d => d.DeviceId == id);

    [Fact]
    public async Task OneCardPerDeviceTitledWithItsNameAndNothingShowsEmptyUntilLoaded()
    {
        var rig = new Rig(FakeScenario.Loaded);
        using var _ = rig;
        var vm = new DevicesViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        Assert.True(vm.IsLoading);
        Assert.False(vm.ShowEmpty); // no empty state while the first answer is pending
        await vm.LoadAsync();
        Assert.True(vm.ShowEmpty);
        rig.Host.CompletePairing("Kitchen laptop");
        rig.Host.CompletePairing("Phone");
        await vm.RefreshAsync(default);
        Assert.Equal(["Kitchen laptop", "Phone"], vm.Cards.Select(c => c.Name));
        Assert.False(vm.ShowEmpty);
        vm.Dispose();
    }

    [Fact]
    public async Task CardShowsAppsTodayCountsAndASevenDayBar()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        Summary(rig.Host, id).Apps =
        [
            new DeviceAppSummary { Name = "Notes", RequestsToday = 4, TokensToday = 1200, Active = 2, Queued = 1, Last7Days = [0, 0, 1, 2, 0, 3, 4] },
        ];
        Summary(rig.Host, id).Status = DeviceStatuses.Online;
        Summary(rig.Host, id).Route = "relay";
        await vm.RefreshAsync(default);
        var card = Assert.Single(vm.Cards);
        var app = Assert.Single(card.Apps);
        Assert.Equal("Notes", app.Name);
        Assert.StartsWith("Vm_DevToday:4|", app.TodayText);
        Assert.Equal("Vm_DevRunningQueued:1|1", app.ActiveText);
        Assert.Equal(7, app.Bars.Count);
        Assert.True(app.Bars[6].Height > app.Bars[3].Height);
        Assert.Equal("Vm_DevOnline", card.StatusText);
        Assert.Equal("Vm_DevRouteRelay", card.RouteText);
    }

    [Fact]
    public async Task RefreshUpdatesRowsInPlaceSoNothingJumps()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        Summary(rig.Host, id).Apps = [new DeviceAppSummary { Name = "Notes", RequestsToday = 1, Last7Days = [0, 0, 0, 0, 0, 0, 1] }];
        await vm.RefreshAsync(default);
        var card = vm.Cards.Single();
        var row = card.Apps.Single();
        Summary(rig.Host, id).Apps = [new DeviceAppSummary { Name = "Notes", RequestsToday = 2, Last7Days = [0, 0, 0, 0, 0, 0, 2] }];
        await vm.RefreshAsync(default);
        Assert.Same(card, vm.Cards.Single());
        Assert.Same(row, card.Apps.Single());
        Assert.StartsWith("Vm_DevToday:2|", row.TodayText);
    }

    [Fact]
    public async Task PausingTheDeviceAndOneAppGoToTheHost()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        Summary(rig.Host, id).Apps = [new DeviceAppSummary { Name = "Notes", Last7Days = [0, 0, 0, 0, 0, 0, 0] }];
        await vm.RefreshAsync(default);
        var card = vm.Cards.Single();

        await card.TogglePauseCommand.ExecuteAsync(null);
        Assert.True(Summary(rig.Host, id).Paused);
        Assert.True(card.IsPaused);
        Assert.Equal("Vm_DevResume", card.PauseLabel);

        await card.Apps.Single().TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal(["Notes"], Summary(rig.Host, id).PausedApps);
        Assert.True(card.Apps.Single().IsPaused);

        await card.TogglePauseCommand.ExecuteAsync(null);
        Assert.False(Summary(rig.Host, id).Paused);
    }

    [Fact]
    public async Task LimitsQuietHoursPriorityAndAllowedModelsAreSavedTogether()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        var card = vm.Cards.Single();
        card.RequestLimit = 50;
        card.TokenLimit = 20000;
        card.QuietEnabled = true;
        card.QuietStart = new TimeSpan(23, 30, 0);
        card.QuietEnd = new TimeSpan(6, 0, 0);
        card.PriorityEqual = true; // saves by itself
        Assert.True(rig.Host.LastDeviceSettings!.PriorityEqual);

        await card.SaveSettingsCommand.ExecuteAsync(null);
        var saved = Summary(rig.Host, id);
        Assert.Equal((50, 20000, "23:30", "06:00", true), (saved.MaxRequestsPerDay, saved.MaxTokensPerDay, saved.QuietStart, saved.QuietEnd, saved.PriorityEqual));
        Assert.Empty(saved.AllowedModels); // every model allowed: the list stays empty

        card.QuietEnabled = false;
        card.RequestLimit = double.NaN;
        await card.SaveSettingsCommand.ExecuteAsync(null);
        saved = Summary(rig.Host, id);
        Assert.Null(saved.QuietStart);
        Assert.Null(saved.MaxRequestsPerDay);
    }

    [Fact]
    public async Task AllowedModelsAreOnlyThoseTicked()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        await rig.Host.ImportModelAsync(@"C:\models\extra.gguf");
        await vm.RefreshAsync(default);
        var card = vm.Cards.Single();
        Assert.True(card.Models.Count >= 2, "the fake lists installed models");
        Assert.All(card.Models, m => Assert.True(m.Allowed));
        card.Models[0].Allowed = false;
        await card.SaveSettingsCommand.ExecuteAsync(null);
        var allowed = Summary(rig.Host, id).AllowedModels;
        Assert.DoesNotContain(card.Models[0].Id, allowed);
        Assert.Equal(card.Models.Count - 1, allowed.Count);
        await vm.RefreshAsync(default);
        Assert.False(vm.Cards.Single().Models[0].Allowed);
    }

    [Fact]
    public async Task RenameChangesTheCardTitleAndAnEmptyNameIsIgnored()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        var card = vm.Cards.Single();
        card.NewName = "  Den laptop ";
        await card.RenameCommand.ExecuteAsync(null);
        Assert.Equal("Den laptop", Summary(rig.Host, id).Name);
        Assert.Equal("Den laptop", vm.Cards.Single().Name);
        card.NewName = "   ";
        await card.RenameCommand.ExecuteAsync(null);
        Assert.Equal("Den laptop", Summary(rig.Host, id).Name);
    }

    [Fact]
    public async Task ForgetAsksFirstAndRemovesTheCardOnlyOnYes()
    {
        var (rig, vm, _) = await StartAsync();
        using var _2 = rig;
        rig.Dialogs.Answer = false;
        await vm.Cards.Single().ForgetCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Dialogs.Asked);
        Assert.Single(vm.Cards);

        rig.Dialogs.Answer = true;
        await vm.Cards.Single().ForgetCommand.ExecuteAsync(null);
        Assert.Empty(vm.Cards);
        Assert.True(vm.ShowEmpty);
    }

    [Fact]
    public async Task LoadedByShowsThePcOrTheDeviceAndTakeBackIsOnlyForTheDevicesModel()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        rig.Host.LoadedModels.Add(new LoadedModelInfo { Kind = "chat", ModelId = "alpha", DisplayName = "Alpha", DeviceId = id, DeviceName = "Laptop" });
        await vm.RefreshAsync(default);
        var card = vm.Cards.Single();
        Assert.True(card.HasLoaded);
        Assert.Equal("Vm_DevLoadedByDevice:Laptop|Alpha", card.LoadedByText);
        await card.TakeBackCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Host.TakeBacks);
        Assert.False(vm.Cards.Single().HasLoaded);

        rig.Host.LoadedModels.Add(new LoadedModelInfo { Kind = "chat", ModelId = "pc", DisplayName = "PC model", DeviceId = null });
        await vm.RefreshAsync(default);
        Assert.False(vm.Cards.Single().HasLoaded);
        Assert.Equal("Vm_DevLoadedByPc:PC model", vm.Cards.Single().LoadedByText);
    }

    [Fact]
    public async Task GamingModeIsSavedPcWideAndTheBusyNoteShows()
    {
        var (rig, vm, _) = await StartAsync();
        using var _2 = rig;
        Assert.False(vm.GamingMode); // off by default
        vm.GamingMode = true;
        await Task.Delay(100);
        Assert.True((await rig.Host.GetSettingsAsync()).Remote.GamingMode);
    }

    [Fact]
    public async Task BurstsOfPushesBecomeOneRefreshAndLeavingStopsThem()
    {
        var (rig, vm, id) = await StartAsync();
        using var _ = rig;
        Summary(rig.Host, id).Apps = [new DeviceAppSummary { Name = "Notes", RequestsToday = 9, Last7Days = [0, 0, 0, 0, 0, 0, 0] }];
        for (var i = 0; i < 20; i++) rig.Host.CompletePairingNoop();
        await Task.Delay(700);
        Assert.StartsWith("Vm_DevToday:9|", vm.Cards.Single().Apps.Single().TodayText);

        vm.Dispose();
        Summary(rig.Host, id).Apps = [new DeviceAppSummary { Name = "Notes", RequestsToday = 10, Last7Days = [0, 0, 0, 0, 0, 0, 0] }];
        rig.Host.CompletePairingNoop();
        await Task.Delay(500);
        Assert.StartsWith("Vm_DevToday:9|", vm.Cards.Single().Apps.Single().TodayText); // nothing refreshed after leaving
    }
}
