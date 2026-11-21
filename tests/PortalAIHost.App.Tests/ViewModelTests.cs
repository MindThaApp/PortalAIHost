using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class ShellTests
{
    [Fact]
    public async Task No_host_tries_to_start_it_and_shows_a_message_when_that_fails()
    {
        using var rig = new Rig(FakeScenario.NoHost, launcherSucceeds: false);
        await rig.Shell.InitializeAsync();
        Assert.False(rig.Shell.Connected);
        Assert.Equal(1, rig.Launcher.Calls);
        Assert.Equal("Vm_StartFailed", rig.Shell.StartMessage);
    }

    [Fact]
    public async Task Starting_the_host_connects_and_clears_the_message()
    {
        using var rig = new Rig(FakeScenario.NoHost, launcherSucceeds: true);
        await rig.Shell.InitializeAsync();
        Assert.True(rig.Shell.Connected);
        Assert.Equal("", rig.Shell.StartMessage);
    }

    [Fact]
    public async Task First_run_shows_only_when_there_is_no_model_and_it_was_not_done()
    {
        using var fresh = new Rig(FakeScenario.Fresh);
        await fresh.Shell.InitializeAsync();
        Assert.True(fresh.Shell.ShowFirstRun);
        fresh.Shell.CompleteFirstRun();
        Assert.False(fresh.Shell.ShowFirstRun);
        Assert.True(fresh.State.FirstRunDone);

        using var loaded = new Rig(FakeScenario.Loaded);
        await loaded.Shell.InitializeAsync();
        Assert.False(loaded.Shell.ShowFirstRun);
    }

    [Fact]
    public async Task Approval_prompts_come_one_at_a_time_and_answers_reach_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var answers = new Queue<bool?>(new bool?[] { true, false, null });
        var shown = new List<string>();
        rig.Shell.ApprovalPresenter = a => { shown.Add(a.RequestId); return Task.FromResult(answers.Dequeue()); };
        await rig.Shell.InitializeAsync();
        var before = (await rig.Host.ListClientsAsync()).Count;
        var a1 = rig.Host.RaiseApproval();
        var a2 = rig.Host.RaiseApproval();
        var a3 = rig.Host.RaiseApproval();
        await rig.Shell.PumpApprovalsAsync();
        Assert.Equal(new[] { a1.RequestId, a2.RequestId, a3.RequestId }, shown);
        Assert.Equal(before + 1, (await rig.Host.ListClientsAsync()).Count);
        var left = await rig.Host.ListApprovalsAsync();
        Assert.Single(left); // the one closed without an answer is still waiting in the host
        Assert.Equal(a3.RequestId, left[0].RequestId);
    }

    [Fact]
    public async Task Safe_mode_is_reported()
    {
        using var rig = new Rig(FakeScenario.SafeMode);
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.True(home.SafeMode);
        Assert.Equal("Vm_StateIdle", home.StatusTitle);
    }
}

