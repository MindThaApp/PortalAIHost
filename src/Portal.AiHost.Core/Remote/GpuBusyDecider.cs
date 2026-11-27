namespace Portal.AiHost.Core.Remote;

/// <summary>3D engine use of one process, in percent of the engine (all of the process's 3D engines added up).</summary>
public readonly record struct GpuProcessLoad(int Pid, double Percent);

/// <summary>Where GPU samples come from. Null means the counters are unavailable.</summary>
public interface IGpuSampleSource
{
    IReadOnlyList<GpuProcessLoad>? Sample();
}

/// <summary>Thresholds for <see cref="GpuBusyDecider"/>.</summary>
public sealed record GpuBusyOptions
{
    public double ThresholdPercent { get; init; } = 40;
    /// <summary>The look-back window of samples.</summary>
    public TimeSpan SustainedFor { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CoolDown { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Share of the samples in the window that must be above the threshold (games dip in menus and loading).</summary>
    public double MinAboveFraction { get; init; } = 0.7;
    public int MinSamples { get; init; } = 3;
}

/// <summary>Marker sample list: a source with nothing to report yet (just reopened). The decider leaves its history alone.</summary>
public static class GpuSamples
{
    public static readonly IReadOnlyList<GpuProcessLoad> NoData = new GpuProcessLoad[0];
}

/// <summary>
/// Pure decision logic for GPU-based gaming detection. Busy when, over the last window, enough samples (at least
/// <see cref="GpuBusyOptions.MinAboveFraction"/>, with at least <see cref="GpuBusyOptions.MinSamples"/>) show a process that
/// is not ours at or above the threshold, and the history covers most of the window. Stays busy until the cool-down has
/// passed since the last such sample.
/// </summary>
public sealed class GpuBusyDecider(GpuBusyOptions options, Func<int, bool> isOwnProcess, Func<DateTimeOffset> clock)
{
    private readonly Queue<(DateTimeOffset At, bool Above)> _history = new();
    private DateTimeOffset _lastAbove;
    private bool _busy;

    public bool Busy => _busy;

    /// <summary>Feeds one sample (null when unavailable, which counts as quiet) and returns the new state.</summary>
    public bool Update(IReadOnlyList<GpuProcessLoad>? samples)
    {
        if (ReferenceEquals(samples, GpuSamples.NoData))
        {
            return _busy;
        }

        var now = clock();
        var above = false;
        if (samples is not null)
        {
            foreach (var s in samples)
            {
                if (s.Percent >= options.ThresholdPercent && !isOwnProcess(s.Pid))
                {
                    above = true;
                    break;
                }
            }
        }

        if (above)
        {
            _lastAbove = now;
        }

        _history.Enqueue((now, above));
        while (_history.Count > 0 && now - _history.Peek().At > options.SustainedFor)
        {
            _history.Dequeue();
        }

        var hits = _history.Count(h => h.Above);
        var covered = now - _history.Peek().At >= options.SustainedFor * 0.8;
        if (covered && _history.Count >= options.MinSamples && hits >= _history.Count * options.MinAboveFraction)
        {
            _busy = true;
        }
        else if (_busy && now - _lastAbove >= options.CoolDown)
        {
            _busy = false;
        }

        return _busy;
    }
}

/// <summary>Busy when any of the given detectors says busy.</summary>
public sealed class CompositeBusyDetector(params IBusyDetector[] detectors) : IBusyDetector
{
    public bool IsBusy()
    {
        foreach (var d in detectors)
        {
            if (d.IsBusy())
            {
                return true;
            }
        }

        return false;
    }
}
