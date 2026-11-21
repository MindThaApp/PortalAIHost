using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

/// <summary>A scripted control connection: answers by message type, records what was sent.</summary>
internal sealed class ScriptedChannel : IControlChannel
{
    public Dictionary<string, Func<JsonElement?, ControlEnvelope>> Answers { get; } = new();
    public List<(string Type, JsonElement? Payload)> Sent { get; } = new();
    public bool Disposed { get; private set; }
    public event Action<ControlEnvelope>? EventReceived;
    public event Action? Disconnected;

    public Task<ControlEnvelope> ExchangeAsync(string type, JsonElement? payload, CancellationToken ct)
    {
        Sent.Add((type, payload));
        return Answers.TryGetValue(type, out var a) ? Task.FromResult(a(payload)) : Task.FromResult(new ControlEnvelope { Type = ControlTypes.Ok });
    }

    public void Push(string type, JsonElement payload) => EventReceived?.Invoke(new ControlEnvelope { Type = type, Payload = payload });
    public void Drop() => Disconnected?.Invoke();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public static ControlEnvelope Ok<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        new() { Type = ControlTypes.Ok, Payload = JsonSerializer.SerializeToElement(value, info) };
}

internal sealed class ScriptedFactory : IControlChannelFactory
{
    public Queue<IControlChannel?> Next { get; } = new();
    public int Attempts { get; private set; }

    public Task<IControlChannel?> TryConnectAsync(CancellationToken ct)
    {
        Attempts++;
        return Task.FromResult(Next.Count > 0 ? Next.Dequeue() : null);
    }
}

internal sealed class ScriptedStartup(string state) : IStartupControl
{
    public string State { get; set; } = state;
    public List<bool> Requests { get; } = new();
    public Task<string> GetStateAsync() => Task.FromResult(State);

    public Task<string> SetEnabledAsync(bool enabled)
    {
        Requests.Add(enabled);
        // Windows refuses to enable a task the user turned off in Task Manager.
        if (State != StartupStates.DisabledByUser) State = enabled ? StartupStates.Enabled : StartupStates.Disabled;
        return Task.FromResult(State);
    }
}

public class PipeHostControlTests
{
    private static ControlStatus Status(bool safe = false) => new()
    {
        Status = new HostStatus { State = HostStates.Ready }, SafeMode = safe, UncleanExits = safe ? 2 : 0, UpgradeModelId = "big",
    };

    [Fact]
    public async Task No_host_gives_the_not_running_snapshot_and_calls_throw_unavailable()
    {
        var factory = new ScriptedFactory();
        await using var host = new PipeHostControl(factory);
        Assert.False((await host.GetStatusAsync()).Connected);
        await Assert.ThrowsAsync<HostUnavailableException>(() => host.ListModelsAsync());
        Assert.Equal(2, factory.Attempts);
    }

    [Fact]
    public async Task Status_maps_safe_mode_and_upgrade_id_and_reconnects_after_a_drop()
    {
        var first = new ScriptedChannel();
        first.Answers[ControlTypes.GetStatus] = _ => ScriptedChannel.Ok(Status(safe: true), AiHostJsonContext.Default.ControlStatus);
        var second = new ScriptedChannel();
        second.Answers[ControlTypes.GetStatus] = _ => ScriptedChannel.Ok(Status(), AiHostJsonContext.Default.ControlStatus);
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(first);
        factory.Next.Enqueue(second);
        await using var host = new PipeHostControl(factory);
        var seen = new List<HostSnapshot>();
        host.StatusChanged += seen.Add;

        var snap = await host.GetStatusAsync();
        Assert.True(snap.Connected);
        Assert.True(snap.SafeMode);
        Assert.Equal(2, snap.UncleanExits);
        Assert.Equal("big", snap.UpgradeModelId);

        first.Drop();
        Assert.False(Assert.Single(seen).Connected);
        Assert.False(host.IsConnected);

        var again = await host.GetStatusAsync();
        Assert.True(again.Connected);
        Assert.False(again.SafeMode);
    }

