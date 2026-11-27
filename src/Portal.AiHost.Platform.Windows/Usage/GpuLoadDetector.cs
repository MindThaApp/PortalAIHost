using System.Diagnostics;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Platform.Windows.Usage;

/// <summary>
/// Gaming mode's GPU detector: a process that is not ours using the 3D engine heavily for a while (a game in a window or
/// borderless window). A background timer samples every 2 s; <see cref="IsBusy"/> only reads the cached answer.
/// </summary>
public sealed class GpuLoadDetector : IBusyDetector, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly string[] OwnNames = ["llama-server", "PortalAIHost", "PortalAIHost.Service"];

    private readonly IGpuSampleSource _source;
    private readonly GpuBusyDecider _decider;
    private readonly Action<string>? _log;
    private readonly Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private volatile HashSet<int> _ownPids = [];
    private DateTime _ownRefreshed;
    private volatile bool _busy;
    private int _running;
    private bool _warned;

    public GpuLoadDetector(IGpuSampleSource? source = null, GpuBusyOptions? options = null, Action<string>? log = null)
    {
        _source = source ?? new PdhGpuSampleSource();
        _log = log;
        _decider = new GpuBusyDecider(options ?? new GpuBusyOptions(), IsOwn, () => DateTimeOffset.UtcNow);
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    public bool IsBusy() => _busy;

    private bool IsOwn(int pid) => pid == _ownPid || _ownPids.Contains(pid);

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var samples = _source.Sample();
            if (samples is null)
            {
                if (!_warned)
                {
                    _warned = true;
                    _log?.Invoke("GPU engine counters are unavailable; gaming mode uses full-screen detection only.");
                }

                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _busy = false;
                return;
            }

            RefreshOwnPids();
            _busy = _decider.Update(samples);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke("GPU load detection failed: " + ex.GetType().Name);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void RefreshOwnPids()
    {
        if (DateTime.UtcNow - _ownRefreshed < TimeSpan.FromSeconds(10))
        {
            return;
        }

        var set = new HashSet<int>();
        foreach (var name in OwnNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                set.Add(p.Id);
                p.Dispose();
            }
        }

        _ownPids = set;
        _ownRefreshed = DateTime.UtcNow;
    }

    public void Dispose()
    {
        _timer.Dispose();
        (_source as IDisposable)?.Dispose();
    }
}
