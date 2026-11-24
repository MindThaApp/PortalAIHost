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
        Assert.Contains("Runs well on your graphics card", home.RecReason);
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
    public async Task The_remove_question_says_how_much_space_it_frees()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        var row = vm.Models.First(m => m.Id == "chat-14b");
        Assert.True(row.SizeBytes > 0);
        Assert.Equal("Vm_RemoveFrees:" + Fmt.Bytes(row.SizeBytes), row.FreesText);
        Assert.Contains("Vm_RemoveActiveBody:", row.RemoveBody); // the model in use: it is unloaded first
        Assert.EndsWith(row.FreesText, row.RemoveBody);
    }

    [Fact]
    public void A_model_added_in_place_frees_nothing_because_its_file_stays()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        var summary = new CatalogueModelSummary { Id = "mine", DisplayName = "Mine", SizeBytes = 4_000_000_000, Installed = true };
        var row = new ModelRowViewModel(vm, rig.Strings, summary, existingCopy: true);
        Assert.Equal("", row.FreesText);
        Assert.Equal("Vm_RemoveBody:Mine", row.RemoveBody);
        var downloaded = new ModelRowViewModel(vm, rig.Strings, summary, existingCopy: false);
        Assert.NotEqual("", downloaded.FreesText);
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
    /// <summary>A delay the test releases by hand, so the undo time never depends on a clock.</summary>
    private sealed class HandDelay
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Wait(TimeSpan _, CancellationToken ct)
        {
            ct.Register(() => _gate.TrySetCanceled(ct));
            return _gate.Task;
        }
        public void Elapse() => _gate.TrySetResult();
    }

    private static async Task<(AppsViewModel vm, HandDelay timer, Task revoke)> RevokeFirstAsync(Rig rig)
    {
        var timer = new HandDelay();
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, timer.Wait);
        await vm.LoadAsync();
        var revoke = vm.Clients[0].RevokeCommand.ExecuteAsync(null);
        return (vm, timer, revoke);
    }

    [Fact]
    public async Task Revoking_hides_the_app_at_once_without_asking_and_without_telling_the_host_yet()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _, _) = await RevokeFirstAsync(rig);
        Assert.Equal(0, rig.Dialogs.Asked);
        Assert.Single(vm.Clients);
        Assert.True(vm.HasUndo);
        Assert.StartsWith("Vm_RevokedBar:", vm.RevokedLine);
        Assert.Equal(2, (await rig.Host.ListClientsAsync()).Count); // still allowed until the undo time ends
    }

    [Fact]
    public async Task The_host_revokes_when_the_undo_time_ends()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, timer, revoke) = await RevokeFirstAsync(rig);
        timer.Elapse();
        await revoke;
        Assert.Single(await rig.Host.ListClientsAsync());
        Assert.Single(vm.Clients);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task Undo_brings_the_app_back_in_place_and_the_host_is_never_asked()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var name = (await rig.Host.ListClientsAsync())[0].AppName;
        var (vm, timer, revoke) = await RevokeFirstAsync(rig);
        vm.UndoCommand.Execute(null);
        await revoke; // the waiting call ends quietly
        timer.Elapse();
        Assert.Equal(2, vm.Clients.Count);
        Assert.Equal(name, vm.Clients[0].Name);
        Assert.False(vm.HasUndo);
        Assert.Equal(2, (await rig.Host.ListClientsAsync()).Count);
    }

    [Fact]
    public async Task Closing_the_page_or_window_sends_a_removal_that_is_still_waiting()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _, _) = await RevokeFirstAsync(rig);
        await rig.Shell.FlushAsync();
        Assert.Single(await rig.Host.ListClientsAsync());
        Assert.False(vm.HasUndo);
        vm.UndoCommand.Execute(null); // too late: nothing comes back that the host has revoked
        Assert.Single(vm.Clients);
    }

    [Fact]
    public async Task A_second_removal_makes_the_first_final()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var timer = new HandDelay();
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, timer.Wait);
        await vm.LoadAsync();
        var first = vm.Clients[0];
        _ = first.RevokeCommand.ExecuteAsync(null);
        _ = vm.Clients[0].RevokeCommand.ExecuteAsync(null);
        Assert.Empty(vm.Clients);
        Assert.Single(await rig.Host.ListClientsAsync()); // the first is gone for good, the second still waits
        await rig.Shell.FlushAsync();
        Assert.Empty(await rig.Host.ListClientsAsync());
    }

    [Fact]
    public async Task A_page_reload_does_not_show_an_app_that_is_waiting_to_be_removed()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _, _) = await RevokeFirstAsync(rig);
        await vm.LoadAsync();
        Assert.Single(vm.Clients);
    }

    [Fact]
    public async Task Rows_show_the_paused_app_and_the_priority_from_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        var voice = vm.Clients.Single(c => c.AppId == "voice-portal");
        var pdf = vm.Clients.Single(c => c.AppId == "pdf-portal");
        Assert.False(voice.IsPaused);
        Assert.Equal(0, voice.PriorityIndex); // High
        Assert.True(pdf.IsPaused);
        Assert.Equal(1, pdf.PriorityIndex); // Normal
        Assert.StartsWith("Vm_AppResume", pdf.PauseLabel);
        Assert.StartsWith("Vm_AppPause", voice.PauseLabel);
    }

    [Fact]
    public async Task Pause_and_priority_changes_reach_the_host_once()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new AppsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        var voice = vm.Clients.Single(c => c.AppId == "voice-portal");
        await voice.TogglePauseCommand.ExecuteAsync(null);
        voice.PriorityIndex = 2;
        await Task.Delay(50);
        var host = (FakeHostControl)rig.Host;
        Assert.Contains(host.ControlCalls, c => c is ("voice-portal", true, null));
        Assert.Contains(host.ControlCalls, c => c is ("voice-portal", null, "low"));
        Assert.Equal(2, host.ControlCalls.Count);
        await vm.LoadAsync();
        var again = vm.Clients.Single(c => c.AppId == "voice-portal");
        Assert.True(again.IsPaused);
        Assert.Equal(2, again.PriorityIndex);
        Assert.Equal(2, host.ControlCalls.Count); // loading never sends
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

