using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Welcome flow in three steps: what the app does, the recommended model, and where to find the app.</summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    public const int StepWelcome = 0;
    public const int StepModel = 1;
    public const int StepWhere = 2;
    public const int StepCount = 3;

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CatalogueModelSummary? _model;
    private HostSettings _settings = new();
    private bool _startupTouched;

    public FirstRunViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        shell.Host.DownloadProgress += OnProgress;
    }

    [ObservableProperty] private int _step = StepWelcome;
    [ObservableProperty] private bool _checking = true;
    [ObservableProperty] private string _memoryLine = "";
    [ObservableProperty] private string _graphicsLine = "";
    [ObservableProperty] private string _coresLine = "";
    [ObservableProperty] private string _diskLine = "";
    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _modelReason = "";
    [ObservableProperty] private string _modelSize = "";
    [ObservableProperty] private string _existingLine = "";
    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private bool _downloadStarted;
    [ObservableProperty] private bool _downloadDone;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _downloadFailed;
    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private string _error = "";

    /// <summary>Raised when the window should close, after Finish or Skip. The window then marks the welcome as done.</summary>
    public event Action? CloseRequested;

    public bool HasExisting => !string.IsNullOrEmpty(ExistingLine);
    partial void OnExistingLineChanged(string value) => OnPropertyChanged(nameof(HasExisting));

    public bool OnWelcome => Step == StepWelcome;
    public bool OnModel => Step == StepModel;
    public bool OnWhere => Step == StepWhere;
    public bool CanGoBack => Step > StepWelcome;
    public bool IsLast => Step == StepWhere;

    /// <summary>The check is over, a model fits this PC and the download has not started.</summary>
    public bool ShowOffer => !Checking && HasModel && !DownloadStarted;

    /// <summary>The check is over and nothing fits this PC (or the catalogue is missing).</summary>
    public bool ShowNoModel => !Checking && !HasModel;

    public bool ShowProgress => DownloadStarted;
    public bool ShowRetry => DownloadFailed;
    /// <summary>"Recommended: Medium model": a friendly label that leads, with the technical name and size below it.</summary>
    [ObservableProperty] private string _modelLabel = "";

    /// <summary>Technical name and size on one quiet line.</summary>
    public string ModelDetail => ModelName.Length == 0 ? ModelSize : _s.Format("Vm_WelcomeDetail", ModelName, ModelSize);

    [ObservableProperty] private bool _choseLater;

    /// <summary>Shown on the last step after "Choose later", unless a download was started.</summary>
    public bool ShowLaterNote => ChoseLater && !DownloadStarted;

    partial void OnChoseLaterChanged(bool value) => OnPropertyChanged(nameof(ShowLaterNote));
    partial void OnModelNameChanged(string value) => OnPropertyChanged(nameof(ModelDetail));
    partial void OnModelSizeChanged(string value) => OnPropertyChanged(nameof(ModelDetail));

    public bool HasError => Error.Length > 0;

    public string NextText => _s.Get(IsLast ? "Vm_WelcomeFinish" : "Vm_WelcomeNext");

    /// <summary>"Step 2 of 3", read out by screen readers.</summary>
    public string StepLine => _s.Format("Vm_StepOf", Math.Clamp(Step, 0, StepCount - 1) + 1, StepCount);

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(StepLine));
        OnPropertyChanged(nameof(OnWelcome));
        OnPropertyChanged(nameof(OnModel));
        OnPropertyChanged(nameof(OnWhere));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLast));
        OnPropertyChanged(nameof(NextText));
    }

    partial void OnCheckingChanged(bool value) { OnPropertyChanged(nameof(ShowOffer)); OnPropertyChanged(nameof(ShowNoModel)); }
    partial void OnHasModelChanged(bool value) { OnPropertyChanged(nameof(ShowOffer)); OnPropertyChanged(nameof(ShowNoModel)); }
    partial void OnDownloadStartedChanged(bool value) { OnPropertyChanged(nameof(ShowLaterNote)); OnPropertyChanged(nameof(ShowOffer)); OnPropertyChanged(nameof(ShowProgress)); }
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnDownloadFailedChanged(bool value) => OnPropertyChanged(nameof(ShowRetry));

    public async Task LoadAsync()
    {
        try
        {
            _ui.Post(() =>
            {
                Checking = true;
                MemoryLine = _s.Get("Vm_HwChecking");
                GraphicsLine = "";
                CoresLine = "";
            });
            var snap = await _shell.Host.GetStatusAsync().ConfigureAwait(false);
            // The hardware check runs in the host in the background; wait for it so the numbers match the next step.
            for (var tries = 0; tries < 40 && snap.Status is { Hardware.RamBytes: <= 0 }; tries++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                snap = await _shell.Host.GetStatusAsync().ConfigureAwait(false);
            }

            var view = await _shell.Host.ListModelsAsync().ConfigureAwait(false);
            var settings = await _shell.Host.GetSettingsAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                _settings = settings;
                if (snap.Status is { } st && st.Hardware.RamBytes > 0)
                {
                    var hw = st.Hardware;
                    MemoryLine = _s.Format("Vm_HwMemory", Fmt.Bytes(hw.RamBytes));
                    var gpu = hw.Gpus.FirstOrDefault();
                    GraphicsLine = _s.Format("Vm_HwGraphics", gpu is null ? _s.Get("Vm_NoGpu") : $"{gpu.Name} ({Fmt.Bytes(gpu.VramBytes)})");
                    CoresLine = _s.Format("Vm_HwCores", hw.CpuCores);
                }
                else
                {
                    MemoryLine = _s.Format("Vm_HwMemory", _s.Get("Vm_HwUnknown"));
                }
                DiskLine = _s.Format("Vm_HwDisk", Fmt.Bytes(view.FreeBytes));
                _model = view.Models.FirstOrDefault(m => m.Recommended) ?? view.Models.FirstOrDefault(m => m.FitsThisPc);
                if (_model is not null)
                {
                    ModelName = _model.DisplayName;
                    ModelReason = _model.Reason ?? "";
                    ModelSize = _s.Format("Vm_WelcomeSize", Fmt.Bytes(_model.SizeBytes));
                    ModelLabel = _s.Format("Vm_WelcomeRecommended", _s.Get(SizeClass(_model.SizeBytes)));
                    ExistingLine = view.ExistingCopyIds.Contains(_model.Id) ? _s.Format("Vm_ExistingCopy", _model.DisplayName) : "";
                }
                HasModel = _model is not null;
                OnPropertyChanged(nameof(RecommendedTip));
                Checking = false;
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Error = _s.Get("Vm_NoHostTitle"); Checking = false; });
        }
    }

    /// <summary>Small, medium or large, by download size: a name people understand.</summary>
    public static string SizeClass(long bytes) => bytes < 4_000_000_000 ? "Vm_WelcomeSmall" : bytes < 12_000_000_000 ? "Vm_WelcomeMedium" : "Vm_WelcomeLarge";

    /// <summary>Moves on without downloading; the last step reminds that a model can be picked later.</summary>
    [RelayCommand]
    private void ChooseLater()
    {
        ChoseLater = true;
        if (Step < StepWhere) Step++;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > StepWelcome) Step--;
    }

    /// <summary>Next on the first two steps; Finish on the last.</summary>
    [RelayCommand]
    private async Task NextAsync()
    {
        if (Step < StepWhere) Step++;
        else await FinishAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_model is null) return;
        DownloadFailed = false;
        DownloadStarted = true;
        ProgressText = _s.Get("Vm_Queued");
        try
        {
            await _shell.Host.StartDownloadAsync(_model.Id).ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { DownloadFailed = true; Error = _s.Get("Vm_NoHostTitle"); });
        }
    }

    [RelayCommand]
    private Task RetryAsync() => DownloadAsync();

    private void OnProgress(DownloadProgressEvent e) => _ui.Post(() =>
    {
        if (_model is null || e.CatalogueId != _model.Id) return;
        Percent = e.BytesTotal > 0 ? 100.0 * e.BytesDone / e.BytesTotal : 0;
        switch (e.State)
        {
            case "done":
                ProgressText = _s.Get("Vm_DownloadDone");
                DownloadDone = true;
                break;
            case "failed":
                DownloadFailed = true;
                ProgressText = _s.Format("Vm_DownloadFailed", e.Message ?? "");
                break;
            case "verifying":
                ProgressText = _s.Get("Vm_Verifying");
                break;
            default:
                ProgressText = e.EtaSeconds is { } eta
                    ? _s.Format("Vm_DownloadEta", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal), Math.Max(1, (int)Math.Ceiling(eta / 60)))
                    : _s.Format("Vm_DownloadProgress", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal));
                break;
        }
    });

    /// <summary>The pre-ticked crash-report option. Unticking is one click and takes effect at once.</summary>
    public bool SendCrashReports => _shell.State.SendCrashReports;

    public Task SetSendCrashReportsAsync(bool on) => _shell.SetSendCrashReportsAsync(on);

    /// <summary>Called when the person flips the switch: the choice is saved straight away, so Skip keeps it.</summary>
    public async Task SetStartWithWindowsAsync(bool on)
    {
        _startupTouched = true;
        StartWithWindows = on;
        await SaveStartupAsync().ConfigureAwait(false);
    }

    private async Task SaveStartupAsync()
    {
        try
        {
            if (StartWithWindows == _settings.StartWithWindows) return;
            var next = new HostSettings
            {
                StartWithWindows = StartWithWindows,
                Backend = _settings.Backend,
                ModelsFolder = _settings.ModelsFolder,
                MaxMemoryPercent = _settings.MaxMemoryPercent,
                Preset = _settings.Preset,
                MemoryLimitBytes = _settings.MemoryLimitBytes,
                KeepFreeBytes = _settings.KeepFreeBytes,
                Compute = _settings.Compute,
                GpuMemoryLimitBytes = _settings.GpuMemoryLimitBytes,
                CpuThreads = _settings.CpuThreads,
                Priority = _settings.Priority,
                PreventSleepWhileDownloading = _settings.PreventSleepWhileDownloading,
                SendCrashReports = _shell.State.SendCrashReports,
                BandwidthCapBytesPerSecond = _settings.BandwidthCapBytesPerSecond,
                MutedNotifications = _settings.MutedNotifications,
                Remote = _settings.Remote,
                Kinds = _settings.Kinds,
            };
            await _shell.Host.SetSettingsAsync(next).ConfigureAwait(false);
            _settings = next;
        }
        catch (HostUnavailableException)
        {
        }
    }

    /// <summary>Finishing keeps the recommended default (start with Windows) unless the person already chose, then closes.</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        if (!_startupTouched) await SaveStartupAsync().ConfigureAwait(false);
        _ui.Post(() => CloseRequested?.Invoke());
    }

    /// <summary>Spoken and shown as the tooltip: what "Start with recommended settings" will do, with the download size.</summary>
    public string RecommendedTip => _model is null
        ? _s.Get("Vm_WelcomeRecommendedTipNoModel")
        : _s.Format("Vm_WelcomeRecommendedTip", Fmt.Bytes(_model.SizeBytes));

    /// <summary>Starts the recommended model download (it carries on in the host), keeps start with Windows on, and closes.</summary>
    [RelayCommand]
    private async Task StartRecommendedAsync()
    {
        if (_model is not null && !DownloadStarted) await DownloadAsync().ConfigureAwait(false);
        StartWithWindows = true;
        await SaveStartupAsync().ConfigureAwait(false);
        _ui.Post(() => CloseRequested?.Invoke());
    }

    /// <summary>Leaves the welcome without finishing (a download in progress carries on in the host). Choices already made stay.</summary>
    [RelayCommand]
    private void Skip() => CloseRequested?.Invoke();

    public void Dispose() => _shell.Host.DownloadProgress -= OnProgress;
}
