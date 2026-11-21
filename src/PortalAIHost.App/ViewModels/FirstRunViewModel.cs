using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>First-run flow: check this PC, pick the recommended model, download it, choose start with Windows.</summary>
public sealed partial class FirstRunViewModel : ObservableObject, IDisposable
{
    public const int StepCheck = 0;
    public const int StepModel = 1;
    public const int StepDownload = 2;
    public const int StepStartup = 3;

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CatalogueModelSummary? _model;
    private HostSettings _settings = new();

    public FirstRunViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        shell.Host.DownloadProgress += OnProgress;
    }

    [ObservableProperty] private int _step = StepCheck;
    [ObservableProperty] private string _memoryLine = "";
    [ObservableProperty] private string _graphicsLine = "";
    [ObservableProperty] private string _coresLine = "";
    [ObservableProperty] private string _diskLine = "";
    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _modelReason = "";
    [ObservableProperty] private string _modelSize = "";
    [ObservableProperty] private string _existingLine = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _downloadFailed;
    [ObservableProperty] private bool _startWithWindows = true;
    [ObservableProperty] private string _error = "";

    public bool OnCheck => Step == StepCheck;
    public bool OnModel => Step == StepModel;
    public bool OnDownload => Step == StepDownload;
    public bool OnStartup => Step == StepStartup;

    public const int StepCount = 4;

    /// <summary>"Step 2 of 4", read out by screen readers.</summary>
    public string StepLine => _s.Format("Vm_StepOf", Math.Clamp(Step, 0, StepCount - 1) + 1, StepCount);

    /// <summary>Step dots: a filled dot for the current step and open dots for the others.</summary>
    public string StepDots => string.Join(" ", Enumerable.Range(0, StepCount).Select(i => i == Step ? "●" : "○"));

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(StepLine));
        OnPropertyChanged(nameof(StepDots));
        OnPropertyChanged(nameof(OnCheck));
        OnPropertyChanged(nameof(OnModel));
        OnPropertyChanged(nameof(OnDownload));
        OnPropertyChanged(nameof(OnStartup));
    }

    public async Task LoadAsync()
    {
        try
        {
            var snap = await _shell.Host.GetStatusAsync().ConfigureAwait(false);
            var view = await _shell.Host.ListModelsAsync().ConfigureAwait(false);
            var settings = await _shell.Host.GetSettingsAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                _settings = settings;
                if (snap.Status is { } st)
                {
                    var hw = st.Hardware;
                    MemoryLine = _s.Format("Vm_HwMemory", Fmt.Bytes(hw.RamBytes));
                    var gpu = hw.Gpus.FirstOrDefault();
                    GraphicsLine = _s.Format("Vm_HwGraphics", gpu is null ? _s.Get("Vm_NoGpu") : $"{gpu.Name} ({Fmt.Bytes(gpu.VramBytes)})");
                    CoresLine = _s.Format("Vm_HwCores", hw.CpuCores);
                }
                DiskLine = _s.Format("Vm_HwDisk", Fmt.Bytes(view.FreeBytes));
                _model = view.Models.FirstOrDefault(m => m.Recommended) ?? view.Models.FirstOrDefault(m => m.FitsThisPc);
                if (_model is not null)
                {
                    ModelName = _model.DisplayName;
                    ModelReason = _model.Reason ?? "";
                    ModelSize = Fmt.Bytes(_model.SizeBytes);
                    ExistingLine = view.ExistingCopyIds.Contains(_model.Id) ? _s.Format("Vm_ExistingCopy", _model.DisplayName) : "";
                }
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Error = _s.Get("Vm_NoHostTitle"));
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (Step < StepModel) Step++;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_model is null) return;
        DownloadFailed = false;
        Step = StepDownload;
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
                if (Step == StepDownload) Step = StepStartup;
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

    /// <summary>Saves the start-with-Windows choice, makes the model the active one, and closes the flow.</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        try
        {
            if (StartWithWindows != _settings.StartWithWindows)
            {
                await _shell.Host.SetSettingsAsync(new HostSettings
                {
                    StartWithWindows = StartWithWindows,
                    Backend = _settings.Backend,
                    ModelsFolder = _settings.ModelsFolder,
                    MaxMemoryPercent = _settings.MaxMemoryPercent,
                    PreventSleepWhileDownloading = _settings.PreventSleepWhileDownloading,
                    BandwidthCapBytesPerSecond = _settings.BandwidthCapBytesPerSecond,
                    Kinds = _settings.Kinds,
                }).ConfigureAwait(false);
            }
        }
        catch (HostUnavailableException)
        {
        }
        _ui.Post(_shell.CompleteFirstRun);
    }

    /// <summary>Leaves the flow without finishing (a download in progress carries on in the host).</summary>
    [RelayCommand]
    private void Skip() => _shell.CompleteFirstRun();

    public void Dispose() => _shell.Host.DownloadProgress -= OnProgress;
}