public class CrashReportChoiceTests
{
    [Fact]
    public void Crash_reports_are_on_by_default()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        Assert.True(rig.State.SendCrashReports);
        Assert.True(new HostSettings().SendCrashReports);
    }

    [Fact]
    public async Task Turning_off_applies_at_once_saves_and_reaches_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var applied = new List<bool>();
        rig.Shell.CrashReportsApplier = applied.Add;
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        Assert.True(vm.SendCrashReports);
        vm.SendCrashReports = false;
        await rig.Shell.SyncCrashReportsAsync();
        Assert.Equal(new[] { false }, applied);
        Assert.False(rig.State.SendCrashReports);
        Assert.False((await rig.Host.GetSettingsAsync()).SendCrashReports);
    }

    [Fact]
    public async Task Other_settings_changes_keep_the_choice()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        await rig.Shell.SetSendCrashReportsAsync(false);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        Assert.False(vm.SendCrashReports);
        vm.IdleIndex = SettingsViewModel.FiveMinutesIndex;
        Assert.False((await rig.Host.GetSettingsAsync()).SendCrashReports);
    }

    [Fact]
    public async Task Welcome_option_is_pre_ticked_and_one_click_turns_it_off()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new FirstRunViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.True(vm.SendCrashReports);
        await vm.SetSendCrashReportsAsync(false);
        Assert.False(vm.SendCrashReports);
        Assert.False(rig.State.SendCrashReports);
    }

    [Fact]
    public async Task Choice_is_synced_to_the_host_on_start()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.State.SaveSendCrashReports(false);
        await rig.Shell.SyncCrashReportsAsync();
        Assert.False((await rig.Host.GetSettingsAsync()).SendCrashReports);
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
    public async Task Folder_change_applies_live_as_a_request_to_the_host_without_asking()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        vm.ModelsFolder = @"E:\new";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        Assert.Equal(0, rig.Dialogs.Asked);
        Assert.Equal(@"E:\new", (await rig.Host.GetSettingsAsync()).ModelsFolder);
        Assert.True(vm.HasMessage);
        Assert.Equal(SettingsViewModel.MessageSuccess, vm.MessageKind);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_full_path_is_rejected_before_it_reaches_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        var original = (await rig.Host.GetSettingsAsync()).ModelsFolder;
        vm.ModelsFolder = "models";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        Assert.Equal(original, (await rig.Host.GetSettingsAsync()).ModelsFolder);
        Assert.Equal("Vm_FolderNotFull", vm.Message);
        Assert.Equal(SettingsViewModel.MessageError, vm.MessageKind);
    }

    [Fact]
    public async Task Unchanged_folder_text_sends_nothing()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        await vm.CommitFolderCommand.ExecuteAsync(null);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public async Task Reset_models_folder_goes_back_to_the_default_folder()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        vm.ModelsFolder = @"E:\new";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        await vm.ResetFolderCommand.ExecuteAsync(null);
        Assert.Equal("", vm.ModelsFolder);
        Assert.Equal("", (await rig.Host.GetSettingsAsync()).ModelsFolder);
    }

    [Fact]
    public async Task Reset_general_restores_idle_time_and_start_with_windows()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        vm.IdleIndex = SettingsViewModel.KeepLoadedIndex;
        vm.StartWithWindows = true;
        vm.ResetGeneralCommand.Execute(null);
        Assert.Equal(SettingsViewModel.TenMinutesIndex, vm.IdleIndex);
        Assert.False(vm.StartWithWindows);
        var saved = await rig.Host.GetSettingsAsync();
        Assert.False(saved.Kinds[ModelKinds.Chat].KeepLoaded);
        Assert.False(saved.StartWithWindows);
    }

    [Fact]
    public void Appearance_choices_apply_and_are_remembered_at_once()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var applier = new RecordingAppearance();
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, null, applier);
        Assert.Empty(applier.Applied);
        vm.ThemeIndex = (int)ThemeChoice.Dark;
        vm.CornersIndex = (int)CornerChoice.Square;
        Assert.Equal(2, applier.Applied.Count);
        Assert.Equal(ThemeChoice.Dark, applier.Applied[^1].Theme);
        Assert.Equal(CornerChoice.Square, applier.Applied[^1].Corners);
        Assert.Equal(CornerChoice.Square, rig.State.Appearance.Corners);
    }

    [Fact]
    public void Reset_appearance_puts_every_choice_back_to_the_defaults()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var applier = new RecordingAppearance();
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, null, applier);
        vm.ThemeIndex = (int)ThemeChoice.Light;
        vm.MaterialIndex = (int)MaterialChoice.Solid;
        vm.DensityIndex = (int)DensityChoice.Compact;
        vm.TextIndex = 2;
        vm.MotionIndex = (int)MotionChoice.Off;
        vm.ResetAppearanceCommand.Execute(null);
        Assert.Equal(AppearancePrefs.Default, applier.Applied[^1]);
        Assert.Equal(AppearancePrefs.Default, rig.State.Appearance);
        Assert.Equal(0, vm.ThemeIndex);
        Assert.Equal(0, vm.TextIndex);
    }

    [Fact]
    public void Saved_appearance_is_shown_when_the_page_opens()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.State.SaveAppearance(new AppearancePrefs { Theme = ThemeChoice.Dark, Density = DensityChoice.Comfortable });
        var applier = new RecordingAppearance();
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, null, applier);
        Assert.Equal((int)ThemeChoice.Dark, vm.ThemeIndex);
        Assert.Equal((int)DensityChoice.Comfortable, vm.DensityIndex);
        Assert.Empty(applier.Applied); // showing saved choices must not re-apply or re-save them
    }
}

