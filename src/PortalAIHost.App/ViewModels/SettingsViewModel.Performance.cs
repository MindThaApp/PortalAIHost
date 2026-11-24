using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// Settings > Performance. Visual first: every control and label changes at once; then, after a short pause, the host
/// previews the fit and applies the limits, and an InfoBar tells what happened. There is no Apply button.
/// Moving any slider or choice switches the preset to Custom.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const int QuietIndex = 0;
    public const int BalancedIndex = 1;
    public const int FastIndex = 2;
    public const int CustomIndex = 3;

    private const long Gb = 1L << 30;
    private static readonly string[] PresetNames = [PerformancePresets.Quiet, PerformancePresets.Balanced, PerformancePresets.Fast, PerformancePresets.Custom];
    private static readonly string[] ComputeNames = ["auto", "gpu", "cpu"];

    /// <summary>How long the controls rest before the host is asked (milliseconds).</summary>
    public int PerformanceApplyDelayMs { get; set; } = 400;

    private PerformanceView? _perf;
    private bool _perfLoading;
    private bool _perfTouched;
    private int _perfVersion;

    public IReadOnlyList<string> PresetChoices { get; private set; } = [];
    public IReadOnlyList<string> RunOnChoices { get; private set; } = [];
    public IReadOnlyList<string> PriorityChoices { get; private set; } = [];

    [ObservableProperty] private bool _performanceAvailable;
    [ObservableProperty] private int _presetIndex = BalancedIndex;
    [ObservableProperty] private string _presetHelp = "";
    [ObservableProperty] private double _memoryGb = 51;
    [ObservableProperty] private double _memoryMinGb = 2;
    [ObservableProperty] private double _memoryMaxGb = 56;
    [ObservableProperty] private string _memoryLabel = "";
    [ObservableProperty] private string _memoryRangeText = "";
    [ObservableProperty] private double _keepFreeGb = 8;
    [ObservableProperty] private double _keepFreeMinGb = 2;
    [ObservableProperty] private double _keepFreeMaxGb = 32;
    [ObservableProperty] private string _keepFreeLabel = "";
    [ObservableProperty] private int _runOnIndex;
    [ObservableProperty] private bool _runOnEnabled;
    [ObservableProperty] private string _runOnHelp = "";
    [ObservableProperty] private string _gpuNote = "";
    [ObservableProperty] private bool _hasGpuNote;
    [ObservableProperty] private bool _gpuMemoryVisible;
    [ObservableProperty] private double _gpuMemoryGb = 7.5;
    [ObservableProperty] private double _gpuMemoryMinGb = 1;
    [ObservableProperty] private double _gpuMemoryMaxGb = 7.5;
    [ObservableProperty] private string _gpuMemoryLabel = "";
    [ObservableProperty] private double _coresValue = 10;
    [ObservableProperty] private double _coresMin = 1;
    [ObservableProperty] private double _coresMax = 11;
    [ObservableProperty] private string _coresLabel = "";
    [ObservableProperty] private int _priorityIndex;
    [ObservableProperty] private string _priorityHelp = "";
    [ObservableProperty] private double _ceilingValue = 100;
    [ObservableProperty] private string _ceilingLabel = "";
    [ObservableProperty] private bool _easeOffFullscreen = true;
    [ObservableProperty] private bool _easeOffBattery = true;
    [ObservableProperty] private bool _easeOffAppBusy = true;
    // gpu_busy has no reliable reading yet, so the page does not offer it; the saved flag is kept as it is.
    private bool _easeOffGpuBusy;
    [ObservableProperty] private string _fitText = "";
    [ObservableProperty] private bool _hasFit;
    [ObservableProperty] private string _performanceMessage = "";
    [ObservableProperty] private int _performanceMessageKind;
    [ObservableProperty] private bool _hasPerformanceMessage;

    partial void OnPerformanceMessageChanged(string value) => HasPerformanceMessage = value.Length > 0;
    partial void OnGpuNoteChanged(string value) => HasGpuNote = value.Length > 0;
    partial void OnFitTextChanged(string value) => HasFit = value.Length > 0;

    private void InitPerformance()
    {
        PresetChoices = Names("Vm_Perf_", "Quiet", "Balanced", "Fast", "Custom");
        RunOnChoices = Names("Vm_Perf_Run_", "Auto", "Gpu", "Cpu");
        PriorityChoices = Names("Vm_Perf_Prio_", "Low", "Normal");
        RefreshPerformanceLabels();
    }

    // ---- loading ------------------------------------------------------------------------------

    private async Task LoadPerformanceAsync()
    {
        try
        {
            var view = await _shell.Host.GetPerformanceAsync().ConfigureAwait(false);
            _ui.Post(() => ShowPerformance(view));
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
            _ui.Post(() => PerformanceAvailable = false);
        }
    }

    /// <summary>Shows what the host reports, without sending anything back.</summary>
    private void ShowPerformance(PerformanceView view)
    {
        _perfLoading = true;
        _perf = view;
        var info = view.Info;
        var r = info.Ranges;
        MemoryMinGb = r.MemoryLimitBytes.Min / (double)Gb;
        MemoryMaxGb = r.MemoryLimitBytes.Max / (double)Gb;
        KeepFreeMinGb = r.KeepFreeBytes.Min / (double)Gb;
        KeepFreeMaxGb = r.KeepFreeBytes.Max / (double)Gb;
        CoresMin = r.CpuThreads.Min;
        CoresMax = r.CpuThreads.Max;
        if (r.GpuMemoryLimitBytes is { } g)
        {
            GpuMemoryMinGb = g.Min / (double)Gb;
            GpuMemoryMaxGb = g.Max / (double)Gb;
        }

        MemoryGb = info.MemoryLimitBytes / (double)Gb;
        KeepFreeGb = info.KeepFreeBytes / (double)Gb;
        GpuMemoryGb = (info.GpuMemoryLimitBytes ?? r.GpuMemoryLimitBytes?.Max ?? 0) / (double)Gb;
        CoresValue = info.CpuThreads;
        PresetIndex = Math.Max(0, Array.IndexOf(PresetNames, info.Preset));
        RunOnIndex = Math.Max(0, Array.IndexOf(ComputeNames, info.Compute));
        PriorityIndex = info.Priority == PerformancePriorities.Normal ? 1 : 0;
        CeilingValue = Math.Clamp(info.CpuCeilingPercent, 20, 100);
        EaseOffFullscreen = info.EaseOff.Contains(EaseOffReasons.Fullscreen);
        EaseOffBattery = info.EaseOff.Contains(EaseOffReasons.Battery);
        EaseOffAppBusy = info.EaseOff.Contains(EaseOffReasons.AppBusy);
        _easeOffGpuBusy = info.EaseOff.Contains(EaseOffReasons.GpuBusy);
        RunOnEnabled = r.GpuAvailable;
        GpuNote = r.GpuAvailable ? "" : view.GpuUnavailableReason ?? _s.Get("Vm_Perf_NoGpu");
        PerformanceAvailable = true;
        _perfTouched = false;
        _perfLoading = false;
        RefreshPerformanceLabels();
        ShowFit(view);
    }

    // ---- visual first: labels follow the controls at once -------------------------------------

    private void RefreshPerformanceLabels()
    {
        PresetHelp = _s.Get("Vm_Perf_Help_" + PresetNames[Math.Clamp(PresetIndex, 0, 3)]);
        MemoryLabel = MemoryLabelFor(MemoryGb);
        MemoryRangeText = _perf is null ? "" : _s.Format("Vm_Perf_MemoryRange", Fmt.Gb(MemoryMinGb), Fmt.Gb(MemoryMaxGb));
        KeepFreeLabel = _s.Format("Vm_Perf_Gb", Fmt.Gb(KeepFreeGb));
        GpuMemoryLabel = _s.Format("Vm_Perf_Gb", Fmt.Gb(GpuMemoryGb));
        CoresLabel = _s.Format("Vm_Perf_Cores", (int)Math.Round(CoresValue), _perf?.LogicalCores ?? (int)CoresMax + 1);
        RunOnHelp = _s.Get("Vm_Perf_RunHelp_" + ComputeNames[Math.Clamp(RunOnIndex, 0, 2)]);
        PriorityHelp = _s.Get(PriorityIndex == 1 ? "Vm_Perf_PrioHelp_Normal" : "Vm_Perf_PrioHelp_Low");
        CeilingLabel = CeilingValue >= 100 ? _s.Get("Vm_Perf_CeilingNone") : _s.Format("Vm_Perf_Percent", (int)Math.Round(CeilingValue));
        GpuMemoryVisible = (_perf?.Info.Ranges.GpuAvailable ?? false) && RunOnIndex != 2;
    }

    /// <summary>"16 GB · fits Qwen3 14B fully", from the stop the host worked out for this size.</summary>
    private string MemoryLabelFor(double gb)
    {
        var shown = Fmt.Gb(gb);
        var stops = _perf?.MemoryStops;
        if (stops is not { Count: > 0 }) return _s.Format("Vm_Perf_Gb", shown);
        var stop = stops.OrderBy(s => Math.Abs(s.Bytes / (double)Gb - gb)).First();
        if (stop.FitsModel is null) return _s.Format("Vm_Perf_StopNone", shown);
        return stop.Fit switch
        {
            PerformanceFits.FitsFully => _s.Format("Vm_Perf_StopFull", shown, stop.FitsModel),
            PerformanceFits.CpuOnly => _s.Format("Vm_Perf_StopCpu", shown, stop.FitsModel),
            _ => _s.Format("Vm_Perf_StopPartial", shown, stop.FitsModel),
        };
    }

    private void ShowFit(PerformanceView view)
    {
        FitText = view.Info.Fit switch
        {
            PerformanceFits.FitsFully => _s.Get("Vm_Perf_Fit_Full"),
            PerformanceFits.FitsPartial => _s.Get("Vm_Perf_Fit_Partial"),
            PerformanceFits.CpuOnly => _s.Get("Vm_Perf_Fit_Cpu"),
            PerformanceFits.NeedsSmallerModel => view.SmallerModel is null
                ? _s.Format("Vm_Perf_Fit_LittleMemory", view.FitModel ?? "")
                : _s.Format("Vm_Perf_Fit_Smaller", view.FitModel ?? "", view.SmallerModel),
            PerformanceFits.TooBig => _s.Get("Vm_Perf_Fit_TooBig"),
            _ => "",
        };
    }

    // ---- edits --------------------------------------------------------------------------------

    private void PerformanceEdited(bool toCustom = true)
    {
        if (_perfLoading || _perf is null) return;
        _perfTouched = true;
        if (toCustom && PresetIndex != CustomIndex)
        {
            _perfLoading = true;
            PresetIndex = CustomIndex;
            _perfLoading = false;
        }

        RefreshPerformanceLabels();
        _ = ApplyPerformanceSoonAsync();
    }

    partial void OnMemoryGbChanged(double value) => PerformanceEdited();
    partial void OnGpuMemoryGbChanged(double value) => PerformanceEdited();
    partial void OnCoresValueChanged(double value) => PerformanceEdited();
    partial void OnPriorityIndexChanged(int value) => PerformanceEdited();

    // The ceiling and the ease-off rules are not part of any preset, so they do not switch it to Custom.
    partial void OnCeilingValueChanged(double value) => PerformanceEdited(toCustom: false);
    partial void OnEaseOffFullscreenChanged(bool value) => PerformanceEdited(toCustom: false);
    partial void OnEaseOffBatteryChanged(bool value) => PerformanceEdited(toCustom: false);
    partial void OnEaseOffAppBusyChanged(bool value) => PerformanceEdited(toCustom: false);
    partial void OnRunOnIndexChanged(int value) => PerformanceEdited();

    partial void OnKeepFreeGbChanged(double value)
    {
        if (_perfLoading || _perf is null) return;
        // The memory range follows: it ends where "always leave free" begins.
        var ram = _perf.RamBytes / (double)Gb;
        _perfLoading = true;
        MemoryMaxGb = Math.Max(MemoryMinGb, ram - value);
        if (MemoryGb > MemoryMaxGb) MemoryGb = MemoryMaxGb;
        _perfLoading = false;
        PerformanceEdited();
    }

    partial void OnPresetIndexChanged(int value)
    {
        if (_perfLoading || _perf is null) return;
        _perfTouched = true;
        if (value is >= QuietIndex and <= FastIndex && _perf.Presets.TryGetValue(PresetNames[value], out var p))
        {
            _perfLoading = true;
            MemoryGb = p.MemoryLimitBytes / (double)Gb;
            CoresValue = p.CpuThreads;
            PriorityIndex = p.Priority == PerformancePriorities.Normal ? 1 : 0;
            RunOnIndex = 0;
            _perfLoading = false;
        }

        RefreshPerformanceLabels();
        _ = ApplyPerformanceSoonAsync();
    }

    /// <summary>Settings for the values on screen: the current ones with only the performance fields replaced.</summary>
    private void WritePerformance(HostSettings s)
    {
        if (!_perfTouched || _perf is null) return;
        var r = _perf.Info.Ranges;
        var compute = ComputeNames[Math.Clamp(RunOnIndex, 0, 2)];
        s.Preset = PresetNames[Math.Clamp(PresetIndex, 0, 3)];
        s.MemoryLimitBytes = BytesFor(MemoryGb, r.MemoryLimitBytes);
        s.KeepFreeBytes = BytesFor(KeepFreeGb, r.KeepFreeBytes);
        s.Compute = compute;
        s.Backend = compute; // the older setting follows, so the two never disagree
        s.GpuMemoryLimitBytes = r.GpuMemoryLimitBytes is { } g ? BytesFor(GpuMemoryGb, g) : null;
        s.CpuThreads = (int)Math.Round(CoresValue);
        s.Priority = PriorityIndex == 1 ? PerformancePriorities.Normal : PerformancePriorities.Low;
        s.CpuCeilingPercent = (int)Math.Clamp(Math.Round(CeilingValue / 5) * 5, 20, 100);
        s.EaseOff = EaseOffFlagsOnScreen();
    }

    private List<string> EaseOffFlagsOnScreen()
    {
        var on = new List<string>();
        if (EaseOffFullscreen) on.Add(EaseOffReasons.Fullscreen);
        if (_easeOffGpuBusy) on.Add(EaseOffReasons.GpuBusy);
        if (EaseOffBattery) on.Add(EaseOffReasons.Battery);
        if (EaseOffAppBusy) on.Add(EaseOffReasons.AppBusy);
        return EaseOffReasons.Normalize(on);
    }

    private static long BytesFor(double gb, NumberRange range)
    {
        var bytes = (long)Math.Round(gb * 2) * (Gb / 2);
        return Math.Clamp(bytes, range.Min, range.Max);
    }

    // ---- apply --------------------------------------------------------------------------------

    private async Task ApplyPerformanceSoonAsync()
    {
        var version = Interlocked.Increment(ref _perfVersion);
        var candidate = Build();
        try
        {
            if (PerformanceApplyDelayMs > 0) await Task.Delay(PerformanceApplyDelayMs).ConfigureAwait(false);
            if (version != Volatile.Read(ref _perfVersion)) return; // moved again: the later change wins

            // The fit line first, then the same settings are applied.
            var preview = await _shell.Host.GetPerformanceAsync(candidate).ConfigureAwait(false);
            if (version != Volatile.Read(ref _perfVersion)) return;
            _ui.Post(() => ShowFit(preview));

            var saved = await _shell.Host.SetSettingsAsync(candidate).ConfigureAwait(false);
            if (version != Volatile.Read(ref _perfVersion)) return;
            _ui.Post(() =>
            {
                _current = candidate;
                SayPerformance(ResultText(saved, preview), saved.PerformanceReloaded?.Unloaded.Count > 0 ? MessageWarning : MessageSuccess);
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => SayPerformance(_s.Get("Vm_SaveFailed"), MessageError));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => SayPerformance(ex.Message, MessageError));
        }
    }

    private string ResultText(SettingsSaved saved, PerformanceView preview)
    {
        var model = preview.FitModel ?? _s.Get("Vm_Perf_TheModel");
        if (saved.PerformanceReloaded is { Unloaded.Count: > 0 }) return _s.Format("Vm_Perf_Unloaded", model);
        if (saved.PerformanceReloaded is { Reloaded.Count: > 0 }) return _s.Format("Vm_Perf_Reloading", model);
        return _s.Get("Vm_Perf_Applied");
    }

    private void SayPerformance(string text, int kind)
    {
        PerformanceMessageKind = kind;
        PerformanceMessage = text;
    }

    /// <summary>Puts every performance choice back to the defaults for this PC.</summary>
    [RelayCommand]
    private async Task ResetPerformanceAsync()
    {
        if (_perf is null) return;
        Interlocked.Increment(ref _perfVersion); // drop any pending change
        var defaults = Build();
        defaults.Preset = PerformancePresets.Balanced;
        defaults.MemoryLimitBytes = null;
        defaults.KeepFreeBytes = null;
        defaults.Compute = "auto";
        defaults.Backend = "auto";
        defaults.GpuMemoryLimitBytes = null;
        defaults.CpuThreads = null;
        defaults.Priority = PerformancePriorities.Low;
        defaults.MaxMemoryPercent = 80;
        defaults.CpuCeilingPercent = 100;
        defaults.EaseOff = [.. EaseOffReasons.Defaults];
        try
        {
            var saved = await _shell.Host.SetSettingsAsync(defaults).ConfigureAwait(false);
            var view = await _shell.Host.GetPerformanceAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                _current = defaults;
                ShowPerformance(view);
                SayPerformance(ResultText(saved, view), MessageSuccess);
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => SayPerformance(_s.Get("Vm_SaveFailed"), MessageError));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => SayPerformance(ex.Message, MessageError));
        }
    }
}
