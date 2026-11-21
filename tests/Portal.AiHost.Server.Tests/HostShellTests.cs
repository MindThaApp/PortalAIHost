using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

public sealed class TestRootGuardTests
{
    [Fact]
    public void Unmarked_or_missing_or_relative_test_root_is_refused()
    {
        using var root = new TestRoot();
        var unmarked = Path.Combine(root.Path, "unmarked");
        Directory.CreateDirectory(unmarked);
        Assert.Null(HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? unmarked : null));
        Assert.Null(HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? Path.Combine(root.Path, "missing") : null));
        Assert.Null(HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? "relative\\dir" : null));
        Assert.Null(HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? "" : null));
    }

    [Fact]
    public void Marked_test_root_keeps_everything_inside_it()
    {
        using var root = new TestRoot();
        var paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!;
        Assert.True(paths.TestMode);
        Assert.Equal(root.Path, paths.DataRoot);
        Assert.StartsWith(root.Path, paths.DiscoveryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".test-", paths.PipeName, StringComparison.Ordinal);
        Assert.NotEqual(ControlPipe.NameForCurrentUser(), paths.PipeName);
    }

    [Fact]
    public async Task Host_exe_exits_with_code_3_for_an_unmarked_test_root()
    {
        using var root = new TestRoot();
        var unmarked = Path.Combine(root.Path, "data");
        Directory.CreateDirectory(unmarked);
        var exe = CopyServiceTo(root.Path);
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [HostPaths.TestRootVariable] = unmarked },
        })!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            Assert.Equal(HostPaths.BadTestRootExitCode, p.ExitCode);
            Assert.Empty(Directory.GetFileSystemEntries(unmarked));
        }
        finally
        {
            if (!p.HasExited)
            {
                p.Kill();
            }
        }
    }

    /// <summary>Runs the host from the test's own temp copy, never from the build output.</summary>
    internal static string CopyServiceTo(string root)
    {
        var source = Path.GetDirectoryName(typeof(HostShell).Assembly.Location)!;
        var target = Path.Combine(root, "svc");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return Path.Combine(target, "PortalAIHost.Service.exe");
    }
}

public sealed class SafeModeTests
{
    [Fact]
    public void Two_unclean_exits_in_a_row_mean_safe_mode_and_a_clean_quit_resets()
    {
        using var root = new TestRoot();
        var first = SafeModeMarker.Begin(root.Path);
        Assert.Equal(0, first.UncleanExits);
        Assert.False(first.SafeMode);
        // No EndClean: the process "crashed".
        var second = SafeModeMarker.Begin(root.Path);
        Assert.Equal(1, second.UncleanExits);
        Assert.False(second.SafeMode);
        var third = SafeModeMarker.Begin(root.Path);
        Assert.Equal(2, third.UncleanExits);
        Assert.True(third.SafeMode);
        third.EndClean();
        Assert.False(File.Exists(Path.Combine(root.Path, SafeModeMarker.FileName)));
        Assert.False(SafeModeMarker.Begin(root.Path).SafeMode);
    }

    [Fact]
    public void Clean_end_removes_only_the_marker()
    {
        using var root = new TestRoot();
        var other = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(other, "{}");
        var marker = SafeModeMarker.Begin(root.Path);
        marker.EndClean();
        marker.EndClean();
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.Combine(root.Path, TestRoot.MarkerName)));
    }

    [Fact]
    public async Task Safe_mode_is_reported_over_the_pipe_and_nothing_auto_loads()
    {
        using var root = new TestRoot();
        File.WriteAllText(Path.Combine(root.Path, SafeModeMarker.FileName), "1");
        await using var harness = await ShellHarness.StartAsync(root);
        Assert.True(harness.Shell.Runtime.SafeMode);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var status = await client.GetStatusAsync();
        Assert.True(status.SafeMode);
        Assert.Equal(2, status.UncleanExits);
        Assert.Equal(0, harness.Chat.LoadCount);
    }
}

public sealed class HostShellTests
{
    [Fact]
    public async Task Second_host_for_the_same_user_does_not_start_and_show_reaches_the_first()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        var shown = new TaskCompletionSource<string?>();
        harness.Shell.ShowRequested += (_, page) => shown.TrySetResult(page);