public class AppearanceMathTests
{
    [Fact]
    public void Defaults_match_the_design_tokens()
    {
        Assert.Equal(8, AppearanceMath.CardRadius(CornerChoice.Rounded));
        Assert.Equal(16, AppearanceMath.CardPadding(DensityChoice.Standard));
        Assert.Equal(14, AppearanceMath.BodyFontSize(0));
    }

    [Fact]
    public void Reduced_motion_follows_either_the_choice_or_windows()
    {
        Assert.False(AppearanceMath.ReducedMotion(MotionChoice.FollowWindows, true));
        Assert.True(AppearanceMath.ReducedMotion(MotionChoice.FollowWindows, false));
        Assert.True(AppearanceMath.ReducedMotion(MotionChoice.Reduced, true));
        Assert.True(AppearanceMath.ReducedMotion(MotionChoice.Off, true));
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
    private static async Task<(Rig rig, FirstRunViewModel vm)> Open()
    {
        var rig = new Rig(FakeScenario.Fresh);
        await rig.Shell.InitializeAsync();
        var vm = new FirstRunViewModel(rig.Shell, rig.Ui, rig.Strings);
        await vm.LoadAsync();
        return (rig, vm);
    }

    [Fact]
    public async Task Welcome_has_three_steps_and_the_last_one_finishes()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        var closed = 0;
        vm.CloseRequested += () => closed++;
        Assert.True(vm.OnWelcome);
        Assert.False(vm.CanGoBack);
        Assert.Equal("Vm_StepOf:1|3", vm.StepLine);
        vm.NextCommand.Execute(null);
        Assert.True(vm.OnModel);
        Assert.True(vm.CanGoBack);
        vm.NextCommand.Execute(null);
        Assert.True(vm.OnWhere);
        Assert.Equal("Vm_WelcomeFinish", vm.NextText);
        vm.BackCommand.Execute(null);
        Assert.True(vm.OnModel);
        vm.NextCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Model_step_shows_the_check_then_the_recommendation_with_its_size()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        Assert.False(vm.Checking);
        Assert.True(vm.ShowOffer);
        Assert.Equal("14B model (medium)", vm.ModelName);
        Assert.StartsWith("Vm_WelcomeSize:", vm.ModelSize);
        Assert.StartsWith("Vm_HwMemory:", vm.MemoryLine);
    }

