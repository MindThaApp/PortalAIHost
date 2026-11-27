using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Settings page. Every change applies straight away: it is checked, then saved. There is no Apply button.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const int KeepLoadedIndex = 0;
    public const int FiveMinutesIndex = 1;
    public const int TenMinutesIndex = 2;

    public const int MessageInfo = 0;
    public const int MessageSuccess = 1;
    public const int MessageWarning = 2;
    public const int MessageError = 3;

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IStartupControl _startup;
    private readonly IDialogService _dialogs;
    private string _moveTarget = "";
    private readonly IAppearanceApplier? _applier;
    private HostSettings _current = new();
    private bool _loading;
    private bool _appearanceLoading;

    public SettingsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs, IStartupControl? startup = null, IAppearanceApplier? applier = null)
    {
        _startup = startup ?? new UnavailableStartupControl();
        _applier = applier;
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
        shell.Host.StoreProgress += OnStoreProgress;
        shell.Host.RemoteChanged += OnRemoteChanged;
        IdleChoices = new[] { _s.Get("Vm_Idle_Keep"), _s.Get("Vm_Idle_5"), _s.Get("Vm_Idle_10") };
        ThemeChoices = Names("Vm_Theme_", "System", "Light", "Dark");
        MaterialChoices = Names("Vm_Material_", "Mica", "Acrylic", "Solid");
        CornerChoices = Names("Vm_Corners_", "Rounded", "Square", "ExtraRound");
        DensityChoices = Names("Vm_Density_", "Compact", "Standard", "Comfortable");
        TextChoices = Names("Vm_Text_", "Normal", "Larger", "Largest");
        MotionChoices = Names("Vm_Motion_", "Follow", "Reduced", "Off");
        ShowAppearance(shell.State.Appearance);
        InitPerformance();
        InitHomePc();
    }

    public void Dispose()
    {
        _shell.Host.StoreProgress -= OnStoreProgress;
        _shell.Host.RemoteChanged -= OnRemoteChanged;
        DisposeHomePc();
    }

    /// <summary>After a new models folder is saved: offers to move the models already downloaded into it.</summary>
    [ObservableProperty] private bool _moveOfferVisible;
    [ObservableProperty] private string _moveOfferText = "";
    [ObservableProperty] private bool _moving;
    [ObservableProperty] private double _movePercent;

    private async Task OfferMoveAsync(string folder)
    {
        long bytes;
        try
        {
            bytes = (await _shell.Host.ListModelsAsync().ConfigureAwait(false)).ModelsBytes;
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
            return;
        }

        _ui.Post(() =>
        {
            _moveTarget = folder;
            MoveOfferText = _s.Format("Vm_MoveOffer", Fmt.Bytes(bytes));
            MoveOfferVisible = bytes > 0;
        });
    }

    /// <summary>
    /// Moves the models into the chosen folder after a confirmation. The host copies, checks the copies, switches,
    /// and only then removes the old copies; it refuses when the drive has too little space and says how much is needed.
    /// </summary>
    [RelayCommand]
    private async Task MoveAsync()
    {
        if (_moveTarget.Length == 0) return;
        var target = Fmt.PrivatePath(Path.Combine(_moveTarget, HostFolders.ModelsSubfolder));
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_MoveTitle"), _s.Format("Vm_MoveBody", target), _s.Get("Vm_MoveYes"), _s.Get("Vm_Cancel"));
        if (!ok) return;
        _ui.Post(() => { Moving = true; MovePercent = 0; });
        try
        {
            await _shell.Host.MoveStoreAsync(_moveTarget).ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Moving = false; Say(_s.Get("Vm_SaveFailed"), MessageError); });
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => { Moving = false; Say(ex.Message, MessageError); });
        }
    }

    [RelayCommand]
    private async Task CancelMoveAsync()
    {
        try
        {
            await _shell.Host.CancelStoreOperationAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
        }
    }

    private void OnStoreProgress(StoreProgressEvent e) => _ui.Post(() =>
    {
        if (e.Operation != StoreOperations.Move) return;
        switch (e.State)
        {
            case StoreStates.Running:
                Moving = true;
                MovePercent = 100 * e.Fraction;
                Say(_s.Format("Vm_MoveSettingsRunning", (int)MovePercent), MessageInfo);
                break;
            case StoreStates.RemovingOld:
                Say(_s.Get("Vm_MoveRemovingOld"), MessageInfo);
                break;
            case StoreStates.Done:
                Moving = false;
                MoveOfferVisible = false;
                Say(e.Message ?? _s.Get("Vm_MoveDone"), MessageSuccess);
                break;
            case StoreStates.Cancelled:
                Moving = false;
                Say(_s.Get("Vm_MoveCancelled"), MessageInfo);
                break;
            default:
                Moving = false;
                Say(_s.Format("Vm_MoveFailed", e.Message ?? ""), MessageError);
                break;
        }
    });

    private string[] Names(string prefix, params string[] keys) => keys.Select(k => _s.Get(prefix + k)).ToArray();

    public IReadOnlyList<string> IdleChoices { get; }
    public IReadOnlyList<string> ThemeChoices { get; }
    public IReadOnlyList<string> MaterialChoices { get; }
    public IReadOnlyList<string> CornerChoices { get; }
    public IReadOnlyList<string> DensityChoices { get; }
    public IReadOnlyList<string> TextChoices { get; }
    public IReadOnlyList<string> MotionChoices { get; }

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _sendCrashReports = true;
    [ObservableProperty] private bool _notifyDownloads = true;
    [ObservableProperty] private bool _notifyApprovals = true;
    [ObservableProperty] private bool _notifyProblems = true;
    [ObservableProperty] private bool _notifyDevices = true;
    [ObservableProperty] private int _idleIndex = TenMinutesIndex;
    [ObservableProperty] private string _modelsFolder = "";
    [ObservableProperty] private string _folderPlaceholder = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private int _messageKind;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _startupState = StartupStates.Unavailable;
    [ObservableProperty] private bool _startupChangeable;
    [ObservableProperty] private string _startupNote = "";
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _materialIndex;
    [ObservableProperty] private int _cornersIndex;
    [ObservableProperty] private int _densityIndex;
    [ObservableProperty] private int _textIndex;
    [ObservableProperty] private int _motionIndex;

    partial void OnMessageChanged(string value) => HasMessage = value.Length > 0;

    private void Say(string text, int kind)
    {
        MessageKind = kind;
        Message = text;
    }

    private void ShowAppearance(AppearancePrefs p)
    {
        _appearanceLoading = true;
        ThemeIndex = (int)p.Theme;
        MaterialIndex = (int)p.Material;
        CornersIndex = (int)p.Corners;
        DensityIndex = (int)p.Density;
        TextIndex = Math.Clamp(p.TextStep, 0, 2);
        MotionIndex = (int)p.Motion;
        _appearanceLoading = false;
    }

    private AppearancePrefs ReadAppearance() => new()
    {
        Theme = (ThemeChoice)Math.Clamp(ThemeIndex, 0, 2),
        Material = (MaterialChoice)Math.Clamp(MaterialIndex, 0, 2),
        Corners = (CornerChoice)Math.Clamp(CornersIndex, 0, 2),
        Density = (DensityChoice)Math.Clamp(DensityIndex, 0, 2),
        TextStep = Math.Clamp(TextIndex, 0, 2),
        Motion = (MotionChoice)Math.Clamp(MotionIndex, 0, 2),
    };

    private void AppearanceEdited()
    {
        if (_appearanceLoading) return;
        var prefs = ReadAppearance();
        _shell.State.SaveAppearance(prefs);
        _applier?.Apply(prefs);
    }

    partial void OnThemeIndexChanged(int value) => AppearanceEdited();
    partial void OnMaterialIndexChanged(int value) => AppearanceEdited();
    partial void OnCornersIndexChanged(int value) => AppearanceEdited();
    partial void OnDensityIndexChanged(int value) => AppearanceEdited();
    partial void OnTextIndexChanged(int value) => AppearanceEdited();
    partial void OnMotionIndexChanged(int value) => AppearanceEdited();

    /// <summary>Puts every appearance choice back to the design defaults.</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        ShowAppearance(AppearancePrefs.Default);
        _shell.State.SaveAppearance(AppearancePrefs.Default);
        _applier?.Apply(AppearancePrefs.Default);
    }

    /// <summary>Puts start with Windows and the idle time back to their defaults.</summary>
    [RelayCommand]
    private void ResetGeneral()
    {
        IdleIndex = TenMinutesIndex;
        StartWithWindows = false;
    }

    /// <summary>Goes back to the default models folder. Models already downloaded are not moved or deleted.</summary>
    [RelayCommand]
    private async Task ResetFolderAsync()
    {
        if (string.IsNullOrEmpty(_current.ModelsFolder) && ModelsFolder.Length == 0) return;
        ModelsFolder = "";
        await SaveAsync("");
    }

    private void ApplyStartup(string state)
    {
        StartupState = state;
        StartupChangeable = state is StartupStates.Enabled or StartupStates.Disabled;
        StartupNote = state switch
        {
            StartupStates.Unavailable => _s.Get("Vm_Startup_Unavailable"),
            StartupStates.DisabledByUser => _s.Get("Vm_Startup_DisabledByUser"),
            StartupStates.DisabledByPolicy or StartupStates.EnabledByPolicy => _s.Get("Vm_Startup_Policy"),
            _ => "",
        };
    }

    public async Task LoadAsync()
    {
        try
        {
            var s = await _shell.Host.GetSettingsAsync().ConfigureAwait(false);
            var startup = await _startup.GetStateAsync().ConfigureAwait(false);
            var shownFolder = "";
            try { shownFolder = (await _shell.Host.ListModelsAsync().ConfigureAwait(false)).StorageFolder; }
            catch (Exception ex) when (ex is HostUnavailableException or HostRequestException) { }
            await LoadPerformanceAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                _loading = true;
                _current = s;
                ApplyStartup(startup);
                // Windows owns the real state (Task Manager can turn it off); the saved value only mirrors it.
                StartWithWindows = startup is StartupStates.Unavailable ? s.StartWithWindows
                    : startup is StartupStates.Enabled or StartupStates.EnabledByPolicy;
                IdleIndex = IndexFor(s);
                SendCrashReports = _shell.State.SendCrashReports;
                NotifyDownloads = IsOn(s, NotificationKinds.Downloads);
                NotifyApprovals = IsOn(s, NotificationKinds.Approvals);
                NotifyProblems = IsOn(s, NotificationKinds.Problems);
                NotifyDevices = IsOn(s, NotificationKinds.Devices);
                ModelsFolder = s.ModelsFolder ?? "";
                FolderPlaceholder = Fmt.PrivatePath(shownFolder);
                RemoteEnabled = s.Remote.Enabled;
                RemoteRelay = s.Remote.Relay;
                AllowReplaceModels = s.Remote.AllowReplaceModels;
                RelayAdmission = s.Remote.RelayAdmission;
                Available = true;
                Message = "";
                _loading = false;
            });
            await RefreshRemoteAsync().ConfigureAwait(false);
            await LoadHomePcAsync().ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Available = false; Say(_s.Get("Vm_NoHostTitle"), MessageError); });
        }
    }

    private static bool IsOn(HostSettings s, string kind) => s.MutedNotifications?.Contains(kind) != true;

    /// <summary>The notification kinds switched off, or null when all are on (the saved form).</summary>
    private List<string>? MutedKinds()
    {
        List<string> muted = [];
        if (!NotifyDownloads) muted.Add(NotificationKinds.Downloads);
        if (!NotifyApprovals) muted.Add(NotificationKinds.Approvals);
        if (!NotifyProblems) muted.Add(NotificationKinds.Problems);
        if (!NotifyDevices) muted.Add(NotificationKinds.Devices);
        // Names this version does not know stay as they were.
        muted.AddRange((_current.MutedNotifications ?? []).Where(k => !NotificationKinds.All.Contains(k)));
        return muted.Count == 0 ? null : muted;
    }

    partial void OnNotifyDownloadsChanged(bool value) { if (!_loading) _ = SaveAsync(); }
    partial void OnNotifyApprovalsChanged(bool value) { if (!_loading) _ = SaveAsync(); }
    partial void OnNotifyProblemsChanged(bool value) { if (!_loading) _ = SaveAsync(); }
    partial void OnNotifyDevicesChanged(bool value) { if (!_loading) _ = SaveAsync(); }

    /// <summary>Puts the notification choices back to their defaults (all on).</summary>
    [RelayCommand]
    private void ResetNotifications() => (NotifyDownloads, NotifyApprovals, NotifyProblems, NotifyDevices) = (true, true, true, true);

    private static int IndexFor(HostSettings s)
    {
        if (!s.Kinds.TryGetValue(ModelKinds.Chat, out var k)) return TenMinutesIndex;
        if (k.KeepLoaded) return KeepLoadedIndex;
        return k.IdleUnloadSeconds <= 300 ? FiveMinutesIndex : TenMinutesIndex;
    }

    partial void OnStartWithWindowsChanged(bool value) { if (!_loading) _ = ChangeStartupAsync(value); }

    private async Task ChangeStartupAsync(bool value)
    {
        var state = await _startup.SetEnabledAsync(value).ConfigureAwait(false);
        _ui.Post(() =>
        {
            ApplyStartup(state);
            if (state is not StartupStates.Unavailable && (state == StartupStates.Enabled) != value)
            {
                _loading = true;
                StartWithWindows = state == StartupStates.Enabled;
                _loading = false;
            }
        });
        await SaveAsync().ConfigureAwait(false);
    }

    partial void OnSendCrashReportsChanged(bool value) { if (!_loading) _ = _shell.SetSendCrashReportsAsync(value); }

    /// <summary>Puts the privacy choices back to their defaults (crash reports on).</summary>
    [RelayCommand]
    private void ResetPrivacy() => SendCrashReports = true;

    partial void OnIdleIndexChanged(int value) { if (!_loading && value >= 0) _ = SaveAsync(); }

    /// <summary>Builds the settings to send: the current ones with only the changed fields replaced.</summary>
    public HostSettings Build(string? folder = null)
    {
        var kinds = new Dictionary<string, KindSettings>(_current.Kinds);
        kinds[ModelKinds.Chat] = new KindSettings
        {
            KeepLoaded = IdleIndex == KeepLoadedIndex,
            IdleUnloadSeconds = IdleIndex == FiveMinutesIndex ? 300 : 600,
        };
        var next = new HostSettings
        {
            StartWithWindows = StartWithWindows,
            Backend = _current.Backend,
            ModelsFolder = folder ?? _current.ModelsFolder,
            MaxMemoryPercent = _current.MaxMemoryPercent,
            Preset = _current.Preset,
            MemoryLimitBytes = _current.MemoryLimitBytes,
            KeepFreeBytes = _current.KeepFreeBytes,
            Compute = _current.Compute,
            GpuMemoryLimitBytes = _current.GpuMemoryLimitBytes,
            CpuThreads = _current.CpuThreads,
            Priority = _current.Priority,
            PreventSleepWhileDownloading = _current.PreventSleepWhileDownloading,
            SendCrashReports = _shell.State.SendCrashReports,
            BandwidthCapBytesPerSecond = _current.BandwidthCapBytesPerSecond,
            MutedNotifications = MutedKinds(),
            Remote = new RemoteSettings
            {
                Enabled = RemoteEnabled, Port = _current.Remote.Port, Relay = RemoteRelay, RelayUrl = _current.Remote.RelayUrl,
                AllowReplaceModels = AllowReplaceModels, RelayAdmission = RelayAdmission,
            },
            Kinds = kinds,
        };
        WritePerformance(next);
        return next;
    }

    private async Task SaveAsync(string? folder = null)
    {
        var next = Build(folder);
        try
        {
            var old = _current.ModelsFolder;
            var saved = await _shell.Host.SetSettingsAsync(next).ConfigureAwait(false);
            _ui.Post(() =>
            {
                _current = next;
                if (saved.FolderRefused is { } refused)
                {
                    _current.ModelsFolder = old; // the host kept the old folder
                    ModelsFolder = old ?? "";
                    Say(_s.Format("Vm_FolderRefused", refused), MessageError);
                    return;
                }

                var target = string.IsNullOrEmpty(next.ModelsFolder)
                    ? _s.Get("Vm_DefaultFolder")
                    : Fmt.PrivatePath(Path.Combine(next.ModelsFolder, HostFolders.ModelsSubfolder));
                if (saved.ModelsFolderChanged)
                    Say(_s.Format("Vm_FolderSavedNote", string.IsNullOrEmpty(old) ? _s.Get("Vm_DefaultFolder") : Fmt.PrivatePath(Path.Combine(old, HostFolders.ModelsSubfolder)), target), MessageSuccess);
                else
                    Message = "";
            });
            if (saved.FolderRefused is null && saved.ModelsFolderChanged && !string.IsNullOrEmpty(next.ModelsFolder))
                await OfferMoveAsync(next.ModelsFolder);
            else
                _ui.Post(() => MoveOfferVisible = false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Say(_s.Get("Vm_SaveFailed"), MessageError));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Say(ex.Message, MessageError));
        }
    }

    /// <summary>
    /// Applies the typed models folder straight away: checks that it is a full path, then asks the host, which checks
    /// it again and answers. The window never moves or deletes anything.
    /// </summary>
    [RelayCommand]
    private async Task CommitFolderAsync()
    {
        var folder = ModelsFolder.Trim();
        if (folder == (_current.ModelsFolder ?? "")) return;
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder))
        {
            Say(_s.Get("Vm_FolderNotFull"), MessageError);
            return;
        }
        await SaveAsync(folder);
    }
}
