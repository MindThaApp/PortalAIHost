using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class LiveTests
{
    private static (FakeHostControl Host, LiveViewModel Live) Make(FakeScenario scenario = FakeScenario.Loaded)
    {
        var host = new FakeHostControl(scenario, autoTick: false);
        return (host, new LiveViewModel(host, new InlineDispatcher(), new KeyStrings()));
    }

    [Fact]
    public async Task Shows_generating_app_speed_meters_and_today_list()
    {
        var (host, live) = Make();
        host.Activity.Add(new AppActivity { AppId = "v", AppName = "Voice Portal", Requests = 3, Running = 1, LastSeen = DateTimeOffset.UtcNow, Tokens = 90 });
        host.Activity.Add(new AppActivity { AppId = "p", AppName = "PDF", Requests = 1, LastSeen = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await live.RefreshAsync();
        Assert.True(live.IsGenerating);
        Assert.Equal("Vm_LiveGenerating:Voice Portal", live.ActivityTitle);
        Assert.True(live.HasSpeed);
        Assert.Equal("Vm_LiveSpeed:21", live.SpeedLine); // 28 tokens a second is about 21 words
        Assert.True(live.HasMemory);
        Assert.True(live.HasVram);
        Assert.Equal(18.0 / 64 * 100, live.RamPercent, 3);
        Assert.Equal(7.0 / 8 * 100, live.VramPercent, 3);
        Assert.Equal(2, live.Today.Count);
        Assert.Equal("Voice Portal", live.Today[0].Name);
        Assert.StartsWith("Vm_TodayRow:3|", live.Today[0].Detail);
        Assert.StartsWith("Vm_TodayRow1:1|Vm_SeenMinutes", live.Today[1].Detail.Replace(":5", ""));
    }

    [Fact]
    public async Task Idle_host_shows_no_speed_and_no_meters_when_unloaded()
    {
        var (_, live) = Make(FakeScenario.SafeMode); // model installed, not loaded
        await live.RefreshAsync();
        Assert.False(live.IsGenerating);
        Assert.Equal("Vm_LiveIdle", live.ActivityTitle);
        Assert.False(live.HasMemory);
        Assert.False(live.CanUnload);
        Assert.True(live.CanLoad);
        Assert.True(live.NoToday);
    }

    [Fact]
    public async Task Unload_is_pending_before_the_host_answers_then_completes()
    {
        var (host, live) = Make();
        await live.RefreshAsync();
        Assert.True(live.CanUnload);
        var gate = new TaskCompletionSource();
        host.EngineGate = gate.Task;
        var run = live.UnloadCommand.ExecuteAsync(null);
        // Visual first: pending is already on, buttons are off, the work has not finished.
        Assert.True(live.ActionPending);
        Assert.False(live.CanUnload);
        Assert.False(live.CanLoad);
        Assert.Equal("Vm_LivePendingUnload", live.PendingText);
        gate.SetResult();
        await run;
        Assert.False(live.ActionPending);
        Assert.False(live.HasActionError);
        Assert.Equal(1, host.UnloadCalls);
        Assert.True(live.CanLoad);
        Assert.False(live.CanUnload);
    }

    [Fact]
    public async Task Failed_unload_rolls_back_and_shows_the_reason()
    {
        var (host, live) = Make();
        await live.RefreshAsync();
        host.EngineFailure = "The engine is busy.";
        await live.UnloadCommand.ExecuteAsync(null);
        Assert.False(live.ActionPending);
        Assert.True(live.CanUnload);
        Assert.True(live.HasActionError);
        Assert.Equal("Vm_LiveUnloadFailed:The engine is busy.", live.ActionError);
    }

    [Fact]
    public async Task Load_runs_the_load_call()
    {
        var (host, live) = Make(FakeScenario.SafeMode);
        await live.RefreshAsync();
        await live.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, host.LoadCalls);
        Assert.True(live.CanUnload);
    }

    [Fact]
    public async Task Reads_only_while_started()
    {
        var (host, live) = Make();
        var time = new Manual();
        var timed = new LiveViewModel(host, new InlineDispatcher(), new KeyStrings(), time, TimeSpan.FromMilliseconds(1500));
        Assert.False(timed.IsRunning);
        timed.Start();
        Assert.True(timed.IsRunning);
        timed.Stop();
        Assert.False(timed.IsRunning);
        await Task.CompletedTask;
        live.Dispose();
    }

    private sealed class Manual : TimeProvider { }

    [Fact]
    public async Task Tray_header_is_state_first()
    {
        var host = new FakeHostControl(FakeScenario.Loaded, autoTick: false);
        var vm = new TrayPopupViewModel(host, new InlineDispatcher(), new KeyStrings());
        Assert.Equal("Vm_TrayOff", vm.Header);
        await vm.Live.RefreshAsync();
        Assert.StartsWith("Vm_TrayReady:", vm.Header);
        Assert.Equal("Vm_TrayLastNone", vm.LastRequestLine);
        host.Activity.Add(new AppActivity { AppId = "v", AppName = "Voice Portal", Requests = 1, Running = 1, LastSeen = DateTimeOffset.UtcNow });
        await vm.Live.RefreshAsync();
        Assert.StartsWith("Vm_TrayLast:Voice Portal|", vm.LastRequestLine);
        var opened = 0;
        vm.OpenApp = () => opened++;
        vm.OpenCommand.Execute(null);
        Assert.Equal(1, opened);
        vm.Dispose();
    }

    [Fact]
    public void Placement_opens_toward_the_screen_interior()
    {
        var monitor = new Box(0, 0, 1920, 1080);
        // Taskbar at the bottom: window sits above it, 8 px clear, centred on the icon but kept on screen.
        var bottom = TrayPlacement.Place((1850, 1060), monitor, new Box(0, 0, 1920, 1040), 400, 300, 8);
        Assert.Equal(1040 - 300 - 8, bottom.Y);
        Assert.Equal(1920 - 400 - 8, bottom.X);
        // Taskbar at the top: below it.
        var top = TrayPlacement.Place((900, 20), monitor, new Box(0, 40, 1920, 1040), 400, 300, 8);
        Assert.Equal(48, top.Y);
        Assert.Equal(700, top.X);
        // Taskbar on the left: to its right.
        var left = TrayPlacement.Place((20, 500), monitor, new Box(48, 0, 1872, 1080), 400, 300, 8);
        Assert.Equal(56, left.X);
        Assert.Equal(350, left.Y);
        // Taskbar on the right: to its left.
        var right = TrayPlacement.Place((1900, 500), monitor, new Box(0, 0, 1872, 1080), 400, 300, 8);
        Assert.Equal(1872 - 400 - 8, right.X);
        // Second monitor to the left (negative coordinates) keeps the window on that monitor.
        var second = TrayPlacement.Place((-100, 1060), new Box(-1920, 0, 1920, 1080), new Box(-1920, 0, 1920, 1040), 400, 300, 8);
        Assert.InRange(second.X, -1920 + 8, -8);
    }
}