    [Fact]
    public async Task Pushed_events_reach_the_window_events()
    {
        var ch = new ScriptedChannel();
        ch.Answers[ControlTypes.GetStatus] = _ => ScriptedChannel.Ok(Status(), AiHostJsonContext.Default.ControlStatus);
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(ch);
        await using var host = new PipeHostControl(factory);
        await host.GetStatusAsync();
        DownloadProgressEvent? progress = null;
        PendingApproval? approval = null;
        host.DownloadProgress += e => progress = e;
        host.ApprovalPending += a => approval = a;
        ch.Push(ControlTypes.EventDownloadProgress, JsonSerializer.SerializeToElement(
            new DownloadProgressEvent { CatalogueId = "m", State = "paused", BytesDone = 5, BytesTotal = 10 }, AiHostJsonContext.Default.DownloadProgressEvent));
        ch.Push(ControlTypes.EventApprovalPending, JsonSerializer.SerializeToElement(
            new PendingApproval { RequestId = "r", Publisher = "Contoso" }, AiHostJsonContext.Default.PendingApproval));
        Assert.Equal("paused", progress!.State);
        Assert.Equal("Contoso", approval!.Publisher);
    }

    [Fact]
    public async Task Model_listing_carries_folder_space_existing_copy_upgrade_and_catalogue_state()
    {
        var ch = new ScriptedChannel();
        ch.Answers[ControlTypes.ListModels] = _ => ScriptedChannel.Ok(new ModelListing
        {
            Models = [new CatalogueModelSummary { Id = "a", ExistingCopy = true }, new CatalogueModelSummary { Id = "b", DownloadState = "paused", BytesDone = 3 }],
            ModelsFolder = @"X:\m", FreeBytes = 42, UpgradeModelId = "b", CatalogueAvailable = false,
        }, AiHostJsonContext.Default.ModelListing);
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(ch);
        await using var host = new PipeHostControl(factory);
        var view = await host.ListModelsAsync();
        Assert.Equal(@"X:\m", view.StorageFolder);
        Assert.Equal(42, view.FreeBytes);
        Assert.Equal("b", view.UpgradeModelId);
        Assert.False(view.CatalogueAvailable);
        Assert.Equal(["a"], view.ExistingCopyIds);
    }

    [Fact]
    public async Task Remove_resume_and_settings_send_the_new_messages()
    {
        var ch = new ScriptedChannel();
        ch.Answers[ControlTypes.SetSettings] = _ => ScriptedChannel.Ok(new SettingsSaved { ModelsFolderChanged = true }, AiHostJsonContext.Default.SettingsSaved);
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(ch);
        await using var host = new PipeHostControl(factory);
        await host.RemoveModelAsync("a");
        await host.ResumeDownloadAsync("b");
        var saved = await host.SetSettingsAsync(new HostSettings { ModelsFolder = @"Y:\n" });
        Assert.True(saved.ModelsFolderChanged);
        Assert.Equal([ControlTypes.RemoveModel, ControlTypes.ResumeDownload, ControlTypes.SetSettings], ch.Sent.Select(s => s.Type));
        Assert.Equal("a", ch.Sent[0].Payload!.Value.Deserialize(AiHostJsonContext.Default.RemoveModelCommand)!.CatalogueId);
    }
}

public class ProcessHostLauncherTests
{
    private sealed class Exited(int code) : IStartedProcess
    {
        public bool HasExited => true;
        public int ExitCode => code;
        public void Dispose() { }
    }

