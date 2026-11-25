using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One bar of the 7-day chart. Height is 0 to <see cref="DeviceAppRow.BarMaxHeight"/>.</summary>
public sealed record DayBar(double Height, string Automation);

/// <summary>One app of a device: today's counts, a 7-day bar, and its own pause.</summary>
public sealed partial class DeviceAppRow : ObservableObject
{
    public const double BarMaxHeight = 28;

    private readonly DeviceCardViewModel _card;

    public DeviceAppRow(DeviceCardViewModel card, IStrings s, DeviceAppSummary app, string deviceName)
    {
        _card = card;
        Name = app.Name;
        Update(s, app, deviceName);
    }

    public string Name { get; }

    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private string _todayText = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseLabel = "";
    [ObservableProperty] private string _pauseTip = "";
    [ObservableProperty] private string _pauseAutomation = "";
    [ObservableProperty] private IReadOnlyList<DayBar> _bars = [];

    public void Update(IStrings s, DeviceAppSummary app, string deviceName)
    {
        ActiveText = app.Active == 0 ? ""
            : app.Queued > 0 ? s.Format("Vm_DevRunningQueued", app.Active - app.Queued, app.Queued)
            : s.Format("Vm_DevRunning", app.Active);
        TodayText = s.Format("Vm_DevToday", app.RequestsToday, app.TokensToday.ToString("N0", CultureInfo.CurrentCulture));
        IsPaused = app.Paused;
        PauseLabel = s.Get(app.Paused ? "Vm_DevResume" : "Vm_DevPause");
        PauseTip = s.Get(app.Paused ? "Vm_DevAppResumeTip" : "Vm_DevAppPauseTip");
        PauseAutomation = s.Format(app.Paused ? "Vm_AutoDevAppResume" : "Vm_AutoDevAppPause", Name, deviceName);
        var days = app.Last7Days.Count == 7 ? app.Last7Days : [.. app.Last7Days.Concat(Enumerable.Repeat(0, 7)).Take(7)];
        var max = Math.Max(1, days.Max());
        Bars = [.. days.Select((n, i) => new DayBar(n == 0 ? 2 : Math.Max(4, BarMaxHeight * n / max), s.Format("Vm_DevBar", n, 6 - i)))];
    }

    [RelayCommand]
    private Task TogglePauseAsync() => _card.SetAppPausedAsync(Name, !IsPaused);
}

/// <summary>A model the owner can allow or not for a device.</summary>
public sealed partial class ModelChoice(string id, string name, bool allowed) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    [ObservableProperty] private bool _allowed = allowed;
}

/// <summary>One card: a paired device with its apps, usage, model, speed and controls.</summary>
public sealed partial class DeviceCardViewModel : ObservableObject
{
    private readonly DevicesViewModel _owner;
    private readonly IStrings _s;
    private bool _applying;

    public DeviceCardViewModel(DevicesViewModel owner, IStrings s, string deviceId)
    {
        _owner = owner;
        _s = s;
        DeviceId = deviceId;
    }

    public string DeviceId { get; }

    public ObservableCollection<DeviceAppRow> Apps { get; } = [];
    public ObservableCollection<ModelChoice> Models { get; } = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _routeText = "";
    [ObservableProperty] private string _lastSeenText = "";
    [ObservableProperty] private bool _hasApps;
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private string _loadedByText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _takeBackKind = "chat";
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseLabel = "";
    [ObservableProperty] private string _pauseTip = "";
    [ObservableProperty] private string _pauseAutomation = "";
    [ObservableProperty] private string _forgetAutomation = "";
    [ObservableProperty] private string _renameAutomation = "";
    [ObservableProperty] private string _takeBackAutomation = "";

    /// <summary>Priority option "Equal" (off: this PC's own apps go first).</summary>
    [ObservableProperty] private bool _priorityEqual;

    /// <summary>Daily limits; NaN means none (the number box is empty).</summary>
    [ObservableProperty] private double _requestLimit = double.NaN;
    [ObservableProperty] private double _tokenLimit = double.NaN;
    [ObservableProperty] private bool _quietEnabled;
    [ObservableProperty] private TimeSpan _quietStart = new(22, 0, 0);
    [ObservableProperty] private TimeSpan _quietEnd = new(7, 0, 0);

    /// <summary>The name being typed in the rename box.</summary>
    [ObservableProperty] private string _newName = "";

    partial void OnPriorityEqualChanged(bool value)
    {
        if (!_applying) _ = SaveSettingsAsync();
    }

