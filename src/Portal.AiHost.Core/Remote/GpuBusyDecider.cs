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
    public TimeSpan SustainedFor { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CoolDown { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Pure decision logic for GPU-based gaming detection. Busy once a process that is not ours keeps the 3D engine at or above
/// the threshold for the sustained window; stays busy until the cool-down has passed without such a process.
/// </summary>
public sealed class GpuBusyDecider(GpuBusyOptions options, Func<int, bool> isOwnProcess, Func<DateTimeOffset> clock)
{
    private DateTimeOffset? _aboveSince;
    private DateTimeOffset _lastAbove;
    private bool _busy;

    public bool Busy => _busy;

    /// <summary>Feeds one sample (null when unavailable, which counts as quiet) and returns the new state.</summary>
    public bool Update(IReadOnlyList<GpuProcessLoad>? samples)
    {
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
            _aboveSince ??= now;
            _lastAbove = now;
            if (now - _aboveSince.Value >= options.SustainedFor)
            {
                _busy = true;
            }
        }
        else
        {
            _aboveSince = null;
            if (_busy && now - _lastAbove >= options.CoolDown)
            {
                _busy = false;
            }
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
