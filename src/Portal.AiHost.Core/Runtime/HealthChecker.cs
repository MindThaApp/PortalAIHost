using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Runtime;

/// <summary>One engine build the host ships (cpu or vulkan) and whether its program file is there.</summary>
public sealed record EngineFileStatus(string Flavour, bool Present);

/// <summary>What the host knows about the active chat model, for the health checks.</summary>
public sealed record HealthModelInfo(
    string Id, string DisplayName, int MissingFiles, string? DamagedReason, EngineState EngineState);

/// <summary>
/// Everything the health checks look at, gathered by the runtime from the places that already know it (engine
/// folder, hardware probe, performance view, model notes). The checks themselves are pure, so each one is tested
/// with plain values.
/// </summary>
public sealed record HealthInputs
{
    /// <summary>True when the HTTP API is bound (always true when the request came over HTTP).</summary>
    public bool ApiListening { get; init; } = true;

    /// <summary>The engine builds that should be next to the host, or null when this host cannot say.</summary>
    public IReadOnlyList<EngineFileStatus>? EngineFiles { get; init; }

    public bool GpuAvailable { get; init; }
    public string? GpuName { get; init; }

    /// <summary>The "Run on" setting: auto, gpu or cpu.</summary>
    public string Compute { get; init; } = "auto";

    /// <summary>gpu or cpu: where the model really runs under the limits.</summary>
    public string ComputeEffective { get; init; } = "cpu";

    /// <summary>The active chat model, or null when none is chosen.</summary>
    public HealthModelInfo? Model { get; init; }

    /// <summary>Free bytes on the drive of the models folder, or null when unknown.</summary>
    public long? StoreFreeBytes { get; init; }

    /// <summary>fits_fully, fits_partial, cpu_only, needs_smaller_model or too_big; null with no model or no plan.</summary>
    public string? Fit { get; init; }

    public string? FitModel { get; init; }
    public string? SmallerModel { get; init; }

    /// <summary>Reason the host is easing off now, or null.</summary>
    public string? EasedOffNow { get; init; }

    public bool MemoryCapActive { get; init; } = true;
    public string? MemoryCapReason { get; init; }

    public string SettingsLink { get; init; } = "portal-ai-host:settings/performance";
}

/// <summary>The health checks. Pure functions of <see cref="HealthInputs"/>; no check changes anything.</summary>
public static class HealthChecker
{
    /// <summary>Under this much free space a download is refused (the downloader's own headroom).</summary>
    public const long DiskWarnBytes = Recommender.DiskFixedHeadroom;

    /// <summary>Under this much free space the drive is nearly full.</summary>
    public const long DiskFailBytes = 256L << 20;

    public static HealthReport Evaluate(HealthInputs i)
    {
        var checks = new List<HealthCheck>
        {
            Service(i), Engine(i), Graphics(i), Model(i), Disk(i), Memory(i), EaseOff(i), MemoryCap(i),
        };
        var worst = checks.Any(c => c.Status == HealthStatuses.Fail) ? HealthStatuses.Fail
            : checks.Any(c => c.Status == HealthStatuses.Warn) ? HealthStatuses.Warn
            : HealthStatuses.Ok;
        return new HealthReport
        {
            Status = worst,
            NeedsAttention = checks.Count(c => c.Status != HealthStatuses.Ok),
            Checks = checks,
        };
    }

    private static HealthCheck Make(string id, string status, string title, string detail,
        string? fix = null, string? target = null) =>
        new() { Id = id, Status = status, Title = title, Detail = detail, FixAction = fix, FixTarget = target };

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

    internal static HealthCheck Service(HealthInputs i) => i.ApiListening
        ? Make(HealthIds.Service, HealthStatuses.Ok, "Host service", "Portal AI Host is running and apps can connect.")
        : Make(HealthIds.Service, HealthStatuses.Fail, "Host service",
            "Portal AI Host is running, but apps cannot connect because its address could not be opened. Restart Portal AI Host.");

    internal static HealthCheck Engine(HealthInputs i)
    {
        const string title = "AI engine";
        if (i.EngineFiles is not { Count: > 0 } files)
        {
            return Make(HealthIds.Engine, HealthStatuses.Ok, title, "The AI engine is part of this host.");
        }

        var cpu = files.FirstOrDefault(f => f.Flavour == "cpu");
        if (cpu is null || !cpu.Present)
        {
            return Make(HealthIds.Engine, HealthStatuses.Fail, title,
                "The AI engine files are missing, so no model can run. Reinstall Portal AI Host from the Microsoft Store.");
        }

        return files.All(f => f.Present)
            ? Make(HealthIds.Engine, HealthStatuses.Ok, title, "The AI engine files are in place.")
            : Make(HealthIds.Engine, HealthStatuses.Warn, title,
                "The graphics engine files are missing, so models run on the processor only. Reinstalling Portal AI Host from the Microsoft Store restores them.");
    }