    public void Update(DevicesViewModel.CardData data)
    {
        var d = data.Summary;
        _applying = true;
        try
        {
            Name = d.Name;
            NewName = d.Name;
            StatusText = _s.Get(d.Status switch { DeviceStatuses.Online => "Vm_DevOnline", DeviceStatuses.Idle => "Vm_DevIdle", _ => "Vm_DevOffline" });
            IsOnline = d.Status == DeviceStatuses.Online;
            RouteText = _s.Get(d.Route switch { "lan" => "Vm_DevRouteLan", "relay" => "Vm_DevRouteRelay", _ => "Vm_DevRouteNone" });
            LastSeenText = d.LastSeenAt is { } t ? _s.Format("Vm_RemoteLastSeen", t.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)) : _s.Get("Vm_RemoteNeverSeen");
            ModelText = d.CurrentModelName is { Length: > 0 } m ? _s.Format("Vm_DevModel", m) : _s.Get("Vm_DevModelNone");
            var loaded = data.Loaded.FirstOrDefault(l => l.Kind == ModelKinds.Chat);
            HasLoaded = loaded?.DeviceId == d.DeviceId;
            TakeBackKind = loaded?.Kind ?? "chat";
            LoadedByText = loaded is null ? ""
                : loaded.DeviceId is null ? _s.Format("Vm_DevLoadedByPc", loaded.DisplayName)
                : _s.Format("Vm_DevLoadedByDevice", loaded.DeviceName ?? d.Name, loaded.DisplayName);
            SpeedText = d.TokensPerSecond is { } tps
                ? _s.Format("Vm_DevSpeed", tps.ToString("0.#", CultureInfo.CurrentCulture), d.LatencyMs?.ToString(CultureInfo.CurrentCulture) ?? "-")
                : _s.Get("Vm_DevSpeedNone");
            IsPaused = d.Paused;
            PauseLabel = _s.Get(d.Paused ? "Vm_DevResume" : "Vm_DevPause");
            PauseTip = _s.Get(d.Paused ? "Vm_DevResumeTip" : "Vm_DevPauseTip");
            PauseAutomation = _s.Format(d.Paused ? "Vm_AutoDevResume" : "Vm_AutoDevPause", d.Name);
            ForgetAutomation = _s.Format("Vm_AutoRemoveDevice", d.Name);
            RenameAutomation = _s.Format("Vm_AutoDevRename", d.Name);
            TakeBackAutomation = _s.Format("Vm_AutoTakeBack", d.Name);
            PriorityEqual = d.PriorityEqual;
            RequestLimit = d.MaxRequestsPerDay is { } r ? r : double.NaN;
            TokenLimit = d.MaxTokensPerDay is { } k ? k : double.NaN;
            QuietEnabled = d.QuietStart is not null && d.QuietEnd is not null;
            if (TimeSpan.TryParseExact(d.QuietStart, "hh\\:mm", CultureInfo.InvariantCulture, out var qs)) QuietStart = qs;
            if (TimeSpan.TryParseExact(d.QuietEnd, "hh\\:mm", CultureInfo.InvariantCulture, out var qe)) QuietEnd = qe;

            // Rows are updated in place so nothing jumps while numbers change.
            var wanted = d.Apps.Select(a => a.Name).ToList();
            for (var i = Apps.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(Apps[i].Name)) Apps.RemoveAt(i);
            }

            foreach (var app in d.Apps)
            {
                if (Apps.FirstOrDefault(a => a.Name == app.Name) is { } row) row.Update(_s, app, d.Name);
                else Apps.Add(new DeviceAppRow(this, _s, app, d.Name));
            }

            HasApps = Apps.Count > 0;

            var allowed = d.AllowedModels;
            Models.Clear();
            foreach (var model in data.Models)
            {
                Models.Add(new ModelChoice(model.Id, model.DisplayName, allowed.Count == 0 || allowed.Contains(model.Id)));
            }
        }
        finally
        {
            _applying = false;
        }
    }

    [RelayCommand]
    private Task TogglePauseAsync() => _owner.RunAsync(() => _owner.Host.PauseDeviceAsync(DeviceId, null, !IsPaused));

    public Task SetAppPausedAsync(string app, bool paused) => _owner.RunAsync(() => _owner.Host.PauseDeviceAsync(DeviceId, app, paused));

    [RelayCommand]
    private Task RenameAsync() =>
        string.IsNullOrWhiteSpace(NewName) || NewName.Trim() == Name ? Task.CompletedTask
            : _owner.RunAsync(() => _owner.Host.RenameDeviceAsync(DeviceId, NewName));

    [RelayCommand]
    private Task ForgetAsync() => _owner.ForgetAsync(this);

    [RelayCommand]
    private Task TakeBackAsync() => _owner.RunAsync(() => _owner.Host.TakeBackAsync(TakeBackKind));

    /// <summary>Saves priority, limits, quiet hours and the allowed models together.</summary>
    [RelayCommand]
    private Task SaveSettingsAsync()
    {
        if (_applying) return Task.CompletedTask;
        // Every model ticked, or none ticked (an empty list would mean "all" anyway): all models stay allowed. Pause blocks a device.
        var every = Models.Count == 0 || Models.All(m => m.Allowed) || !Models.Any(m => m.Allowed);
        var cmd = new SetDeviceSettingsCommand
        {
            DeviceId = DeviceId,
            PriorityEqual = PriorityEqual,
            MaxRequestsPerDay = double.IsNaN(RequestLimit) || RequestLimit < 1 ? null : (int)Math.Min(RequestLimit, int.MaxValue),
            MaxTokensPerDay = double.IsNaN(TokenLimit) || TokenLimit < 1 ? null : (long)TokenLimit,
            QuietStart = QuietEnabled ? QuietStart.ToString("hh\\:mm", CultureInfo.InvariantCulture) : null,
            QuietEnd = QuietEnabled ? QuietEnd.ToString("hh\\:mm", CultureInfo.InvariantCulture) : null,
            AllowedModels = every ? [] : [.. Models.Where(m => m.Allowed).Select(m => m.Id)],
        };
        return _owner.RunAsync(() => _owner.Host.SetDeviceSettingsAsync(cmd));
    }
}

