using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Runtime;

/// <summary>
/// Sends ": portal" SSE comments for a waiting stream: each new event at once, and the latest one again at
/// least every interval, so proxies and clients see the stream is alive. One writer at a time.
/// </summary>
internal sealed class ProgressPump : IAsyncDisposable
{
    private readonly ApiContext _ctx;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private PortalProgressEvent? _latest;
    private int _stopped;

    public ProgressPump(ApiContext ctx, TimeSpan interval)
    {
        _ctx = ctx;
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(ApiLimits.ProgressCommentIntervalSeconds) : interval;
        _loop = LoopAsync();
    }

    public void Report(PortalProgressEvent e)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        Volatile.Write(ref _latest, e);
        _ = SendAsync(e);
    }

    /// <summary>Stops the pump and writes one final event (for example "started").</summary>
    public async Task StopAsync(PortalProgressEvent final)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _write.WaitAsync(_ctx.Aborted).ConfigureAwait(false);
        try
        {
            await _ctx.WriteProgressAsync(final).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task SendAsync(PortalProgressEvent e)
    {
        try
        {
            await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // The pump was stopped or disposed while this tick waited.
        }

        try
        {
            if (!_stop.IsCancellationRequested)
            {
                await _ctx.WriteProgressAsync(e).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or
            ObjectDisposedException)
        {
            // The client went away; the request's own token ends the work.
        }
        finally
        {
            try
            {
                _write.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_interval, _ctx.Time, _stop.Token).ConfigureAwait(false);
                await SendAsync(Volatile.Read(ref _latest) ?? new PortalProgressEvent { Event = ProgressEvents.Queued, Position = 0 })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        // Wait for any in-flight comment before the stream continues.
        await _write.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _write.Release();
        _write.Dispose();
        _stop.Dispose();
    }
}
