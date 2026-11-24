using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class PerformanceSettingsTests
{
    private const long Gb = 1L << 30;

    private static async Task<(Rig Rig, SettingsViewModel Vm)> StartAsync()
    {
        var rig = new Rig(FakeScenario.Loaded);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs) { PerformanceApplyDelayMs = 0 };
        await vm.LoadAsync();
        return (rig, vm);
    }

    private static async Task WaitForSavesAsync(Rig rig, int count)
    {
        for (var i = 0; i < 200 && rig.Host.SavedSettings.Count < count; i++) await Task.Delay(10);
        Assert.True(rig.Host.SavedSettings.Count >= count, "the host was not asked to apply");
        await Task.Delay(30);
    }

    [Fact]
    public async Task Page_shows_what_the_host_reports()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        Assert.True(vm.PerformanceAvailable);
        Assert.Equal(SettingsViewModel.BalancedIndex, vm.PresetIndex);
        Assert.Equal(51, vm.MemoryGb);
        Assert.Equal(56, vm.MemoryMaxGb);
        Assert.Contains("Vm_Perf_StopFull", vm.MemoryLabel);
        Assert.Contains("Vm_Perf_Cores", vm.CoresLabel);
        Assert.True(vm.RunOnEnabled);
        Assert.True(vm.GpuMemoryVisible);
        Assert.False(vm.HasGpuNote);
        Assert.Equal("Vm_Perf_Fit_Full", vm.FitText);
    }

    [Fact]
    public async Task Moving_a_slider_updates_the_label_at_once_switches_to_custom_then_applies()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.MemoryGb = 6;

        // Visual first: no wait for the host.
        Assert.Equal(SettingsViewModel.CustomIndex, vm.PresetIndex);
        Assert.Contains("Vm_Perf_StopPartial", vm.MemoryLabel);
        Assert.Equal("Vm_Perf_Help_custom", vm.PresetHelp);

        await WaitForSavesAsync(rig, 1);
        var saved = rig.Host.SavedSettings[^1];
        Assert.Equal("custom", saved.Preset);
        Assert.Equal(6 * Gb, saved.MemoryLimitBytes);
        Assert.Equal(saved.Compute, saved.Backend);
        Assert.True(vm.HasPerformanceMessage);
        Assert.Equal(SettingsViewModel.MessageSuccess, vm.PerformanceMessageKind);
    }

    [Fact]
    public async Task Choosing_a_preset_fills_the_controls_with_its_values()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.PresetIndex = SettingsViewModel.QuietIndex;
        Assert.Equal(32, vm.MemoryGb);
        Assert.Equal(6, vm.CoresValue);
        Assert.Equal(0, vm.PriorityIndex);
        Assert.Equal(SettingsViewModel.QuietIndex, vm.PresetIndex);
        await WaitForSavesAsync(rig, 1);
        Assert.Equal("quiet", rig.Host.SavedSettings[^1].Preset);

        vm.PresetIndex = SettingsViewModel.FastIndex;
        Assert.Equal(56, vm.MemoryGb);
        Assert.Equal(1, vm.PriorityIndex);
        await WaitForSavesAsync(rig, 2);
        Assert.Equal("normal", rig.Host.SavedSettings[^1].Priority);
    }

    [Fact]
    public async Task Processor_choice_hides_graphics_memory_and_saves_both_settings()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.RunOnIndex = 2;
        Assert.False(vm.GpuMemoryVisible);
        await WaitForSavesAsync(rig, 1);
        var saved = rig.Host.SavedSettings[^1];
        Assert.Equal("cpu", saved.Compute);
        Assert.Equal("cpu", saved.Backend);
        Assert.Equal("Vm_Perf_Fit_Cpu", vm.FitText);
    }

    [Fact]
    public async Task A_small_limit_shows_the_smaller_model_wording_before_it_applies()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.MemoryGb = 8;
        await WaitForSavesAsync(rig, 1);
        Assert.Equal("Vm_Perf_Fit_Smaller:Qwen3 14B|Qwen3 4B", vm.FitText);
    }

    [Fact]
    public async Task Reset_sends_the_defaults_and_shows_what_the_host_reports()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.MemoryGb = 10;
        await WaitForSavesAsync(rig, 1);
        await vm.ResetPerformanceCommand.ExecuteAsync(null);
        var saved = rig.Host.SavedSettings[^1];
        Assert.Equal("balanced", saved.Preset);
        Assert.Null(saved.MemoryLimitBytes);
        Assert.Null(saved.CpuThreads);
        Assert.Equal("auto", saved.Compute);
        Assert.Equal("auto", saved.Backend);
        Assert.Equal("low", saved.Priority);
        Assert.Equal(SettingsViewModel.BalancedIndex, vm.PresetIndex);
    }

    [Fact]
    public async Task Other_settings_keep_the_performance_values_the_page_has_not_touched()
    {
        var (rig, vm) = await StartAsync();
        using var _ = rig;
        vm.IdleIndex = SettingsViewModel.FiveMinutesIndex;
        for (var i = 0; i < 100 && rig.Host.SavedSettings.Count == 0; i++) await Task.Delay(10);
        var saved = rig.Host.SavedSettings[^1];
        Assert.Equal("balanced", saved.Preset);
        Assert.Null(saved.MemoryLimitBytes);
    }
}