public class HomeTests
{
    [Fact]
    public async Task Loaded_model_and_queue_are_described_in_plain_words()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.Equal("Vm_StateReady", home.StatusTitle);
        Assert.Equal("Vm_HomeLoaded", home.ModelLine);
        Assert.Equal("Vm_QueueSome:1|2", home.QueueLine);
        Assert.False(home.NoHost);
    }

    private static async Task<(Rig rig, HomeViewModel home)> Open(FakeScenario scenario)
    {
        var rig = new Rig(scenario);
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await home.LoadAsync();
        return (rig, home);
    }

    [Fact]
    public async Task Page_opened_before_the_host_answers_fills_in_once_connected()
    {
        using var rig = new Rig(FakeScenario.NoHost);
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await home.LoadAsync();
        Assert.False(home.HasRecommendation);
        rig.Host.HostStarted();
        await rig.Shell.RefreshAsync();
        await Task.Delay(100);
        Assert.True(home.ShowOnboarding);
        Assert.True(home.HasRecommendation);
    }

    [Fact]
    public async Task No_model_shows_the_recommended_model_and_a_download_button()
    {
        var (rig, home) = await Open(FakeScenario.Fresh);
        using var _ = rig;
        Assert.True(home.ShowOnboarding);
        Assert.False(home.ShowInstalled);
        Assert.True(home.HasRecommendation);
        Assert.Equal("14B model (medium)", home.RecName);
        Assert.Equal("8.4 GB", home.RecSize);
        Assert.Contains("Best for your PC", home.RecReason);
        Assert.False(home.CatalogueUnavailable);
        Assert.False(home.NotEnoughSpace);
        Assert.True(home.CanDownload);
        Assert.False(home.IsDownloading);
    }

    [Fact]
    public async Task Download_button_starts_the_download_and_progress_reaches_the_panel()
    {
        var (rig, home) = await Open(FakeScenario.Fresh);
        using var _ = rig;
        await home.DownloadCommand.ExecuteAsync(null);
        Assert.True(home.IsDownloading);
        Assert.False(home.CanDownload);
        rig.Host.Tick();
        Assert.True(home.Percent > 0);
        Assert.StartsWith("Vm_Download", home.ProgressText);
        for (var i = 0; i < 25; i++) rig.Host.Tick();
        // The host reports the model as installed: the panel gives way to the status card.
        await rig.Shell.RefreshAsync();
        Assert.True(home.ShowInstalled);
        Assert.False(home.ShowOnboarding);
    }

    [Fact]
    public async Task A_running_download_is_shown_when_the_page_opens()
    {
        var (rig, home) = await Open(FakeScenario.Downloading);
        using var _ = rig;
        Assert.True(home.ShowOnboarding);
        Assert.True(home.IsDownloading);
        Assert.False(home.CanDownload);
        Assert.InRange(home.Percent, 34, 36);
    }

    [Fact]
    public async Task Unavailable_catalogue_hides_the_recommendation()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        rig.Host.CatalogueAvailable = false;
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await home.LoadAsync();
        Assert.True(home.CatalogueUnavailable);
        Assert.False(home.HasRecommendation);
        Assert.False(home.CanDownload);
        rig.Host.CatalogueAvailable = true;
        await home.TryAgainCommand.ExecuteAsync(null);
        Assert.False(home.CatalogueUnavailable);
        Assert.True(home.HasRecommendation);
        Assert.True(home.CanDownload);
    }

    [Fact]
    public async Task Not_enough_disk_space_blocks_the_download_and_says_why()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        rig.Host.FreeBytes = 1_000_000_000;
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await home.LoadAsync();
        Assert.True(home.NotEnoughSpace);
        Assert.False(home.CanDownload);
        Assert.StartsWith("Vm_HomeNoSpace:8.4 GB|", home.SpaceLine);
        await home.DownloadCommand.ExecuteAsync(null);
        Assert.False(home.IsDownloading);
    }

    [Fact]
    public async Task Installed_model_shows_the_status_card_with_apps_and_idle_info()
    {
        var (rig, home) = await Open(FakeScenario.Loaded);
        using var _ = rig;
        Assert.True(home.ShowInstalled);
        Assert.False(home.ShowOnboarding);
        Assert.Equal("14B model (medium)", home.ModelName);
        Assert.Equal("Vm_HomeIdleUnload:10", home.IdleLine);
        Assert.Equal("Vm_HomeApps:2", home.AppsLine);
        Assert.True(home.UpgradeAvailable);
    }

    [Fact]
    public async Task Installed_but_not_loaded_says_it_loads_on_demand()
    {
        var (rig, home) = await Open(FakeScenario.SafeMode);
        using var _ = rig;
        Assert.Equal("Vm_HomeUnloaded", home.ModelLine);
        Assert.True(home.SafeMode);
    }

    [Fact]
    public async Task Missing_host_offers_the_start_button()
    {
        using var rig = new Rig(FakeScenario.NoHost);
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.True(home.NoHost);
        Assert.True(home.CanStart);
        await home.StartHostCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Launcher.Calls);
    }
}

public class ModelsTests
{
    private static ModelsViewModel Make(Rig rig) => new(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);

    [Fact]
    public async Task Recommended_model_and_existing_copy_are_shown()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = Make(rig);
        await vm.LoadAsync();
        Assert.Equal("14B model (medium)", vm.Recommended?.Name);
        Assert.StartsWith("Vm_ExistingCopy:", vm.ExistingCopyLine);
        Assert.True(vm.HasExisting);
        Assert.StartsWith("Vm_StorageLine:", vm.StorageLine);
    }

    [Fact]
    public async Task Download_shows_progress_and_finishes_as_installed()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = Make(rig);
        await vm.LoadAsync();
        var row = vm.Models.First(m => m.Id == "chat-8b");
        await row.DownloadCommand.ExecuteAsync(null);
        rig.Host.Tick();
        Assert.True(row.ShowProgress);
        Assert.InRange(row.Percent, 1, 99);
        Assert.True(row.CanPause);
        await row.PauseCommand.ExecuteAsync(null);
        rig.Host.Tick();
        Assert.True(row.CanResume);
        await row.ResumeCommand.ExecuteAsync(null);
        for (var i = 0; i < 30; i++) rig.Host.Tick();
        Assert.True(row.Installed);
        Assert.False(row.ShowProgress);
    }

    [Fact]
    public async Task Cancelling_a_download_needs_a_yes_first()
    {
        using var rig = new Rig(FakeScenario.Downloading);
        var vm = Make(rig);
        await vm.LoadAsync();
        var row = vm.Models.First(m => m.Id == "chat-14b");
        rig.Host.Tick();
        rig.Dialogs.Answer = false;
        await row.CancelCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Dialogs.Asked);
        rig.Host.Tick();
        Assert.True(row.ShowProgress); // still going

        rig.Dialogs.Answer = true;
        await row.CancelCommand.ExecuteAsync(null);
        Assert.False(row.ShowProgress);
        rig.Host.Tick();
        Assert.False(row.ShowProgress);
    }

    [Fact]
    public async Task Models_that_do_not_fit_are_flagged_and_upgrade_is_offered()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        Assert.True(vm.Models.First(m => m.Id == "qwen3-30b").TooBig);
        Assert.StartsWith("Vm_UpgradeOffer:", vm.UpgradeLine);
        Assert.Equal("Vm_InUse", vm.Models.First(m => m.Id == "chat-14b").Badge);
    }
}

