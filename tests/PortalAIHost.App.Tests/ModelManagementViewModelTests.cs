using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

internal sealed class RecordingFolders : IFolderOpener
{
    public List<string> Opened { get; } = new();
    public void Open(string path) => Opened.Add(path);
}

public class ModelManagementViewModelTests
{
    private static ModelsViewModel Make(Rig rig, RecordingFolders? folders = null) =>
        new(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, folders);

    private static async Task<(ModelsViewModel Vm, ModelRowViewModel Active)> LoadedWithSpareAsync(Rig rig)
    {
        rig.Host.MarkInstalled("chat-8b");
        var vm = Make(rig);
        await vm.LoadAsync();
        return (vm, vm.Models.First(m => m.Id == "chat-14b"));
    }

    [Fact]
    public async Task The_model_in_use_has_a_remove_button_and_the_confirm_names_what_it_removes()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, row) = await LoadedWithSpareAsync(rig);
        Assert.True(row.Active);
        Assert.True(row.CanRemove);
        Assert.Equal("Vm_RemoveVerb:" + row.Name + "|" + row.SizeText, row.RemoveVerb);
        rig.Dialogs.Answer = false;
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal([row.RemoveVerb], rig.Dialogs.ConfirmTexts);
        Assert.Empty(rig.Host.Removals); // declined: nothing asked of the host
        Assert.False(row.Removing);
    }

    [Fact]
    public async Task Removing_the_active_model_can_switch_to_another_installed_one()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, row) = await LoadedWithSpareAsync(rig);
        rig.Dialogs.Script.Enqueue(true); // remove
        rig.Dialogs.Script.Enqueue(true); // switch
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal([("chat-14b", (string?)"chat-8b")], rig.Host.Removals);
        Assert.Equal("Vm_SwitchYes:" + vm.Models.First(m => m.Id == "chat-8b").Name, rig.Dialogs.ConfirmTexts[1]);
        Assert.True(vm.Models.First(m => m.Id == "chat-8b").Active);
        Assert.DoesNotContain(vm.Downloaded, m => m.Id == "chat-14b");
    }

    [Fact]
    public async Task Declining_the_switch_leaves_the_host_with_no_model()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, row) = await LoadedWithSpareAsync(rig);
        rig.Dialogs.Script.Enqueue(true);
        rig.Dialogs.Script.Enqueue(false);
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal([("chat-14b", (string?)null)], rig.Host.Removals);
        Assert.DoesNotContain(vm.Models, m => m.Active);
        Assert.Equal(HostStates.NoModel, (await rig.Host.GetStatusAsync()).Status!.State);
    }

    [Fact]
    public async Task With_no_other_model_installed_there_is_no_switch_question()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        var row = vm.Models.First(m => m.Id == "chat-14b");
        rig.Dialogs.Answer = true;
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Dialogs.Asked);
        Assert.Equal([("chat-14b", (string?)null)], rig.Host.Removals);
    }

    [Fact]
    public async Task The_row_says_removing_before_the_host_is_asked_and_a_refusal_rolls_it_back_with_the_reason()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, row) = await LoadedWithSpareAsync(rig);
        var removalsWhenFlagged = -1;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelRowViewModel.Removing) && row.Removing && removalsWhenFlagged < 0)
                removalsWhenFlagged = rig.Host.Removals.Count;
        };
        rig.Host.RefuseNextRemove = "The model is in use by another process.";
        rig.Dialogs.Script.Enqueue(true);
        rig.Dialogs.Script.Enqueue(false);
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(0, removalsWhenFlagged); // flagged first, host asked after
        Assert.False(row.Removing); // rolled back
        Assert.True(row.Installed);
        Assert.True(row.CanRemove);
        Assert.True(vm.HasRemoveFailure);
        Assert.Contains("The model is in use by another process.", vm.RemoveFailure);
        Assert.StartsWith("Vm_RemoveFailed:", vm.RemoveFailure);
    }

    [Fact]
    public async Task While_removing_the_row_shows_removing_and_offers_no_other_action()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (_, row) = await LoadedWithSpareAsync(rig);
        row.Apply(new DownloadProgressEvent { CatalogueId = row.Id, State = "removing" });
        Assert.True(row.Removing);
        Assert.Equal("Vm_Removing", row.ProgressText);
        Assert.False(row.CanRemove);
        Assert.False(row.CanActivate);
        Assert.False(row.CanCheck);
        row.Apply(new DownloadProgressEvent { CatalogueId = row.Id, State = "remove_failed", Message = "x" });
        Assert.False(row.Removing);
        Assert.True(row.CanRemove);
    }

    [Fact]
    public async Task Installed_models_sit_in_the_downloaded_section_and_the_rest_follows_below()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _) = await LoadedWithSpareAsync(rig);
        Assert.True(vm.HasDownloaded);
        Assert.Equal(["chat-14b", "chat-8b"], vm.Downloaded.Select(m => m.Id).Order());
        Assert.All(vm.Downloaded, m => Assert.True(m.Installed));
        Assert.DoesNotContain(vm.Catalogue, m => m.Installed);
        Assert.Contains(vm.Catalogue, m => m.Id == "qwen3-30b");
        Assert.All(vm.Downloaded, m => Assert.StartsWith("Vm_UsedDays:", m.LastUsedText));
    }

    [Fact]
    public async Task With_nothing_installed_there_is_no_downloaded_section()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = Make(rig);
        await vm.LoadAsync();
        Assert.False(vm.HasDownloaded);
        Assert.Empty(vm.Downloaded);
        Assert.Equal(vm.Models.Count, vm.Catalogue.Count);
    }

    [Fact]
    public async Task The_storage_line_gives_the_folder_free_space_and_what_the_models_use_and_open_folder_opens_it()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var folders = new RecordingFolders();
        var vm = Make(rig, folders);
        await vm.LoadAsync();
        Assert.StartsWith("Vm_StorageLine:", vm.StorageLine);
        Assert.EndsWith(Fmt.Bytes(9_000_000_000), vm.StorageLine);
        Assert.True(vm.CanOpenFolder);
        await vm.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal([await rig.Host.GetStoreFolderAsync()], folders.Opened);
        Assert.Equal("", vm.ErrorLine);
    }

    [Fact]
    public async Task Details_show_advice_memory_and_a_licence_link_only_what_exists()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        var big = vm.Models.First(m => m.Id == "chat-14b");
        Assert.True(big.HasAdvice);
        Assert.Contains("all-rounder", big.AdviceText);
        Assert.Contains("Vm_GoodFor:everyday chat, writing", big.AdviceText);
        Assert.True(big.HasMemory);
        Assert.StartsWith("Vm_MemoryNeeded:", big.MemoryText);
        Assert.True(big.HasLicence);
        Assert.Equal("Vm_Licence:apache-2.0", big.LicenceText);
        Assert.Equal(Uri.UriSchemeHttps, big.LicenceUri!.Scheme);
        var plain = vm.Models.First(m => m.Id == "chat-3b");
        Assert.False(plain.HasAdvice); // no advice in the list: nothing shown
        Assert.DoesNotContain("Vm_GoodFor", plain.AdviceText);
    }

    [Fact]
    public void A_licence_link_that_is_not_https_is_not_offered()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        var row = new ModelRowViewModel(vm, rig.Strings, new CatalogueModelSummary
        {
            Id = "x", DisplayName = "X", Installed = true, LicenceName = "custom", LicenceUrl = "file:///C:/secret",
        }, existingCopy: false);
        Assert.Null(row.LicenceUri);
    }

    [Fact]
    public async Task A_check_with_a_damaged_file_marks_it_damaged_offers_download_again_and_deletes_nothing()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, row) = await LoadedWithSpareAsync(rig);
        var other = vm.Models.First(m => m.Id == "chat-8b");
        rig.Host.VerifyOutcome = "damaged";
        await other.CheckCommand.ExecuteAsync(null);
        Assert.Equal(["chat-8b"], rig.Host.Verifies);
        Assert.True(other.Damaged);
        Assert.False(other.Checking);
        Assert.StartsWith("Vm_CheckDamaged:", other.CheckText);
        Assert.True(other.CanRedownload);
        Assert.False(other.CanActivate); // a damaged model is not offered for use
        Assert.Equal("Vm_DamagedBadge", other.Badge);
        Assert.Equal(0, rig.Host.RemoveCalls); // never removed by the check
        Assert.Empty(rig.Host.Removals);
        Assert.Equal(1, row.Id == "chat-14b" ? 1 : 0);
    }

    [Fact]
    public async Task A_clean_check_says_so_and_download_again_removes_through_the_host_then_downloads_after_a_yes()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _) = await LoadedWithSpareAsync(rig);
        var other = vm.Models.First(m => m.Id == "chat-8b");
        await other.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Vm_CheckOk", other.CheckText);
        Assert.False(other.Damaged);
        Assert.False(other.CanRedownload);

        rig.Host.VerifyOutcome = "damaged";
        await other.CheckCommand.ExecuteAsync(null);
        rig.Dialogs.Answer = false;
        await other.RedownloadCommand.ExecuteAsync(null);
        Assert.Empty(rig.Host.Removals); // the user said no

        rig.Dialogs.Answer = true;
        var again = vm.Models.First(m => m.Id == "chat-8b");
        await again.RedownloadCommand.ExecuteAsync(null);
        Assert.Equal([("chat-8b", (string?)null)], rig.Host.Removals);
        rig.Host.Tick();
        Assert.True(vm.Models.First(m => m.Id == "chat-8b").ShowProgress); // downloading again
    }

    [Fact]
    public async Task Download_again_uses_the_damaged_removal_and_chooses_the_model_again_when_it_was_in_use()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        var row = vm.Models.First(m => m.Id == "chat-14b");
        Assert.True(row.Active);
        rig.Host.VerifyOutcome = "damaged";
        await row.CheckCommand.ExecuteAsync(null);
        rig.Dialogs.Answer = true;
        await row.RedownloadCommand.ExecuteAsync(null);
        Assert.Equal(["chat-14b"], rig.Host.DamagedRemovals);
        Assert.Empty(rig.Host.Activations); // nothing to choose yet: it is downloading
        for (var i = 0; i < 40; i++) rig.Host.Tick();
        Assert.Equal(["chat-14b"], rig.Host.Activations); // chosen again once the download finished
    }

    [Fact]
    public async Task Download_again_for_a_model_not_in_use_does_not_choose_it()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _) = await LoadedWithSpareAsync(rig);
        var other = vm.Models.First(m => m.Id == "chat-8b");
        rig.Host.VerifyOutcome = "damaged";
        await other.CheckCommand.ExecuteAsync(null);
        rig.Dialogs.Answer = true;
        await other.RedownloadCommand.ExecuteAsync(null);
        for (var i = 0; i < 40; i++) rig.Host.Tick();
        Assert.Empty(rig.Host.Activations);
    }

    [Fact]
    public async Task A_check_can_be_stopped_and_files_added_in_place_cannot_be_checked()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var (vm, _) = await LoadedWithSpareAsync(rig);
        var row = vm.Models.First(m => m.Id == "chat-8b");
        await row.CancelCheckCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Host.CancelStoreCalls);
        var mine = new ModelRowViewModel(vm, rig.Strings, new CatalogueModelSummary { Id = "mine", DisplayName = "Mine", Installed = true }, existingCopy: true);
        Assert.False(mine.CanCheck);
    }

    [Fact]
    public async Task A_move_started_elsewhere_shows_progress_and_the_result_on_the_models_page()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.LoadAsync();
        rig.Host.HoldMove = true;
        await rig.Host.MoveStoreAsync(@"D:\Models");
        Assert.True(vm.StoreBusy);
        Assert.StartsWith("Vm_MoveRunning:", vm.StoreBusyLine);
        await vm.CancelMoveCommand.ExecuteAsync(null);
        Assert.False(vm.StoreBusy);
        Assert.Equal("Vm_MoveCancelled", vm.StoreBusyLine);
    }

    // ---- Settings > Models folder: move the models too ----------------------------------------------

    private static async Task<SettingsViewModel> SettingsAsync(Rig rig)
    {
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Picking_a_new_folder_with_models_installed_offers_to_move_them()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = await SettingsAsync(rig);
        vm.ModelsFolder = @"D:\Models";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        Assert.True(vm.MoveOfferVisible);
        Assert.StartsWith("Vm_MoveOffer:", vm.MoveOfferText);

        rig.Dialogs.Answer = false;
        await vm.MoveCommand.ExecuteAsync(null);
        Assert.Empty(rig.Host.Moves); // declined: nothing moves

        rig.Dialogs.Answer = true;
        await vm.MoveCommand.ExecuteAsync(null);
        Assert.Equal([@"D:\Models"], rig.Host.Moves);
        Assert.False(vm.Moving); // the fake finished at once
        Assert.False(vm.MoveOfferVisible);
        Assert.Equal(SettingsViewModel.MessageSuccess, vm.MessageKind);
    }

    [Fact]
    public async Task With_no_models_installed_no_move_is_offered()
    {
        using var rig = new Rig(FakeScenario.Fresh);
        var vm = await SettingsAsync(rig);
        vm.ModelsFolder = @"D:\Models";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        Assert.False(vm.MoveOfferVisible);
    }

    [Fact]
    public async Task A_refused_move_shows_the_reason_including_how_much_space_is_needed()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = await SettingsAsync(rig);
        vm.ModelsFolder = @"D:\Models";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        rig.Host.RefuseMove = "Not enough space on that drive: moving needs 11.0 GB and it has 3.0 GB free.";
        rig.Dialogs.Answer = true;
        await vm.MoveCommand.ExecuteAsync(null);
        Assert.Equal(SettingsViewModel.MessageError, vm.MessageKind);
        Assert.Contains("needs 11.0 GB", vm.Message);
        Assert.False(vm.Moving);
        Assert.True(vm.MoveOfferVisible); // can try another drive
    }

    [Fact]
    public async Task A_running_move_shows_progress_and_can_be_cancelled()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = await SettingsAsync(rig);
        vm.ModelsFolder = @"D:\Models";
        await vm.CommitFolderCommand.ExecuteAsync(null);
        rig.Host.HoldMove = true;
        rig.Dialogs.Answer = true;
        await vm.MoveCommand.ExecuteAsync(null);
        Assert.True(vm.Moving);
        Assert.Equal(40, vm.MovePercent);
        Assert.StartsWith("Vm_MoveSettingsRunning:", vm.Message);
        await vm.CancelMoveCommand.ExecuteAsync(null);
        Assert.False(vm.Moving);
        Assert.Equal("Vm_MoveCancelled", vm.Message);
        Assert.True(vm.MoveOfferVisible);
    }
}