    [Fact]
    public async Task Download_runs_in_place_and_does_not_block_the_next_step()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        vm.NextCommand.Execute(null);
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.True(vm.ShowProgress);
        Assert.False(vm.ShowOffer);
        Assert.True(vm.OnModel);
        for (var i = 0; i < 30; i++) rig.Host.Tick();
        Assert.True(vm.DownloadDone);
        Assert.True(vm.OnModel); // the person moves on with Next
        vm.NextCommand.Execute(null);
        Assert.True(vm.OnWhere);
    }

    [Fact]
    public async Task Choosing_later_just_moves_on_without_downloading()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);
        Assert.False(vm.DownloadStarted);
        Assert.True(vm.OnWhere);
    }

    [Fact]
    public async Task Finish_saves_start_with_windows_and_skip_keeps_an_earlier_choice()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        var closed = 0;
        vm.CloseRequested += () => closed++;
        await vm.SetStartWithWindowsAsync(true);
        Assert.True((await rig.Host.GetSettingsAsync()).StartWithWindows);
        await vm.SetStartWithWindowsAsync(false);
        vm.SkipCommand.Execute(null);
        Assert.Equal(1, closed);
        Assert.False((await rig.Host.GetSettingsAsync()).StartWithWindows);
        Assert.False(rig.State.FirstRunDone); // the window marks it done when it closes
        rig.Shell.CompleteFirstRun();
        Assert.True(rig.State.FirstRunDone);
        Assert.False(rig.Shell.ShowFirstRun);
    }

    [Fact]
    public async Task Finish_applies_the_recommended_default_when_the_person_never_touched_the_switch()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        await vm.FinishCommand.ExecuteAsync(null);
        Assert.True((await rig.Host.GetSettingsAsync()).StartWithWindows);
    }

    [Fact]
    public async Task Start_with_recommended_settings_downloads_the_model_keeps_startup_on_and_closes()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        var closed = 0;
        vm.CloseRequested += () => closed++;
        Assert.StartsWith("Vm_WelcomeRecommendedTip:", vm.RecommendedTip); // the tooltip states the download size
        await vm.StartRecommendedCommand.ExecuteAsync(null);
        Assert.True(vm.DownloadStarted);
        Assert.Equal(1, closed);
        Assert.True((await rig.Host.GetSettingsAsync()).StartWithWindows);
        for (var i = 0; i < 30; i++) rig.Host.Tick();
        Assert.True(vm.DownloadDone);
    }

    [Fact]
    public async Task Model_step_leads_with_a_friendly_label_and_choose_later_leaves_a_note()
    {
        var (rig, vm) = await Open();
        using var _ = rig;
        using var __ = vm;
        Assert.Equal("Vm_WelcomeRecommended:Vm_WelcomeMedium", vm.ModelLabel);
        Assert.Equal("Runs well on your graphics card and memory, about 14 words a second.", vm.ModelReason);
        Assert.False(vm.ShowLaterNote);
        vm.NextCommand.Execute(null);
        vm.ChooseLaterCommand.Execute(null);
        Assert.True(vm.OnWhere);
        Assert.True(vm.ShowLaterNote);
    }

    [Fact]
    public void Portal_apps_are_shown_without_the_raw_id_or_an_unknown_publisher()
    {
        var s = new KeyStrings();
        var portal = new PendingApproval
        {
            AppName = "Files Portal", CallerIdentity = "MindtheApp.FilesPortal_24e29cj9741rt",
            CallerVerified = true, CallerPortalApp = true,
        };
        Assert.True(ApprovalText.IsPortalApp(portal));
        Assert.Equal("Vm_VerifiedShort", ApprovalText.IdentityShort(s, portal));
        Assert.Equal("Vm_VerifiedPortal:MindtheApp.FilesPortal_24e29cj9741rt", ApprovalText.Identity(s, portal));
        Assert.Equal("", ApprovalText.PublisherLine(s, portal));
        var unverified = new PendingApproval { CallerIdentity = @"C:\x\a.exe", CallerVerified = false };
        Assert.StartsWith("Vm_Unverified:", ApprovalText.IdentityShort(s, unverified));
        Assert.Equal("Vm_PublisherUnknown", ApprovalText.PublisherLine(s, unverified));
    }

    [Fact]
    public void Microsoft_signed_packaged_app_is_not_called_a_Portal_app()
    {
        // What a Store-installed PowerShell 7 looks like to the host.
        var s = new KeyStrings();
        var pwsh = new PendingApproval
        {
            AppName = "PowerShell", Publisher = "Microsoft Corporation",
            CallerIdentity = "Microsoft.PowerShell_8wekyb3d8bbwe", CallerVerified = true,
        };
        Assert.False(ApprovalText.IsPortalApp(pwsh));
        Assert.Equal("Vm_Verified:Microsoft.PowerShell_8wekyb3d8bbwe", ApprovalText.IdentityShort(s, pwsh));
        Assert.Equal("Vm_Verified:Microsoft.PowerShell_8wekyb3d8bbwe", ApprovalText.Identity(s, pwsh));
        Assert.Equal("Vm_Publisher:Microsoft Corporation", ApprovalText.PublisherLine(s, pwsh));
    }

    [Fact]
    public void Unsigned_exe_is_not_called_a_Portal_app()
    {
        var s = new KeyStrings();
        var exe = new PendingApproval { AppName = "Files Portal", CallerIdentity = @"C:\Tools\tool.exe" };
        Assert.False(ApprovalText.IsPortalApp(exe));
        Assert.Equal(@"Vm_Unverified:C:\Tools\tool.exe", ApprovalText.IdentityShort(s, exe));
        Assert.Equal("Vm_PublisherUnknown", ApprovalText.PublisherLine(s, exe));
    }

    [Fact]
    public void App_claiming_a_Portal_name_is_not_called_a_Portal_app()
    {
        var s = new KeyStrings();
        // Portal-looking name and package name, but the host did not match the Portal publisher.
        var spoof = new PendingApproval
        {
            AppName = "Files Portal", AppId = "files-portal", Publisher = "MindtheApp",
            CallerIdentity = "MindtheApp.FilesPortal_8wekyb3d8bbwe", CallerVerified = true,
        };
        Assert.False(ApprovalText.IsPortalApp(spoof));
        Assert.Equal("Vm_Verified:MindtheApp.FilesPortal_8wekyb3d8bbwe", ApprovalText.IdentityShort(s, spoof));

        // A Portal flag without a package identity is ignored.
        var noPackage = new PendingApproval { AppName = "Files Portal", CallerIdentity = @"C:\x\a.exe", CallerPortalApp = true };
        Assert.False(ApprovalText.IsPortalApp(noPackage));
        Assert.StartsWith("Vm_Unverified:", ApprovalText.IdentityShort(s, noPackage));
    }
}