public class AppsTests
{
    [Fact]
    public async Task Revoking_asks_first_and_only_removes_on_yes()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Clients.Count);
        rig.Dialogs.Answer = false;
        await vm.Clients[0].RevokeCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Clients.Count);
        rig.Dialogs.Answer = true;
        await vm.Clients[0].RevokeCommand.ExecuteAsync(null);
        Assert.Single(vm.Clients);
    }

    [Fact]
    public async Task Pending_requests_can_be_allowed_from_the_list()
    {
        using var rig = new Rig(FakeScenario.ApprovalPending);
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        Assert.True(vm.HasPending);
        Assert.Contains("Vm_Verified", vm.Pending[0].IdentityText);
        await vm.Pending[0].AllowCommand.ExecuteAsync(null);
        Assert.False(vm.HasPending);
        Assert.Equal(3, vm.Clients.Count);
    }

    [Fact]
    public void Unverified_callers_get_a_warning_text()
    {
        var s = new KeyStrings();
        var text = ApprovalText.Identity(s, new PendingApproval { CallerIdentity = @"C:\x\a.exe", CallerVerified = false });
        Assert.StartsWith("Vm_Unverified:", text);
    }
}

public class SettingsTests
{
    [Fact]
    public async Task Changes_keep_the_settings_the_page_does_not_show()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await rig.Host.SetSettingsAsync(new HostSettings { Backend = "cpu", MaxMemoryPercent = 55, ModelsFolder = @"D:\models", Kinds = new() { [ModelKinds.Chat] = new KindSettings { IdleUnloadSeconds = 600 } } });
        await vm.LoadAsync();
        Assert.Equal(SettingsViewModel.TenMinutesIndex, vm.IdleIndex);
        vm.IdleIndex = SettingsViewModel.FiveMinutesIndex;
        vm.StartWithWindows = true;
        var saved = await rig.Host.GetSettingsAsync();
        Assert.Equal("cpu", saved.Backend);
        Assert.Equal(55, saved.MaxMemoryPercent);
        Assert.Equal(@"D:\models", saved.ModelsFolder);
        Assert.True(saved.StartWithWindows);
        Assert.Equal(300, saved.Kinds[ModelKinds.Chat].IdleUnloadSeconds);
        Assert.False(saved.Kinds[ModelKinds.Chat].KeepLoaded);
    }

    [Fact]
    public async Task Keep_loaded_choice_is_saved()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        vm.IdleIndex = SettingsViewModel.KeepLoadedIndex;
        Assert.True((await rig.Host.GetSettingsAsync()).Kinds[ModelKinds.Chat].KeepLoaded);
    }

    [Fact]
    public async Task Folder_change_needs_a_yes_and_is_only_a_request_to_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        var original = (await rig.Host.GetSettingsAsync()).ModelsFolder;
        vm.ModelsFolder = @"E:\new";
        rig.Dialogs.Answer = false;
        await vm.ApplyFolderCommand.ExecuteAsync(null);
        Assert.Equal(original, (await rig.Host.GetSettingsAsync()).ModelsFolder);
        Assert.Equal(original, vm.ModelsFolder);
        vm.ModelsFolder = @"E:\new";
        rig.Dialogs.Answer = true;
        await vm.ApplyFolderCommand.ExecuteAsync(null);
        Assert.Equal(@"E:\new", (await rig.Host.GetSettingsAsync()).ModelsFolder);
    }
}

public class TryItTests
{
    [Fact]
    public async Task Reply_streams_in_and_send_is_disabled_for_empty_text()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Prompt = "hello";
        Assert.True(vm.SendCommand.CanExecute(null));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("You asked: hello", vm.Reply);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Missing_host_shows_a_message_not_a_crash()
    {
        using var rig = new Rig(FakeScenario.NoHost);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "hi" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("Vm_NoHostTitle", vm.Error);
    }
}

public class FirstRunTests
{
    [Fact]
    public async Task Flow_goes_check_model_download_startup_and_finishes()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        await rig.Shell.InitializeAsync();
        using var vm = new FirstRunViewModel(rig.Shell, rig.Ui, rig.Strings);
        await vm.LoadAsync();
        Assert.True(vm.OnCheck);
        Assert.StartsWith("Vm_HwMemory:", vm.MemoryLine);
        vm.NextCommand.Execute(null);
        Assert.True(vm.OnModel);
        Assert.Equal("14B model (medium)", vm.ModelName);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.OnDownload);
        for (var i = 0; i < 30; i++) rig.Host.Tick();
        Assert.True(vm.OnStartup);
        vm.StartWithWindows = true;
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.False(rig.Shell.ShowFirstRun);
        Assert.True(rig.State.FirstRunDone);
        Assert.True((await rig.Host.GetSettingsAsync()).StartWithWindows);
    }
}
