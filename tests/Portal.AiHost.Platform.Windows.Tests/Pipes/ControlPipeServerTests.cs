using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Platform.Windows.Pipes;

namespace Portal.AiHost.Platform.Windows.Tests.Pipes;

public sealed class ControlPipeServerTests
{
    private static string NewName() => "PortalAIHost.Test." + Guid.NewGuid().ToString("N");

    private static Task<ControlEnvelope> Echo(ControlEnvelope r, CancellationToken ct) => Task.FromResult(ControlPipe.Ok(r.Id));

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        return pipe;
    }

    private static async Task<string?> AskAsync(NamedPipeClientStream pipe, string id)
    {
        try
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"" + id + "\",\"type\":\"get_status\"}\n"));
            await pipe.FlushAsync();
            var reader = new StreamReader(pipe, leaveOpen: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (IOException)
        {
            return null; // The server closed this connection (over the cap).
        }
    }

    [Fact]
    public async Task Many_short_clients_never_free_the_name()
    {
        var name = NewName();
        await using var server = ControlPipeServer.TryStart(name, Echo);
        Assert.NotNull(server);

        // More than MaxConnections at once, and more than the old fixed instance limit in total.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await using var pipe = await ConnectAsync(name);
            var line = await AskAsync(pipe, "r" + i);
            _ = line; // Extra connections over the cap are closed without an answer; both outcomes are fine here.
        }));

        await Task.Delay(200);
        Assert.Null(ControlPipeServer.TryStart(name, Echo));

        await using var after = await ConnectAsync(name);
        Assert.Contains("\"after\"", await AskAsync(after, "after"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connections_over_the_cap_are_closed_and_the_server_keeps_answering()
    {
        var name = NewName();
        await using var server = ControlPipeServer.TryStart(name, Echo)!;
        var held = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < ControlPipeServer.MaxConnections; i++)
            {
                var p = await ConnectAsync(name);
                held.Add(p);
                Assert.NotNull(await AskAsync(p, "h" + i));
            }

            await using var extra = await ConnectAsync(name);
            Assert.Null(await AskAsync(extra, "extra"));
            Assert.Equal(ControlPipeServer.MaxConnections, server.ConnectionCount);
        }
        finally
        {
            foreach (var p in held)
            {
                await p.DisposeAsync();
            }
        }

        await Task.Delay(300);
        await using var again = await ConnectAsync(name);
        Assert.NotNull(await AskAsync(again, "again"));
    }

    [Fact]
    public async Task Closing_a_connection_cancels_its_running_request()
    {
        var name = NewName();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ControlPipeServer.TryStart(name, async (r, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return ControlPipe.Ok(r.Id);
        })!;

        var pipe = await ConnectAsync(name);
        await pipe.WriteAsync("{\"id\":\"1\",\"type\":\"try_it_chat\"}\n"u8.ToArray());
        await pipe.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await pipe.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_window_that_never_reads_is_dropped_and_broadcast_stays_quick()
    {
        var name = NewName();
        await using var server = ControlPipeServer.TryStart(name, Echo, acceptNow: false)!;
        server.WriteTimeout = TimeSpan.FromMilliseconds(300);
        server.Begin();
        await using var pipe = await ConnectAsync(name);
        for (var i = 0; i < 50 && server.ConnectionCount == 0; i++)
        {
            await Task.Delay(50);
        }

        var big = JsonSerializer.SerializeToElement(new string('x', 64 * 1024));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            await server.BroadcastAsync(ControlTypes.EventDownloadProgress, big);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        for (var i = 0; i < 200 && server.ConnectionCount > 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task Held_name_answers_queued_requests_after_begin()
    {
        var name = NewName();
        await using var server = ControlPipeServer.TryStart(name, Echo, acceptNow: false)!;
        Assert.Null(ControlPipeServer.TryStart(name, Echo));
        await using var pipe = await ConnectAsync(name);
        var ask = AskAsync(pipe, "early");
        await Task.Delay(200);
        Assert.False(ask.IsCompleted);
        server.Begin();
        Assert.Contains("\"early\"", await ask, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_twice_never_throws()
    {
        var server = ControlPipeServer.TryStart(NewName(), Echo)!;
        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Probe_reports_the_current_user_as_holder()
    {
        var name = NewName();
        await using var server = ControlPipeServer.TryStart(name, Echo)!;
        Assert.Equal(PipeHolder.CurrentUser, await ControlPipeServer.ProbeHolderAsync(name, TimeSpan.FromSeconds(3)));
        Assert.Equal(PipeHolder.None, await ControlPipeServer.ProbeHolderAsync(NewName(), TimeSpan.FromMilliseconds(200)));
    }
}