public class MiniMarkdownTests
{
    [Fact]
    public void Bold_italic_and_code_become_runs_without_markers()
    {
        var lines = MiniMarkdown.Parse("A **big** and *small* `x = 1` day");
        Assert.Single(lines);
        Assert.Equal(new[] { MdKind.Text, MdKind.Bold, MdKind.Text, MdKind.Italic, MdKind.Text, MdKind.Code, MdKind.Text }, lines[0].Select(r => r.Kind));
        Assert.Equal("A big and small x = 1 day", string.Concat(lines[0].Select(r => r.Text)));
    }

    [Fact]
    public void Lists_get_bullets_and_numbers_stay()
    {
        var lines = MiniMarkdown.Parse(string.Join((char)10, new[] { "- one", "* two", "1. three" }));
        Assert.Equal("• one", string.Concat(lines[0].Select(r => r.Text)));
        Assert.Equal("• two", string.Concat(lines[1].Select(r => r.Text)));
        Assert.Equal("1. three", string.Concat(lines[2].Select(r => r.Text)));
    }

    [Fact]
    public void Unfinished_markers_stay_as_typed_while_a_reply_is_arriving()
    {
        Assert.Equal("half **bold", MiniMarkdown.StripMarkers("half **bold"));
        Assert.Equal("2 * 3 * 4", MiniMarkdown.StripMarkers("2 * 3 * 4"));
        Assert.Equal("", MiniMarkdown.StripMarkers(null));
    }