    private static string FolderWithHostExe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pah-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ProcessHostLauncher.HostExeName), "");
        return dir;
    }

    [Theory]
    [InlineData(3, "Vm_HostExitTestRoot")]
    [InlineData(4, "Vm_HostExitStartFailed")]
    [InlineData(5, "Vm_HostExitOtherUser")]
    [InlineData(1, "Vm_StartFailed")]
    public async Task Host_exit_codes_become_plain_messages(int code, string key)
    {
        var launcher = new ProcessHostLauncher(_ => Task.FromResult(false), FolderWithHostExe(), _ => new Exited(code), poll: TimeSpan.FromMilliseconds(1));
        Assert.False(await launcher.StartHostAsync());
        Assert.Equal(key, launcher.FailureKey);
    }

    [Fact]
    public async Task A_running_host_is_never_started_again()
    {
        var starts = 0;
        var launcher = new ProcessHostLauncher(_ => Task.FromResult(true), FolderWithHostExe(), _ => { starts++; return null; });
        Assert.True(await launcher.StartHostAsync());
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Missing_host_exe_without_a_package_reports_missing()
    {
        var empty = Path.Combine(Path.GetTempPath(), "pah-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var launcher = new ProcessHostLauncher(_ => Task.FromResult(false), empty, protocolStart: () => Task.FromResult(false),
            getEnvironment: _ => null);
        Assert.False(await launcher.StartHostAsync());
        Assert.Equal("Vm_HostMissing", launcher.FailureKey);
    }

    [Fact]
    public async Task A_test_run_never_starts_the_installed_host_by_protocol()
    {
        var empty = Path.Combine(Path.GetTempPath(), "pah-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var protocolCalls = 0;
        var launcher = new ProcessHostLauncher(_ => Task.FromResult(false), empty,
            protocolStart: () => { protocolCalls++; return Task.FromResult(true); },
            getEnvironment: n => n == ProcessHostLauncher.TestRootVariable ? empty : null);
        Assert.False(await launcher.StartHostAsync());
        Assert.Equal(0, protocolCalls);
        Assert.Equal("Vm_HostMissingTestRun", launcher.FailureKey);
    }
}

public class IntegrationViewModelTests
{
    [Fact]
    public async Task Remove_asks_first_and_does_nothing_when_declined()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new ModelsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        var row = vm.Models.First(m => !m.Active);
        row.Installed = true;
        Assert.True(row.CanRemove);
        rig.Dialogs.Answer = false;
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Dialogs.Asked);
        Assert.Equal(0, rig.Host.RemoveCalls);
        rig.Dialogs.Answer = true;
        await row.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Host.RemoveCalls);
    }

    [Fact]
    public async Task The_active_model_cannot_be_removed()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new ModelsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        Assert.False(vm.Models.First(m => m.Active).CanRemove);
    }

    [Fact]
    public async Task Startup_disabled_in_task_manager_is_shown_and_not_fought()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var startup = new ScriptedStartup(StartupStates.DisabledByUser);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, startup);
        await vm.LoadAsync();
        Assert.False(vm.StartWithWindows);
        Assert.False(vm.StartupChangeable);
        Assert.Equal("Vm_Startup_DisabledByUser", vm.StartupNote);
    }

    [Fact]
    public async Task Startup_toggle_goes_to_windows_and_unpackaged_runs_say_unavailable()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var startup = new ScriptedStartup(StartupStates.Disabled);
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs, startup);
        await vm.LoadAsync();
        vm.StartWithWindows = true;
        Assert.Equal([true], startup.Requests);
        Assert.Equal(StartupStates.Enabled, vm.StartupState);

        var dev = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await dev.LoadAsync();
        Assert.Equal("Vm_Startup_Unavailable", dev.StartupNote);
        Assert.False(dev.StartupChangeable);
    }

    [Fact]
    public async Task A_new_models_folder_says_old_models_stay()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        rig.Dialogs.Answer = true;
        var vm = new SettingsViewModel(rig.Shell, rig.Ui, rig.Strings, rig.Dialogs);
        await vm.LoadAsync();
        vm.ModelsFolder = @"Q:\new";
        await vm.ApplyFolderCommand.ExecuteAsync(null);
        Assert.StartsWith("Vm_FolderSavedNote:", vm.Message);
    }

    [Fact]
    public void Publisher_line_falls_back_to_unknown()
    {
        var s = new KeyStrings();
        Assert.Equal("Vm_PublisherUnknown", ApprovalText.Publisher(s, new PendingApproval()));
        Assert.Equal("Vm_Publisher:Contoso", ApprovalText.Publisher(s, new PendingApproval { Publisher = "Contoso" }));
    }
}