    internal static HealthCheck Graphics(HealthInputs i)
    {
        const string title = "Graphics card";
        if (i.Compute == "cpu")
        {
            return Make(HealthIds.Graphics, HealthStatuses.Ok, title, "Set to run on the processor only.",
                HealthFixes.OpenPerformance, i.SettingsLink);
        }

        if (!i.GpuAvailable)
        {
            return Make(HealthIds.Graphics, HealthStatuses.Warn, title,
                "No supported graphics card was found, so models run on the processor. That works, but replies are slower.");
        }

        var name = i.GpuName ?? "The graphics card";
        return i.ComputeEffective == "cpu"
            ? Make(HealthIds.Graphics, HealthStatuses.Warn, title,
                $"{name} is available, but the current model runs on the processor with these limits. Raising the graphics memory limit may speed it up.",
                HealthFixes.OpenPerformance, i.SettingsLink)
            : Make(HealthIds.Graphics, HealthStatuses.Ok, title, $"{name} is used to speed up replies.");
    }

    internal static HealthCheck Model(HealthInputs i)
    {
        const string title = "Chat model";
        if (i.Model is not { } m)
        {
            return Make(HealthIds.Model, HealthStatuses.Warn, title,
                "No chat model is chosen yet, so apps cannot ask questions.", HealthFixes.OpenModels);
        }

        if (m.DamagedReason is { } damaged)
        {
            return Make(HealthIds.Model, HealthStatuses.Fail, title,
                $"A check found a problem with {m.DisplayName}: {damaged} Check the files again, or download it again.",
                HealthFixes.CheckFiles, m.Id);
        }

        if (m.MissingFiles > 0)
        {
            return Make(HealthIds.Model, HealthStatuses.Fail, title,
                $"{m.DisplayName} has files that are no longer on this PC. Check the files to see which ones.",
                HealthFixes.CheckFiles, m.Id);
        }

        return m.EngineState switch
        {
            EngineState.Failed => Make(HealthIds.Model, HealthStatuses.Fail, title,
                $"{m.DisplayName} could not start. Restarting the engine often fixes this.", HealthFixes.RestartEngine, m.Id),
            EngineState.Ready => Make(HealthIds.Model, HealthStatuses.Ok, title, $"{m.DisplayName} is loaded and ready."),
            EngineState.Starting or EngineState.Loading =>
                Make(HealthIds.Model, HealthStatuses.Ok, title, $"{m.DisplayName} is loading."),
            _ => Make(HealthIds.Model, HealthStatuses.Ok, title,
                $"{m.DisplayName} is ready and loads when an app asks for it.", HealthFixes.LoadModel, m.Id),
        };
    }

    internal static HealthCheck Disk(HealthInputs i)
    {
        const string title = "Disk space";
        if (i.StoreFreeBytes is not { } free)
        {
            return Make(HealthIds.Disk, HealthStatuses.Ok, title, "Free space on the models drive could not be read.");
        }

        if (free < DiskFailBytes)
        {
            return Make(HealthIds.Disk, HealthStatuses.Fail, title,
                $"The drive with your models has only {Gb(free)} free. Free up space, or move the models to another drive in Settings.");
        }

        return free < DiskWarnBytes
            ? Make(HealthIds.Disk, HealthStatuses.Warn, title,
                $"The drive with your models has {Gb(free)} free, which is too little to download another model.")
            : Make(HealthIds.Disk, HealthStatuses.Ok, title, $"{Gb(free)} free on the models drive.");
    }

    internal static HealthCheck Memory(HealthInputs i)
    {
        const string title = "Memory";
        var name = i.FitModel ?? "The chat model";
        return i.Fit switch
        {
            PerformanceFits.TooBig => Make(HealthIds.Memory, HealthStatuses.Fail, title,
                $"{name} needs more memory than this PC can give it under your memory limit. Raise the limit or choose a smaller model.",
                HealthFixes.OpenPerformance, i.SettingsLink),
            PerformanceFits.NeedsSmallerModel => Make(HealthIds.Memory, HealthStatuses.Fail, title,
                i.SmallerModel is { } s
                    ? $"{name} does not fit in the memory you allow. {s} would."
                    : $"{name} does not fit in the memory you allow.",
                HealthFixes.OpenPerformance, i.SettingsLink),
            null => Make(HealthIds.Memory, HealthStatuses.Ok, title, "There is enough free memory."),
            _ => Make(HealthIds.Memory, HealthStatuses.Ok, title, $"{name} fits in the memory you allow."),
        };
    }

    internal static HealthCheck EaseOff(HealthInputs i)
    {
        const string title = "Easing off";
        if (i.EasedOffNow is not { } reason)
        {
            return Make(HealthIds.EaseOff, HealthStatuses.Ok, title, "The AI is running at full speed for your settings.");
        }

        var why = reason switch
        {
            EaseOffReasons.Fullscreen => "a game or full-screen program is running",
            EaseOffReasons.GpuBusy => "another program is using the graphics card",
            EaseOffReasons.Battery => "the PC is on battery",
            EaseOffReasons.AppBusy => "a Portal app is busy with live work",
            _ => "your settings ask for it",
        };
        return Make(HealthIds.EaseOff, HealthStatuses.Warn, title,
            $"The AI is running slower on purpose because {why}. It speeds up again by itself.",
            HealthFixes.OpenPerformance, i.SettingsLink);
    }

    internal static HealthCheck MemoryCap(HealthInputs i) => i.MemoryCapActive
        ? Make(HealthIds.MemoryCap, HealthStatuses.Ok, "Memory cap", "The memory limit is being kept.")
        : Make(HealthIds.MemoryCap, HealthStatuses.Warn, "Memory cap",
            i.MemoryCapReason ?? "The AI engine is running without the memory cap.",
            HealthFixes.OpenPerformance, i.SettingsLink);
}