public class StoreControlPipeTests
{
    [Fact]
    public async Task The_new_calls_send_the_new_messages_with_their_payloads()
    {
        var ch = new ScriptedChannel();
        ch.Answers[ControlTypes.OpenStoreFolder] = _ => ScriptedChannel.Ok(new StoreFolderInfo { Path = @"D:\Models\Portal AI Host Models" }, AiHostJsonContext.Default.StoreFolderInfo);
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(ch);
        await using var host = new PipeHostControl(factory);

        await host.RemoveModelAsync("a", "b");
        await host.VerifyModelAsync("c");
        var path = await host.GetStoreFolderAsync();
        await host.MoveStoreAsync(@"E:\Models");
        await host.CancelStoreOperationAsync();

        Assert.Equal(@"D:\Models\Portal AI Host Models", path);
        Assert.Equal([ControlTypes.RemoveModel, ControlTypes.VerifyModel, ControlTypes.OpenStoreFolder, ControlTypes.MoveStore, ControlTypes.CancelStoreOperation],
            ch.Sent.Select(s => s.Type));
        var remove = ch.Sent[0].Payload!.Value.Deserialize(AiHostJsonContext.Default.RemoveModelCommand)!;
        Assert.Equal(("a", "b"), (remove.CatalogueId, remove.SwitchToId));
        Assert.Equal("c", ch.Sent[1].Payload!.Value.Deserialize(AiHostJsonContext.Default.VerifyModelCommand)!.CatalogueId);
        Assert.Equal(@"E:\Models", ch.Sent[3].Payload!.Value.Deserialize(AiHostJsonContext.Default.MoveStoreCommand)!.ParentFolder);
        Assert.Null(ch.Sent[4].Payload);
    }

    [Fact]
    public async Task Store_progress_events_reach_the_window()
    {
        var ch = new ScriptedChannel();
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(ch);
        await using var host = new PipeHostControl(factory);
        var seen = new List<StoreProgressEvent>();
        host.StoreProgress += seen.Add;
        await host.VerifyModelAsync("c"); // connects
        ch.Push(ControlTypes.EventStoreProgress, JsonSerializer.SerializeToElement(
            new StoreProgressEvent { Operation = StoreOperations.Verify, State = StoreStates.Damaged, CatalogueId = "c", Message = "x" },
            AiHostJsonContext.Default.StoreProgressEvent));
        var e = Assert.Single(seen);
        Assert.Equal((StoreOperations.Verify, StoreStates.Damaged, "c"), (e.Operation, e.State, e.CatalogueId));
    }

    [Fact]
    public void The_listing_carries_the_models_size_into_the_view()
    {
        var view = PipeHostControl.ToView(new ModelListing { ModelsFolder = "D:/m", FreeBytes = 5, ModelsBytes = 1234 });
        Assert.Equal(1234, view.ModelsBytes);
    }
}
