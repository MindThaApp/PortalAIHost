using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class CleanupViewModelTests
{
    private static ModelsViewModel Make(Rig rig) => new(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, null);

    [Fact]
    public async Task Scan_lists_the_groups_separately_with_everything_ticked_and_changes_nothing()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.FreeUpSpaceCommand.ExecuteAsync(null);

        Assert.True(vm.CleanupOpen);
        Assert.Single(vm.CleanupPartial);
        Assert.Single(vm.CleanupOld);
        Assert.Single(vm.CleanupDamaged);
        Assert.All(vm.CleanupPartial.Concat(vm.CleanupOld).Concat(vm.CleanupDamaged), i => Assert.True(i.Selected));
        Assert.True(vm.CanCleanup);
        Assert.Equal(0, rig.Host.CleanupCalls);
    }

    [Fact]
    public async Task Unticking_an_item_changes_what_is_asked_and_declining_asks_the_host_for_nothing()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.FreeUpSpaceCommand.ExecuteAsync(null);
        vm.CleanupOld[0].Selected = false;
        rig.Dialogs.Answer = false;

        await vm.ConfirmCleanupCommand.ExecuteAsync(null);

        Assert.Single(rig.Dialogs.ConfirmTexts);
        Assert.Equal(0, rig.Host.CleanupCalls);
        Assert.Equal(3, rig.Host.CleanupSample.Count);
    }

    [Fact]
    public async Task Confirming_removes_only_the_ticked_items_and_reports_what_was_freed()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig);
        await vm.FreeUpSpaceCommand.ExecuteAsync(null);
        vm.CleanupDamaged[0].Selected = false;
        rig.Dialogs.Answer = true;

        await vm.ConfirmCleanupCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Host.CleanupCalls);
        Assert.Single(rig.Host.CleanupSample);
        Assert.Equal(CleanupKinds.Damaged, rig.Host.CleanupSample[0].Kind);
        Assert.StartsWith("Vm_CleanupDone", vm.CleanupLine);
    }

    [Fact]
    public async Task A_busy_host_shows_its_reason_and_no_items()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Host.CleanupBusy = "A download is running. Pause it first, then free up space.";
        var vm = Make(rig);
        await vm.FreeUpSpaceCommand.ExecuteAsync(null);

        Assert.Equal(rig.Host.CleanupBusy, vm.CleanupLine);
        Assert.False(vm.CleanupHasItems);
        Assert.False(vm.CanCleanup);
    }
}