        var second = await HostShell.StartAsync(harness.Options);
        Assert.Null(second);

        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        await client.ShowAsync("models");
        Assert.Equal("models", await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Approval_flow_runs_over_the_pipe_and_quit_cleans_up()
    {
        using var root = new TestRoot();
        var harness = await ShellHarness.StartAsync(root);
        var discovery = harness.Paths.DiscoveryPath;
        Assert.True(File.Exists(discovery));
        Assert.True(File.Exists(Path.Combine(root.Path, SafeModeMarker.FileName)));

        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var pending = new TaskCompletionSource<PendingApproval>();
        client.ApprovalPending += (_, a) => pending.TrySetResult(a);
        var status = await client.GetStatusAsync();
        Assert.False(status.SafeMode);
        using var http = new HttpClient { BaseAddress = new Uri(status.BaseUrl!) };

        using var create = await http.PostAsync(Routes.AuthRequests, TestHost.Json(
            """{ "app_id": "com.mindthaapp.voiceportal", "app_name": "Voice Portal" }"""));
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var approval = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Voice Portal", approval.AppName);
        Assert.Single((await client.ListApprovalsAsync()).Pending);
        await client.ApproveAsync(approval.RequestId);
        using var poll = await http.GetAsync($"{Routes.AuthRequests}/{approval.RequestId}?wait_ms=0");
        Assert.Contains("\"approved\"", await poll.Content.ReadAsStringAsync());
        var clients = await client.ListClientsAsync();
        Assert.Equal("com.mindthaapp.voiceportal", Assert.Single(clients.Clients).AppId);

        var error = await Assert.ThrowsAsync<ControlPipeException>(() => client.StartDownloadAsync("x"));
        Assert.Equal(ErrorCodes.FeatureDisabled, error.Code);
        var unknown = await Assert.ThrowsAsync<ControlPipeException>(() => client.ExchangeAsync("no.such.message", null));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);

        var settings = await client.GetSettingsAsync();
        Assert.Equal(80, settings.MaxMemoryPercent);

        await client.QuitAsync();
        await harness.Shell.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(File.Exists(discovery));
        Assert.False(File.Exists(Path.Combine(root.Path, SafeModeMarker.FileName)));
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task Try_it_runs_through_the_queue_and_engine()
    {
        using var root = new TestRoot();
        await using var harness = await ShellHarness.StartAsync(root);
        await harness.Shell.Runtime.SetActiveAsync(ModelKinds.Chat,
            new Core.Runtime.ActiveModel(ModelKinds.Chat, "m", "M", ["x.gguf"]), default);
        await using var client = await ControlPipeClient.ConnectAsync(harness.Paths.PipeName);
        var reply = await client.TryItChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = ChatRoles.User, Content = "Hello" }],
        });
        Assert.NotEmpty(reply.Choices);
        Assert.Equal(1, harness.Chat.LoadCount);
    }

    [Fact]
    public async Task Host_exe_runs_in_a_test_root_single_instance_and_quits_cleanly()
    {
        using var root = new TestRoot();
        var exe = TestRootGuardTests.CopyServiceTo(root.Path);
        var data = Path.Combine(root.Path, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, TestRoot.MarkerName), "test");
        var paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? data : null)!;
        ProcessStartInfo Info() => new(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { [HostPaths.TestRootVariable] = data },
        };

        using var first = Process.Start(Info())!;
        try
        {
            await StreamingTests.WaitUntil(() => File.Exists(paths.DiscoveryPath) || first.HasExited, 20000);
            Assert.False(first.HasExited);
            var file = JsonDocument.Parse(File.ReadAllText(paths.DiscoveryPath)).RootElement;
            Assert.InRange(file.GetProperty("port").GetInt32(), 41890, 65535);
            Assert.NotInRange(file.GetProperty("port").GetInt32(), 41870, 41879);

            using (var second = Process.Start(Info())!)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await second.WaitForExitAsync(cts.Token);
                Assert.Equal(0, second.ExitCode);
            }

            await using (var client = await ControlPipeClient.ConnectAsync(paths.PipeName, TimeSpan.FromSeconds(5)))
            {
                await client.QuitAsync();
            }

            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await first.WaitForExitAsync(quit.Token);
            Assert.Equal(0, first.ExitCode);
            Assert.False(File.Exists(paths.DiscoveryPath));
            Assert.False(File.Exists(Path.Combine(data, SafeModeMarker.FileName)));
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill();
            }
        }
    }
}

/// <summary>An in-process <see cref="HostShell"/> in a test root, with fake engines.</summary>
internal sealed class ShellHarness : IAsyncDisposable
{
    private ShellHarness(HostShell shell, HostShellOptions options, FakeEngineSupervisor chat)
    {
        Shell = shell;
        Options = options;
        Chat = chat;
    }

    public HostShell Shell { get; }
    public HostShellOptions Options { get; }
    public HostPaths Paths => Options.Paths;
    public FakeEngineSupervisor Chat { get; }

    public static async Task<ShellHarness> StartAsync(TestRoot root)
    {
        var chat = new FakeEngineSupervisor(ModelKinds.Chat);
        var options = new HostShellOptions
        {
            Paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!,
            Supervisors = new Dictionary<string, IEngineSupervisor>
            {
                [ModelKinds.Chat] = chat,
                [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
            },
            KeyProtector = new TestKeyProtector(),
            CallerResolver = new SameUserResolver(),
            QuitGrace = TimeSpan.FromMilliseconds(200),
        };
        var shell = await HostShell.StartAsync(options) ?? throw new InvalidOperationException("pipe already owned");
        return new ShellHarness(shell, options, chat);
    }

    public async ValueTask DisposeAsync() => await Shell.DisposeAsync();
}
