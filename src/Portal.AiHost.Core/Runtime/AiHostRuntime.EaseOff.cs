using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

public sealed partial class AiHostRuntime
{
    /// <summary>How often the screen and power rules are looked at. One cheap check per tick, never a busy loop.</summary>
    public static readonly TimeSpan EaseOffPollInterval = TimeSpan.FromSeconds(3);

    private EaseOffLeases _leases = null!;
    private ITimer? _easeOffTimer;
    private readonly Lock _easeLock = new();
    private string? _easedReason;
    private bool _tuningApplied;
    private bool _appliedEased;
    private int _appliedCeiling = -1;
    private ProcessPriorityClass _appliedPriority = ProcessPriorityClass.Normal;

    /// <summary>The reason the host is easing off now (fullscreen, gpu_busy, battery or app_busy), or null.</summary>
    public string? EasedOffNow => _easedReason;

    private void StartEaseOffTimer()
    {
        _leases = new EaseOffLeases(_o.Time);
        if (_o.Settings is not null)
        {
            _easeOffTimer = _o.Time.CreateTimer(_ => ReevaluateEaseOff(), null, EaseOffPollInterval, EaseOffPollInterval);
        }
    }

    /// <summary>First flag that is both on and currently true, in the order app_busy, fullscreen, gpu_busy, battery.</summary>
    public static string? ChooseReason(IReadOnlyCollection<string> flags, bool appBusy, Func<bool> fullscreen,
        Func<bool> gpuBusy, Func<bool> battery)
    {
        if (flags.Contains(EaseOffReasons.AppBusy) && appBusy)
        {
            return EaseOffReasons.AppBusy;
        }

        if (flags.Contains(EaseOffReasons.Fullscreen) && fullscreen())
        {
            return EaseOffReasons.Fullscreen;
        }

        if (flags.Contains(EaseOffReasons.GpuBusy) && gpuBusy())
        {
            return EaseOffReasons.GpuBusy;
        }

        return flags.Contains(EaseOffReasons.Battery) && battery() ? EaseOffReasons.Battery : null;
    }

    private static bool Safe(Func<bool>? check)
    {
        try
        {
            return check?.Invoke() == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks at the leases and the screen and power state, and eases the engines off or back. Only a flag that is on
    /// can ease off, and only the checks for flags that are on are made. Safe to call from any thread.
    /// </summary>
    public string? ReevaluateEaseOff()
    {
        var limits = _limits;
        if (limits is null)
        {
            return null;
        }

        var sensors = _o.EaseOffSensors;
        var reason = ChooseReason(limits.EaseOffFlags, _leases.Any,
            () => Safe(sensors is null ? null : sensors.IsFullscreen),
            () => Safe(sensors is null ? null : sensors.IsGpuBusy),
            () => Safe(sensors is not null ? sensors.IsOnBattery : _o.Hardware is { } hw ? () => hw.IsOnBattery : null));
        ApplyTuning(limits, reason);
        return reason;
    }

    /// <summary>
    /// Priority and processor ceiling for the engines. Easing off puts the engine at the lowest priority and at most
    /// half the processor; going back restores the saved priority and the user's own ceiling. Only changes are sent.
    /// </summary>
    private void ApplyTuning(PerformanceLimits limits, string? reason)
    {
        lock (_easeLock)
        {
            _easedReason = reason;
            var eased = reason is not null;
            var ceiling = PerformanceModel.EffectiveCeiling(limits.CpuCeilingPercent, eased);
            var priority = eased ? ProcessPriorityClass.Idle
                : limits.LowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            if (_tuningApplied && _appliedEased == eased && _appliedCeiling == ceiling && _appliedPriority == priority)
            {
                return;
            }

            _tuningApplied = true;
            _appliedEased = eased;
            _appliedCeiling = ceiling;
            _appliedPriority = priority;
            foreach (var sup in _o.Supervisors.Values)
            {
                sup.SetPriority(priority);
                sup.SetCpuCeiling(ceiling);
            }
        }
    }

    // ---- POST / DELETE /host/ease-off -----------------------------------------------------------

    private async Task EaseOffSetAsync(ApiContext ctx)
    {
        var principal = ctx.Principal ?? throw new InvalidOperationException("Authenticated route without a caller.");
        var body = ctx.ReadJson(AiHostJsonContext.Default.EaseOffRequest);
        if (body.TtlMs <= 0)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "ttl_ms is required: how long the request lasts unless renewed.");
        }

        if (!_leases.Set(principal.AppId, body.Reason, body.TtlMs, body.ReservedBytes))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "Too many apps have asked the host to ease off.");
        }

        ReevaluateEaseOff();
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private async Task EaseOffClearAsync(ApiContext ctx)
    {
        var principal = ctx.Principal ?? throw new InvalidOperationException("Authenticated route without a caller.");
        _leases.Clear(principal.AppId); // only the caller's own request; ending none is not an error
        ReevaluateEaseOff();
        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }
}
