using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One of the PC's installed models on the laptop's Models page (client mode with Model control).</summary>
public sealed partial class RemoteModelRowViewModel : ObservableObject
{
    private static readonly string[] Placements = [DevicePlacements.Auto, DevicePlacements.Gpu, DevicePlacements.Cpu, DevicePlacements.CpuOnly];

    private readonly RemoteModelsViewModel _owner;
    private readonly IStrings _s;

    public RemoteModelRowViewModel(RemoteModelsViewModel owner, IStrings strings, string id)
    {
        _owner = owner;
        _s = strings;
        Id = id;
        PlacementChoices = [_s.Get("Vm_RemPlacementAuto"), _s.Get("Vm_RemPlacementGpu"), _s.Get("Vm_RemPlacementCpu"), _s.Get("Vm_RemPlacementCpuOnly")];
    }

    public string Id { get; }

    public IReadOnlyList<string> PlacementChoices { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _loadedByText = "";
    [ObservableProperty] private bool _canLoad;
    [ObservableProperty] private bool _canUnload;
    [ObservableProperty] private bool _tooBig;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private int _placementIndex;

    public bool Idle => !Busy;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(Idle));

    /// <summary>The placement word sent with a load.</summary>
    public string Placement => Placements[Math.Clamp(PlacementIndex, 0, Placements.Length - 1)];

    public string AutoLoad => _s.Format("Vm_RemAutoLoad", Name);

    public string AutoUnload => _s.Format("Vm_RemAutoUnload", Name);

    public void Update(DeviceModelState m, string? myDeviceId)
    {
        Name = m.DisplayName.Length > 0 ? m.DisplayName : m.Id;
        SizeText = Fmt.Bytes(m.SizeBytes);
        TooBig = !m.FitsThisPc;
        var loaded = m.State != "unloaded";
        var mine = loaded && myDeviceId is not null && m.LoadedBy == myDeviceId;
        StateText = m.State switch
        {
            "loaded" => _s.Get("Vm_RemStateLoaded"),
            "loading" => _s.Get("Vm_RemStateLoading"),
            _ => "",
        };
        LoadedByText = !loaded ? "" : mine ? _s.Get("Vm_RemByLaptop") : m.LoadedBy is null or "pc" ? _s.Get("Vm_RemByPc") : _s.Get("Vm_RemByOther");
        if (mine && m.Placement is { } placement)
        {
            var at = Array.IndexOf(Placements, placement);
            if (at >= 0) PlacementIndex = at;
        }

        CanLoad = !loaded && m.FitsThisPc;
        CanUnload = mine;
        OnPropertyChanged(nameof(AutoLoad));
        OnPropertyChanged(nameof(AutoUnload));
    }

    [RelayCommand]
    private Task LoadAsync() => _owner.LoadAsync(this);

    [RelayCommand]
    private Task UnloadAsync() => _owner.UnloadAsync(this);
}

