using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// Home page. Three situations: the host is not running, no model is installed yet (a guided panel with the
/// recommended model and a download button), and a model is installed (a status card).
/// </summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CatalogueModelSummary? _rec;
    private string _downloadState = "none";
    private int _loadVersion;
    private bool _loaded;

    public HomeViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        Live = new LiveViewModel(shell.Host, ui, strings);
        Health = new HealthViewModel(shell.Host, ui, strings);
        _shell.SnapshotChanged += OnSnapshot;
        _shell.ClientsChanged += OnClientsChanged;
        _shell.Host.DownloadProgress += OnProgress;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.Starting) or nameof(ShellViewModel.StartMessage)) Update();
        };
        Update();
    }

    /// <summary>Live activity, meters, Unload and Load, and the Today list.</summary>
    public LiveViewModel Live { get; }

    /// <summary>The "All good" / "N things need attention" card.</summary>
    public HealthViewModel Health { get; }

    // Which block is on screen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTryIt))]
    private bool _noHost;
    [ObservableProperty] private bool _showOnboarding;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTryIt))]
    private bool _showInstalled;

    /// <summary>The Try it button: a model is installed and the host is running.</summary>
    public bool CanTryIt => ShowInstalled && !NoHost;

    // Bars shared by every state.
    [ObservableProperty] private bool _safeMode;
    [ObservableProperty] private bool _hasStoreProblem;
    [ObservableProperty] private string _storeProblem = "";
    [ObservableProperty] private bool _upgradeAvailable;

    // Host not running.
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _startMessage = "";
    [ObservableProperty] private bool _canStart = true;

    // Installed status card.
    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _modelLine = "";
    [ObservableProperty] private string _idleLine = "";
    [ObservableProperty] private string _appsLine = "";
    [ObservableProperty] private string _queueLine = "";

    // First-run panel.
    [ObservableProperty] private bool _catalogueUnavailable;
    [ObservableProperty] private bool _loadingCatalogue = true;
    [ObservableProperty] private bool _hasRecommendation;
    [ObservableProperty] private string _recName = "";
    [ObservableProperty] private string _recSize = "";
    [ObservableProperty] private string _recReason = "";
    [ObservableProperty] private string _recAutoDownload = "";
    [ObservableProperty] private bool _notEnoughSpace;
    [ObservableProperty] private string _spaceLine = "";
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _downloadFailed;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _errorLine = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _hasNote;

    /// <summary>Reads the model list and the connected apps from the host. Call when the page opens.</summary>
    public async Task LoadAsync()
    {
        var version = Interlocked.Increment(ref _loadVersion);
        if (!_shell.Snapshot.Connected) return;
        _ = Health.RefreshAsync(force: true);
        ModelCatalogView? view = null;
        int? apps = null;
        var failed = false;
        try
        {
            view = await _shell.Host.ListModelsAsync().ConfigureAwait(false);
            apps = (await _shell.Host.ListClientsAsync().ConfigureAwait(false)).Count;
        }
        catch (HostUnavailableException)
        {
            return;
        }
        catch (HostRequestException)
        {
            failed = true;
        }
        _ui.Post(() =>
        {
            if (version != _loadVersion) return;
            _loaded = true;
            LoadingCatalogue = false;
            if (apps is { } n) AppsLine = n == 0 ? _s.Get("Vm_HomeAppsNone") : n == 1 ? _s.Get("Vm_HomeApps1") : _s.Format("Vm_HomeApps", n);
            if (failed || view is null)
            {
                CatalogueUnavailable = true;
                HasRecommendation = false;
                CanDownload = false;
                return;
            }
            Fill(view);
        });
    }

    private void Fill(ModelCatalogView view)
    {
        CatalogueUnavailable = !view.CatalogueAvailable;
        _rec = view.CatalogueAvailable
            ? view.Models.FirstOrDefault(m => m.Recommended && !m.Installed)
                ?? view.Models.FirstOrDefault(m => m.FitsThisPc && !m.Installed)
            : null;
        HasRecommendation = _rec is not null;
        if (_rec is { } r)
        {
            RecName = r.DisplayName;
            RecSize = Fmt.Bytes(r.SizeBytes);
            RecReason = r.Reason ?? "";
            RecAutoDownload = _s.Format("Vm_AutoDownload", r.DisplayName);
            NotEnoughSpace = r.SizeBytes > view.FreeBytes;
            SpaceLine = _s.Format("Vm_HomeNoSpace", Fmt.Bytes(r.SizeBytes), Fmt.Bytes(view.FreeBytes));
            if (r.DownloadState is { } ds && !r.Installed && r.SizeBytes > 0 && _downloadState == "none")
            {
                _downloadState = ds;
                Percent = 100.0 * r.BytesDone / r.SizeBytes;
                ProgressText = ds == "paused" ? _s.Format("Vm_Paused", (int)Percent) : "";
            }
        }
        RefreshDownload();
    }

    partial void OnErrorLineChanged(string value) => UpdateNote();

    private void UpdateNote()
    {
        NoteText = ErrorLine.Length > 0 ? ErrorLine : IsDownloading ? "" : ProgressText;
        HasNote = NoteText.Length > 0;
    }

    private void RefreshDownload()
    {
        IsDownloading = _downloadState is "downloading" or "queued" or "verifying";
        DownloadFailed = _downloadState == "failed";
        CanDownload = _rec is not null && !NotEnoughSpace && (_downloadState is "none" or "paused" or "failed");
        UpdateNote();
    }

    [RelayCommand]
    private Task StartHostAsync() => _shell.StartHostAsync();

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_rec is not { } r || !CanDownload) return;
        var resume = _downloadState is "paused" or "failed";
        try
        {
            if (resume) await _shell.Host.ResumeDownloadAsync(r.Id).ConfigureAwait(false);
            else await _shell.Host.StartDownloadAsync(r.Id).ConfigureAwait(false);
            _ui.Post(() => ErrorLine = "");
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => ErrorLine = _s.Get("Vm_NoHostTitle"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => ErrorLine = ex.Message);
        }
    }

    [RelayCommand]
    private Task TryAgainAsync()
    {
        CatalogueUnavailable = false;
        LoadingCatalogue = true;
        return LoadAsync();
    }

    private void OnProgress(DownloadProgressEvent e) => _ui.Post(() =>
    {
        if (_rec is null || e.CatalogueId != _rec.Id) return;
        _downloadState = e.State is "done" or "removed" ? "none" : e.State;
        Percent = e.BytesTotal > 0 ? 100.0 * e.BytesDone / e.BytesTotal : 0;
        ProgressText = e.State switch
        {
            "downloading" => e.EtaSeconds is { } eta
                ? _s.Format("Vm_DownloadEta", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal), Math.Max(1, (int)Math.Ceiling(eta / 60)))
                : _s.Format("Vm_DownloadProgress", Fmt.Bytes(e.BytesDone), Fmt.Bytes(e.BytesTotal)),
            "paused" => _s.Format("Vm_Paused", (int)Percent),
            "verifying" => _s.Get("Vm_Verifying"),
            "queued" => _s.Get("Vm_Queued"),
            "failed" => _s.Format("Vm_DownloadFailed", e.Message ?? ""),
            _ => "",
        };
        RefreshDownload();
    });

    /// <summary>An app was allowed or denied: read the connected apps again so the count is right straight away.</summary>
    private void OnClientsChanged() => _ = LoadAsync();

    private void OnSnapshot()
    {
        var hadModel = ShowInstalled;
        Update();
        if (_shell.Snapshot.Connected) _ = Health.RefreshAsync(); // at most every few seconds
        // A model appeared or went away: read the lists again so the card has the right apps count.
        // Also covers the page opening before the host answered: the first connected snapshot loads them.
        if (hadModel != ShowInstalled || (!_loaded && _shell.Snapshot.Connected)) _ = LoadAsync();
    }

    private void Update()
    {
        var snap = _shell.Snapshot;
        NoHost = !snap.Connected;
        SafeMode = snap.Connected && snap.SafeMode;
        StoreProblem = snap.Connected ? snap.StoreProblem ?? "" : "";
        HasStoreProblem = StoreProblem.Length > 0;
        StartMessage = _shell.StartMessage;
        CanStart = !_shell.Starting;
        if (snap.Status is not { } st)
        {
            ShowOnboarding = false;
            ShowInstalled = false;
            UpgradeAvailable = false;
            StatusTitle = _s.Get("Vm_NoHostTitle");
            StatusDetail = _s.Get("Vm_NoHostBody");
            return;
        }
        st.Models.TryGetValue(ModelKinds.Chat, out var chat);
        var hasModel = chat is not null && chat.State != ModelStates.NotInstalled && !string.IsNullOrEmpty(chat.DisplayName);
        ShowInstalled = hasModel;
        ShowOnboarding = !hasModel;
        UpgradeAvailable = hasModel && st.UpgradeAvailable;
        StatusTitle = _s.Get(st.State switch
        {
            HostStates.Ready => "Vm_StateReady",
            HostStates.Busy => "Vm_StateBusy",
            HostStates.Loading => "Vm_StateLoading",
            HostStates.Idle => "Vm_StateIdle",
            HostStates.NoModel => "Vm_StateNoModel",
            HostStates.Downloading => "Vm_StateDownloading",
            _ => "Vm_StateError",
        });
        StatusDetail = _s.Format("Vm_HostVersion", st.HostVersion);
        if (hasModel)
        {
            ModelName = chat!.DisplayName!;
            ModelLine = _s.Get(chat.State switch
            {
                ModelStates.Loaded => "Vm_HomeLoaded",
                ModelStates.Loading => "Vm_HomeLoading",
                _ => "Vm_HomeUnloaded",
            });
            IdleLine = chat.KeepLoaded
                ? _s.Get("Vm_HomeKeepLoaded")
                : chat.IdleUnloadSeconds > 0
                    ? _s.Format("Vm_HomeIdleUnload", Math.Max(1, chat.IdleUnloadSeconds / 60))
                    : "";
        }
        else
        {
            ModelName = "";
            ModelLine = "";
            IdleLine = "";
        }
        QueueLine = st.Queue.Running + st.Queue.Waiting == 0
            ? _s.Get("Vm_QueueEmpty")
            : _s.Format("Vm_QueueSome", st.Queue.Running, st.Queue.Waiting);
    }

    public void Dispose()
    {
        _shell.SnapshotChanged -= OnSnapshot;
        _shell.ClientsChanged -= OnClientsChanged;
        _shell.Host.DownloadProgress -= OnProgress;
        Live.Dispose();
    }
}
