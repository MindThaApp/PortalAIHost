using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

internal sealed class FakePicker(string? path) : IModelFilePicker
{
    public int Asked { get; private set; }
    public Task<string?> PickModelFileAsync() { Asked++; return Task.FromResult(path); }
}

public class ModelImportViewModelTests
{
    private static ModelsViewModel Make(Rig rig, IModelFilePicker picker) =>
        new(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, null, null, picker);

    [Fact]
    public async Task Cancelling_the_picker_asks_nothing_of_the_host()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var picker = new FakePicker(null);
        var vm = Make(rig, picker);
        await vm.AddOwnModelCommand.ExecuteAsync(null);
        Assert.Equal(1, picker.Asked);
        Assert.Empty(rig.Host.Imports);
        Assert.False(vm.StoreBusy);
    }

    [Fact]
    public async Task A_picked_file_is_imported_and_the_model_appears_as_added_by_you()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = Make(rig, new FakePicker(@"C:\Temp\my-model.gguf"));
        await vm.LoadAsync();
        await vm.AddOwnModelCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\Temp\my-model.gguf"], rig.Host.Imports);
        await vm.LoadAsync();
        var row = vm.Models.Single(m => m.UserAdded);
        Assert.True(row.Installed);
        Assert.Equal("Vm_UserAddedBadge", row.Badge);
        Assert.Equal("Vm_UserAddedReason", row.Reason);
        Assert.False(row.CanCheck);
        Assert.False(row.CanRedownload);
        Assert.True(row.CanRemove);
        Assert.True(row.CanActivate);
        Assert.StartsWith("Vm_RemoveUserBody", row.RemoveBody);
        Assert.False(vm.StoreBusy);
    }

    [Fact]
    public async Task A_refused_file_shows_the_reason_and_no_progress_stays()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Host.RefuseImport = "This is not a GGUF model file.";
        var vm = Make(rig, new FakePicker(@"C:\Temp\notes.gguf"));
        await vm.AddOwnModelCommand.ExecuteAsync(null);
        Assert.False(vm.StoreBusy);
        Assert.Contains("This is not a GGUF model file.", vm.StoreBusyLine);
        Assert.Empty(rig.Host.Imports);
    }

    [Fact]
    public async Task A_running_import_can_be_cancelled_and_leaves_no_model()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Host.HoldImport = true;
        var vm = Make(rig, new FakePicker(@"C:\Temp\big.gguf"));
        await vm.LoadAsync();
        var before = vm.Models.Count;
        await vm.AddOwnModelCommand.ExecuteAsync(null);
        Assert.True(vm.StoreBusy);
        Assert.Equal(50, vm.StoreBusyPercent);

        await vm.CancelMoveCommand.ExecuteAsync(null);

        Assert.False(vm.StoreBusy);
        await vm.LoadAsync();
        Assert.Equal(before, vm.Models.Count);
    }
}