/// <summary>
/// "Models on &lt;PC name&gt;" on the laptop's Models page (docs/client-mode-v1.md 4.7). Without Model control it only
/// says whom to ask. Every host call runs off the UI thread and is cancelled when the page is left; bursts of change
/// pushes become one refresh; a row that is waiting for the PC shows busy and the list keeps its place.
/// </summary>
public sealed partial class RemoteModelsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly CancellationTokenSource _cts = new();
    private int _refreshQueued;
    private string _pcName = "";

    public RemoteModelsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        shell.Host.RemoteChanged += OnChanged;
    }

    public IHostControl Host => _shell.Host;

    public ObservableCollection<RemoteModelRowViewModel> Rows { get; } = [];

    /// <summary>True until the first answer arrives (a placeholder shows instead of the empty state).</summary>
    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private bool _isClientMode;
    [ObservableProperty] private bool _hasControl;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _message = "";

    public bool HasMessage => Message.Length > 0;

    /// <summary>Client mode, answered, and no Model control: the empty state tells whom to ask.</summary>
    public bool ShowEmpty => IsClientMode && !IsLoading && !HasControl;

    /// <summary>The list fades in once the PC has answered; its place is kept meanwhile.</summary>
    public double ListOpacity => IsLoading ? 0 : 1;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ListOpacity));
    }

    partial void OnIsClientModeChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnHasControlChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    public Task LoadAsync() => RefreshAsync(_cts.Token);

    private void OnChanged()
    {
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
            var status = await Host.GetClientStatusAsync(ct).ConfigureAwait(false);
            if (status.Mode != HostModes.Client)
            {
                _ui.Post(() => ShowNotClient());
                return;
            }

            _pcName = status.PcName ?? "";
            if (status.ModelControl == false)
            {
                _ui.Post(() => ShowNoControl());
                return;
            }

            var answer = await Host.ListRemoteModelsAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ui.Post(() => ShowAnswer(answer, keepMessage: false));
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

    private void ShowNotClient()
    {
        IsClientMode = false;
        HasControl = false;
        Rows.Clear();
        IsLoading = false;
    }

    private void ShowNoControl()
    {
        IsClientMode = true;
        HasControl = false;
        Title = _s.Format("Vm_RemTitle", _pcName);
        EmptyText = _s.Format("Vm_RemAskOwner", _pcName);
        Rows.Clear();
        Message = "";
        IsLoading = false;
    }

    private void ShowAnswer(RemoteModelsAnswer answer, bool keepMessage)
    {
        IsClientMode = true;
        if (answer.PcName is { Length: > 0 } pc) _pcName = pc;
        Title = _s.Format("Vm_RemTitle", _pcName);
        EmptyText = _s.Format("Vm_RemAskOwner", _pcName);
        if (!answer.Ok && answer.ErrorCode == ErrorCodes.ModelControlRequired)
        {
            ShowNoControl();
            return;
        }

        HasControl = answer.ModelControl || answer.Ok;
        if (answer.Ok || answer.Models.Count > 0)
        {
            Apply(answer.Models, answer.DeviceId);
        }

        if (!answer.Ok) Message = answer.Message ?? "";
        else if (!keepMessage) Message = "";
        IsLoading = false;
    }

    private void Apply(IReadOnlyList<DeviceModelState> models, string? deviceId)
    {
        var ids = models.Where(m => m.Kind == ModelKinds.Chat || m.Kind == ModelKinds.Embedding)
            .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Rows[i].Id)) Rows.RemoveAt(i);
        }

        foreach (var m in models.Where(m => ids.Contains(m.Id)))
        {
            var row = Rows.FirstOrDefault(r => r.Id == m.Id);
            if (row is null) Rows.Add(row = new RemoteModelRowViewModel(this, _s, m.Id));
            row.Update(m, deviceId);
        }
    }

    internal Task LoadAsync(RemoteModelRowViewModel row) =>
        ChangeAsync(row, ct => Host.LoadRemoteModelAsync(row.Id, row.Placement, ct));

    internal Task UnloadAsync(RemoteModelRowViewModel row) =>
        ChangeAsync(row, ct => Host.UnloadRemoteModelAsync(row.Id, ct));

    private async Task ChangeAsync(RemoteModelRowViewModel row, Func<CancellationToken, Task<RemoteModelsAnswer>> call)
    {
        if (row.Busy) return;
        row.Busy = true;
        try
        {
            var ct = _cts.Token;
            var answer = await Task.Run(() => call(ct), ct).ConfigureAwait(false);
            _ui.Post(() => ShowAnswer(answer, keepMessage: false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Message = _s.Get("Vm_RemHostGone"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
        finally
        {
            _ui.Post(() => row.Busy = false);
        }
    }

    public void Dispose()
    {
        _shell.Host.RemoteChanged -= OnChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>One local app and the PC model it asks for (Settings).</summary>
public sealed partial class AppModelRowViewModel : ObservableObject
{
    private readonly AppModelsViewModel _owner;
    private bool _loading;

    public AppModelRowViewModel(AppModelsViewModel owner, string appId, string name)
    {
        _owner = owner;
        AppId = appId;
        Name = name;
    }

    public string AppId { get; }

    public string Name { get; }

    public string Automation => _owner.AutomationFor(Name);

    public ObservableCollection<string> Choices => _owner.Choices;

    /// <summary>Index 0 is "PC's choice"; the others follow <see cref="AppModelsViewModel.Choices"/>.</summary>
    [ObservableProperty] private int _selectedIndex;

    public void Set(int index)
    {
        _loading = true;
        try
        {
            SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_loading && value >= 0) _ = _owner.ChooseAsync(this, value);
    }
}

/// <summary>
/// "Which PC model an app uses" (docs/client-mode-v1.md 4.7). Shown only in client mode with Model control: without the
/// grant the choice is hidden and apps get the PC's choice. The choice is saved on this laptop and applied when
/// requests are forwarded.
/// </summary>
public sealed partial class AppModelsViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly CancellationTokenSource _cts = new();
    private List<string> _modelIds = [];

    public AppModelsViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
    }

    public IHostControl Host => _shell.Host;

    public ObservableCollection<AppModelRowViewModel> Rows { get; } = [];

    /// <summary>"PC's choice" followed by the PC's installed chat models.</summary>
    public ObservableCollection<string> Choices { get; } = [];

    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _message = "";

    public bool HasMessage => Message.Length > 0;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    public string AutomationFor(string app) => _s.Format("Vm_AppModelAutomation", app);

    public Task LoadAsync() => RefreshAsync(_cts.Token);

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var status = await Host.GetClientStatusAsync(ct).ConfigureAwait(false);
            if (status.Mode != HostModes.Client || status.ModelControl != true)
            {
                _ui.Post(() => { Available = false; Rows.Clear(); });
                return;
            }

            var clients = await Host.ListClientsAsync(ct).ConfigureAwait(false);
            var maps = await Host.GetAppModelsAsync(ct).ConfigureAwait(false);
            var models = await Host.ListRemoteModelsAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _ui.Post(() => Show(clients, maps, models));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
    }

    private void Show(IReadOnlyList<ClientSummary> clients, AppModelList maps, RemoteModelsAnswer models)
    {
        if (!models.Ok)
        {
            Available = false;
            Rows.Clear();
            return;
        }

        var chat = models.Models.Where(m => m.Kind == ModelKinds.Chat || m.Kind == ModelKinds.Embedding).ToList();
        _modelIds = chat.Select(m => m.Id).ToList();
        Rows.Clear(); // Before the choices change, so no box reports a cleared selection.
        Choices.Clear();
        Choices.Add(_s.Get("Vm_AppModelPcChoice"));
        foreach (var m in chat) Choices.Add(m.DisplayName.Length > 0 ? m.DisplayName : m.Id);

        var chosen = maps.Apps.ToDictionary(a => a.AppId, a => a.ModelId, StringComparer.Ordinal);
        var apps = clients.Where(c => !c.Revoked && c.DeviceId == "local")
            .GroupBy(c => c.AppId, StringComparer.Ordinal).Select(g => g.First()).OrderBy(c => c.AppName, StringComparer.CurrentCultureIgnoreCase);
        Rows.Clear();
        foreach (var app in apps)
        {
            var row = new AppModelRowViewModel(this, app.AppId, app.AppName.Length > 0 ? app.AppName : app.AppId);
            var at = chosen.TryGetValue(app.AppId, out var id) ? _modelIds.IndexOf(id) : -1;
            row.Set(at >= 0 ? at + 1 : 0);
            Rows.Add(row);
        }

        Available = true;
        Message = "";
    }

    internal async Task ChooseAsync(AppModelRowViewModel row, int index)
    {
        var modelId = index > 0 && index <= _modelIds.Count ? _modelIds[index - 1] : null;
        try
        {
            var ct = _cts.Token;
            await Task.Run(() => Host.SetAppModelAsync(row.AppId, modelId, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Message = _s.Get("Vm_RemHostGone"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Message = ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
