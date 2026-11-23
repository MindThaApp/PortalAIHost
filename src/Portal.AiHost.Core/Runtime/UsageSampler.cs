using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Runtime;

/// <summary>One reading of an engine process.</summary>
public readonly record struct ProcessUsage(long RamBytes, long VramBytes, TimeSpan CpuTime);

/// <summary>Reads memory and processor time of a process. Cheap counters only.</summary>
public interface IProcessUsageProbe
{
    /// <summary>Null when the process is gone or cannot be read.</summary>
    ProcessUsage? Read(int processId);
}

/// <summary>An engine supervisor that can say which process it runs.</summary>
public interface IEngineProcess
{
    int? ProcessId { get; }
}

/// <summary>
/// Turns engine process readings into <see cref="HostUsage"/>. Reads only when asked and at most once per
/// interval; asks between two reads get the cached value.
/// </summary>
public sealed class UsageSampler
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1500);

    private readonly Func<IReadOnlyList<int>> _pids;
    private readonly IProcessUsageProbe _probe;
    private readonly TimeProvider _time;
    private readonly int _cores;
    private readonly Lock _gate = new();
    private HostUsage? _last;
    private DateTimeOffset _lastStamp;
    private TimeSpan _lastCpu;
    private bool _haveCpu;

    public UsageSampler(Func<IReadOnlyList<int>> pids, IProcessUsageProbe probe, TimeProvider? time = null, int? cores = null)
    {
        _pids = pids;
        _probe = probe;
        _time = time ?? TimeProvider.System;
        _cores = Math.Max(1, cores ?? Environment.ProcessorCount);
    }

    /// <summary>Current usage; <paramref name="tokensPerSecond"/> is added fresh on every call.</summary>
    public HostUsage Get(double? tokensPerSecond)
    {
        lock (_gate)
        {
            var stamp = _time.GetUtcNow();
            if (_last is null || stamp - _lastStamp >= MinInterval)
            {
                Sample(stamp);
            }

            return new HostUsage
            {
                RamUsedBytes = _last!.RamUsedBytes,
                VramUsedBytes = _last.VramUsedBytes,
                CpuPercent = _last.CpuPercent,
                UpdatedAt = _last.UpdatedAt,
                GenerationTokensPerSecond = tokensPerSecond,
            };
        }
    }

    private void Sample(DateTimeOffset stamp)
    {
        long ram = 0, vram = 0;
        var cpu = TimeSpan.Zero;
        foreach (var pid in _pids())
        {
            if (_probe.Read(pid) is { } u)
            {
                ram += u.RamBytes;
                vram += u.VramBytes;
                cpu += u.CpuTime;
            }
        }

        var percent = 0;
        if (_haveCpu && _last is not null)
        {
            var wall = (stamp - _lastStamp).TotalSeconds;
            if (wall > 0 && cpu >= _lastCpu)
            {
                percent = (int)Math.Clamp(Math.Round((cpu - _lastCpu).TotalSeconds / wall / _cores * 100), 0, 100);
            }
        }

        _haveCpu = true;
        _lastCpu = cpu;
        _lastStamp = stamp;
        _last = new HostUsage
        {
            RamUsedBytes = ram, VramUsedBytes = vram, CpuPercent = percent, UpdatedAt = _time.GetUtcNow(),
        };
    }
}
