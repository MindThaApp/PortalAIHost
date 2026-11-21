using System.Net;
using System.Net.Sockets;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.FakeEngine;
using Portal.AiHost.Platform.Windows.Pipes;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

public sealed class HostShellLifecycleTests
{
    private static HostShellOptions Options(TestRoot root, IReadOnlyList<int>? ports = null) => new()
    {
        Paths = HostPaths.Resolve(n => n == HostPaths.TestRootVariable ? root.Path : null)!,
        Supervisors = new Dictionary<string, IEngineSupervisor>
        {
            [ModelKinds.Chat] = new FakeEngineSupervisor(ModelKinds.Chat),
            [ModelKinds.Embedding] = new FakeEngineSupervisor(ModelKinds.Embedding),
        },
        KeyProtector = new TestKeyProtector(),
        CallerResolver = new SameUserResolver(),
        QuitGrace = TimeSpan.FromMilliseconds(200),
        Ports = ports,
    };

    [Fact]
    public async Task Session_ends_are_clean_exits_and_never_lead_to_safe_mode()
    {
        using var root = new TestRoot();
        for (var i = 0; i < 3; i++)
        {
            var options = Options(root);
            var shell = (await HostShell.StartAsync(options))!;
            Assert.True(File.Exists(options.Paths.DiscoveryPath));
            Assert.False(shell.Runtime.SafeMode);
            Assert.Equal(0, shell.Marker.UncleanExits);
            Assert.True(File.Exists(Path.Combine(root.Path, SafeModeMarker.FileName)));
            Assert.True(shell.StopForSessionEnd(TimeSpan.FromSeconds(5)));
            Assert.False(File.Exists(Path.Combine(root.Path, SafeModeMarker.FileName)));
            Assert.False(File.Exists(options.Paths.DiscoveryPath));
        }
    }

    [Fact]
    public async Task Failed_start_releases_the_pipe_and_a_retry_starts()
    {
        using var root = new TestRoot();
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await Assert.ThrowsAnyAsync<IOException>(() => HostShell.StartAsync(Options(root, [taken])));

        var retry = await HostShell.StartAsync(Options(root));
        Assert.NotNull(retry);
        await retry.QuitAsync();
    }

    [Fact]
    public async Task Show_sent_before_anyone_listens_is_delivered_to_the_first_handler()
    {
        using var root = new TestRoot();
        var shell = (await HostShell.StartAsync(Options(root)))!;
        try
        {
            await using (var client = await ControlPipeClient.ConnectAsync(shell.Pipe.PipeName))
            {
                await client.ShowAsync("approvals");
            }

            var shown = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.ShowRequested += (_, page) => shown.TrySetResult(page);
            Assert.Equal("approvals", await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await shell.QuitAsync();
        }
    }
}
