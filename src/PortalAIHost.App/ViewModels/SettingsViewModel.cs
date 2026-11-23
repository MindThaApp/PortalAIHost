using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Settings page. Every change applies straight away: it is checked, then saved. There is no Apply button.</summary>
public sealed partial class SettingsViewModel : ObservableObject
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
        _ = dialogs;
        IdleChoices = new[] { _s.Get("Vm_Idle_Keep"), _s.Get("Vm_Idle_5"), _s.Get("Vm_Idle_10") };
        ThemeChoices = Names("Vm_Theme_", "System", "Light", "Dark");
        MaterialChoices = Names("Vm_Material_", "Mica", "Acrylic", "Solid");
        CornerChoices = Names("Vm_Corners_", "Rounded", "Square", "ExtraRound");
        DensityChoices = Names("Vm_Density_", "Compact", "Standard", "Comfortable");
        TextChoices = Names("Vm_Text_", "Normal", "Larger", "Largest");
        MotionChoices = Names("Vm_Motion_", "Follow", "Reduced", "Off");
        ShowAppearance(shell.State.Appearance);
    }

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
                ModelsFolder = s.ModelsFolder ?? "";
                FolderPlaceholder = Fmt.PrivatePath(shownFolder);
                Available = true;
                Message = "";
                _loading = false;
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Available = false; Say(_s.Get("Vm_NoHostTitle"), MessageError); });
        }
    }

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
        return new HostSettings
        {
            StartWithWindows = StartWithWindows,
            Backend = _current.Backend,
            ModelsFolder = folder ?? _current.ModelsFolder,
            MaxMemoryPercent = _current.MaxMemoryPercent,
            PreventSleepWhileDownloading = _current.PreventSleepWhileDownloading,
            SendCrashReports = _shell.State.SendCrashReports,
            BandwidthCapBytesPerSecond = _current.BandwidthCapBytesPerSecond,
            Kinds = kinds,
        };
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