    [Fact]
    public void Headings_show_as_bold_text()
    {
        var lines = MiniMarkdown.Parse("## Title");
        Assert.Equal(MdKind.Bold, lines[0][0].Kind);
        Assert.Equal("Title", lines[0][0].Text);
    }
}

public class ApprovalRefreshTests
{
    [Fact]
    public async Task Home_connected_apps_count_refreshes_after_an_approval_is_answered()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Shell.ApprovalPresenter = _ => Task.FromResult<bool?>(true);
        await rig.Shell.InitializeAsync();
        var home = new HomeViewModel(rig.Shell, rig.Ui, rig.Strings);
        await home.LoadAsync();
        Assert.Equal("Vm_HomeApps:2", home.AppsLine);
        rig.Host.RaiseApproval();
        await rig.Shell.PumpApprovalsAsync();
        Assert.Equal("Vm_HomeApps:3", home.AppsLine);
        home.Dispose();
    }
}

public class TryItThinkingTests
{
    [Fact]
    public async Task Thinking_text_goes_to_its_own_area_and_the_answer_still_arrives()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "[think] hello" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Contains("Working out", vm.Thinking);
        Assert.True(vm.HasThinking);
        Assert.Contains("You asked", vm.Reply);
        Assert.DoesNotContain("Working out", vm.Reply);
        Assert.Equal("", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Think_tags_in_the_answer_are_split_off()
    {
        var (thinking, answer) = TryItText.Split(null, "<think>\nhmm, a greeting\n</think>\n\nHello there.");
        Assert.Equal("hmm, a greeting", thinking);
        Assert.Equal("Hello there.", answer);
    }

    [Fact]
    public void Reasoning_field_and_unclosed_tag_are_both_thinking()
    {
        var (thinking, answer) = TryItText.Split("first", "<think>still going");
        Assert.Equal("first\nstill going", thinking);
        Assert.Equal("", answer);
    }

    [Fact]
    public void Closing_tag_alone_ends_thinking()
    {
        var (thinking, answer) = TryItText.Split(null, "I should greet.</think>Hi!");
        Assert.Equal("I should greet.", thinking);
        Assert.Equal("Hi!", answer);
    }
}
