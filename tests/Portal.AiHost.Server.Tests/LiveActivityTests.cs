using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Platform.Windows.Pipes;

namespace Portal.AiHost.Server.Tests;

public sealed class LiveActivityTests
{
    private static Task SetActiveAsync(ShellHarness h) => h.Shell.Runtime.SetActiveAsync(ModelKinds.Chat,
        new ActiveModel(ModelKinds.Chat, "m", "M", ["x.gguf"]), default);

    [Fact]
    public async Task Engine_load_and_unload_follow_the_pipe_and_leave_the_model_files_alone()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        await SetActiveAsync(harness);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        await client.ExchangeAsync(ControlTypes.LoadEngine, null);
        Assert.Equal(EngineState.Ready, harness.Chat.State);
        await client.ExchangeAsync(ControlTypes.UnloadEngine, null);
        Assert.Equal(EngineState.Stopped, harness.Chat.State);
        var status = await client.GetStatusAsync();
        Assert.Equal(ModelStates.Unloaded, status.Status.Models[ModelKinds.Chat].State);
    }

    [Fact]
    public async Task Unload_without_an_active_model_is_an_error_answer()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        await Assert.ThrowsAsync<ControlPipeException>(() => client.ExchangeAsync(ControlTypes.UnloadEngine, null));
    }

    [Fact]
    public async Task Activity_report_starts_empty()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var answer = await client.ExchangeAsync(ControlTypes.GetActivity, null);
        Assert.Empty(answer.Payload!.Value.Deserialize(AiHostJsonContext.Default.ActivityReport)!.Apps);
    }

    [Fact]
    public async Task Tray_click_signals_a_connected_window_and_asks_for_a_launch_otherwise()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        Assert.False(harness.Shell.RequestTrayPopup(10, 20));
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var seen = new TaskCompletionSource<TrayPopupEvent>();
        client.EventReceived += (_, e) =>
        {
            if (e.Type == ControlTypes.EventTrayPopup)
            {
                seen.TrySetResult(e.Payload!.Value.Deserialize(AiHostJsonContext.Default.TrayPopupEvent)!);
            }
        };
        await client.GetStatusAsync(); // the connection is registered once it has answered
        Assert.True(harness.Shell.RequestTrayPopup(10, 20));
        var popup = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((10, 20), (popup.X, popup.Y));
    }

    [Fact]
    public async Task Status_carries_usage_when_a_probe_is_given()
    {
        using var root = new TestRoot();
        var options = new PortalAIHost.Service.HostShellOptions
        {
            Paths = PortalAIHost.Service.HostPaths.Resolve(n => n == PortalAIHost.Service.HostPaths.TestRootVariable ? root.Path : null)!,
            Supervisors = new Dictionary<string, IEngineSupervisor> { [ModelKinds.Chat] = new FakeEngine.FakeEngineSupervisor(ModelKinds.Chat) },
            KeyProtector = new TestKeyProtector(),
            QuitGrace = TimeSpan.FromMilliseconds(200),
            UsageProbe = new Probe(),
        };
        await using var shell = await PortalAIHost.Service.HostShell.StartAsync(options) ?? throw new InvalidOperationException();
        var usage = shell.BuildControlStatus().Status.Usage;
        Assert.NotNull(usage);
        Assert.Null(usage.GenerationTokensPerSecond);
    }

    private sealed class Probe : IProcessUsageProbe
    {
        public ProcessUsage? Read(int processId) => new(1, 1, TimeSpan.Zero);
    }
}
