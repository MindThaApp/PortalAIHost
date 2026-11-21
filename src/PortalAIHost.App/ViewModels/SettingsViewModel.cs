using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Settings page. Every change is sent to the host straight away.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const int KeepLoadedIndex = 0;
    public const int FiveMinutesIndex = 1;
    public const int TenMinutesIndex = 2;

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IDialogService _dialogs;
    private readonly IStartupControl _startup;
    private HostSettings _current = new();
    private bool _loading;

    public SettingsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs, IStartupControl? startup = null)
    {
        _startup = startup ?? new UnavailableStartupControl();
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
        IdleChoices = new[] { _s.Get("Vm_Idle_Keep"), _s.Get("Vm_Idle_5"), _s.Get("Vm_Idle_10") };
    }

    public IReadOnlyList<string> IdleChoices { get; }

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private int _idleIndex = TenMinutesIndex;
    [ObservableProperty] private string _modelsFolder = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _startupState = StartupStates.Unavailable;
    [ObservableProperty] private bool _startupChangeable;
    [ObservableProperty] private string _startupNote = "";

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
            _ui.Post(() =>
            {
                _loading = true;
                _current = s;
                ApplyStartup(startup);
                // Windows owns the real state (Task Manager can turn it off); the saved value only mirrors it.
                StartWithWindows = startup is StartupStates.Unavailable ? s.StartWithWindows
                    : startup is StartupStates.Enabled or StartupStates.EnabledByPolicy;
                IdleIndex = IndexFor(s);
                ModelsFolder = s.ModelsFolder ?? "";
                Available = true;
                Message = "";
                _loading = false;
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Available = false; Message = _s.Get("Vm_NoHostTitle"); });
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
                    Message = _s.Format("Vm_FolderRefused", refused);
                    return;
                }

                var target = string.IsNullOrEmpty(next.ModelsFolder)
                    ? _s.Get("Vm_DefaultFolder")
                    : Path.Combine(next.ModelsFolder, HostFolders.ModelsSubfolder);
                Message = saved.ModelsFolderChanged
                    ? _s.Format("Vm_FolderSavedNote", string.IsNullOrEmpty(old) ? _s.Get("Vm_DefaultFolder") : Path.Combine(old, HostFolders.ModelsSubfolder), target)
                    : _s.Get("Vm_Saved");
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Message = _s.Get("Vm_SaveFailed"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
    }

    /// <summary>Asks the host to use another models folder. Nothing is moved or deleted by the window.</summary>
    [RelayCommand]
    private async Task ApplyFolderAsync()
    {
        var folder = ModelsFolder.Trim();
        if (folder.Length == 0 || folder == _current.ModelsFolder) return;
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_FolderTitle"), _s.Format("Vm_FolderBody", folder), _s.Get("Vm_FolderYes"), _s.Get("Vm_Cancel"));
        if (!ok)
        {
            ModelsFolder = _current.ModelsFolder ?? "";
            return;
        }
        await SaveAsync(folder);
    }
}