/// <summary>The Devices page: one card per paired device, and the PC-wide gaming switch.</summary>
public sealed partial class DevicesViewModel : ObservableObject, IDisposable
{
    /// <summary>Everything one card needs, read together so a card never shows half of an update.</summary>
    public sealed record CardData(PairedDeviceSummary Summary, IReadOnlyList<LoadedModelInfo> Loaded, IReadOnlyList<CatalogueModelSummary> Models);

    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly IDialogService _dialogs;
    private readonly CancellationTokenSource _cts = new();
    private int _refreshQueued;
    private bool _loading;

    public DevicesViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings, IDialogService dialogs)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        _dialogs = dialogs;
        shell.Host.RemoteChanged += OnRemoteChanged;
    }

    public IHostControl Host => _shell.Host;

    public ObservableCollection<DeviceCardViewModel> Cards { get; } = [];

    /// <summary>True until the first answer arrives (the page shows a placeholder, not an empty state).</summary>
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private bool _gamingMode;
    [ObservableProperty] private string _gamingStatusText = "";
    [ObservableProperty] private string _message = "";

    public bool HasMessage => Message.Length > 0;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>The empty state shows once the first answer says there is no device.</summary>
    public bool ShowEmpty => !IsLoading && !HasDevices;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnGamingModeChanged(bool value)
    {
        if (!_loading) _ = SaveGamingModeAsync(value);
    }

    /// <summary>Reads the state once. All host calls run off the UI thread; results are posted back.</summary>
    public async Task LoadAsync()
    {
        await RefreshAsync(_cts.Token).ConfigureAwait(false);
    }

    private void OnRemoteChanged()
    {
        // Bursts of pushes (a call starting, finishing) become one refresh.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Coalesce, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _refreshQueued, 0);
                await RefreshAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var status = await Host.GetRemoteAsync(ct).ConfigureAwait(false);
            var settings = await Host.GetSettingsAsync(ct).ConfigureAwait(false);
            var models = (await Host.ListModelsAsync(ct).ConfigureAwait(false)).Models.Where(m => m.Installed && m.Kind == ModelKinds.Chat).ToList();
            ct.ThrowIfCancellationRequested();
            _ui.Post(() => Show(status, settings, models));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => IsLoading = false);
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => { Message = ex.Message; IsLoading = false; });
        }
    }

    private void Show(RemoteStatus status, HostSettings settings, IReadOnlyList<CatalogueModelSummary> models)
    {
        _loading = true;
        try
        {
            GamingMode = settings.Remote.GamingMode;
            GamingStatusText = status.GamingBusy ? _s.Get("Vm_DevGamingBusy") : "";
        }
        finally
        {
            _loading = false;
        }

        var ids = status.Devices.Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Cards[i].DeviceId)) Cards.RemoveAt(i);
        }

        foreach (var d in status.Devices)
        {
            var card = Cards.FirstOrDefault(c => c.DeviceId == d.DeviceId);
            if (card is null) Cards.Add(card = new DeviceCardViewModel(this, _s, d.DeviceId));
            card.Update(new CardData(d, status.LoadedModels, models));
        }

        HasDevices = Cards.Count > 0;
        Message = "";
        IsLoading = false;
    }

    private async Task SaveGamingModeAsync(bool on)
    {
        try
        {
            var settings = await Host.GetSettingsAsync(_cts.Token).ConfigureAwait(false);
            settings.Remote.GamingMode = on;
            await Host.SetSettingsAsync(settings, _cts.Token).ConfigureAwait(false);
            await RefreshAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Message = _s.Get("Vm_NoHostTitle"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
    }

    /// <summary>Runs one host action off the UI thread, then refreshes. Errors become a short message.</summary>
    public async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            await RefreshAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Message = _s.Get("Vm_NoHostTitle"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
    }

    /// <summary>Forget: asks first, then removes the device (the host deletes that device's stats file only).</summary>
    public async Task ForgetAsync(DeviceCardViewModel card)
    {
        var sure = await _dialogs.ConfirmAsync(_s.Format("Vm_RemoveDeviceTitle", card.Name), _s.Format("Vm_RemoveDeviceBody", card.Name),
            _s.Get("Vm_RemoveDeviceConfirm"), _s.Get("Vm_Cancel")).ConfigureAwait(false);
        if (!sure) return;
        await RunAsync(() => Host.RemoveDeviceAsync(card.DeviceId)).ConfigureAwait(false);
    }

    /// <summary>Leaving the page cancels what is still being read.</summary>
    public void Dispose()
    {
        _shell.Host.RemoteChanged -= OnRemoteChanged;
        _cts.Cancel();
    }
}
