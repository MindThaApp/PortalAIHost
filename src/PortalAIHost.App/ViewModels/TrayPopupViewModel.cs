using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// The tray pop-up: a state-first header, tiles (model, memory, last request, queue) and text buttons.
/// All numbers come from <see cref="LiveViewModel"/>, which reads the host only while the pop-up is shown.
/// </summary>
public sealed partial class TrayPopupViewModel : ObservableObject, IDisposable
{
    private readonly IStrings _s;

    public TrayPopupViewModel(IHostControl host, IUiDispatcher ui, IStrings strings, TimeProvider? time = null, TimeSpan? interval = null)
    {
        _s = strings;
        Live = new LiveViewModel(host, ui, strings, time, interval);
        Live.Updated += Update;
        Update();
    }

    public LiveViewModel Live { get; }

    /// <summary>Set by the window: opens the main window and closes the pop-up.</summary>
    public Action? OpenApp { get; set; }

    [ObservableProperty] private string _header = "";
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private string _modelName = "";
    [ObservableProperty] private string _memoryLine = "";
    [ObservableProperty] private string _lastRequestLine = "";
    [ObservableProperty] private string _queueLine = "";

    /// <summary>Starts reading the host (every 1.5 s). Call when the pop-up is shown.</summary>
    public void Show() => Live.Start();

    /// <summary>Stops reading. Call when the pop-up hides.</summary>
    public void Hide() => Live.Stop();

    [RelayCommand]
    private void Open() => OpenApp?.Invoke();

    private void Update()
    {
        var status = Live.Status;
        IsOff = status is null;
        if (status is null)
        {
            Header = _s.Get("Vm_TrayOff");
            ModelName = "";
            MemoryLine = "";
            LastRequestLine = _s.Get("Vm_TrayLastNone");
            QueueLine = Live.QueueLine;
            return;
        }

        status.Models.TryGetValue(ModelKinds.Chat, out var chat);
        var name = chat?.DisplayName ?? "";
        var busyApps = Live.IsGenerating ? Live.ActivityTitleApps : "";
        Header = status.State switch
        {
            HostStates.Busy => busyApps.Length > 0 ? _s.Format("Vm_TrayBusy", busyApps) : _s.Get("Vm_TrayBusyOther"),
            HostStates.Ready => _s.Format("Vm_TrayReady", name),
            HostStates.Loading => _s.Format("Vm_TrayLoading", name),
            HostStates.Idle => _s.Format("Vm_TrayIdle", name),
            HostStates.NoModel or HostStates.Downloading => _s.Get("Vm_TrayNoModel"),
            _ => _s.Get("Vm_StateError"),
        };
        ModelName = name;
        MemoryLine = Live.HasMemory ? Live.RamLine : "";
        QueueLine = Live.QueueLine;
        LastRequestLine = Live.LastRequest is { } last
            ? _s.Format("Vm_TrayLast", last.AppName, Live.SeenText(last.LastSeen))
            : _s.Get("Vm_TrayLastNone");
    }

    public void Dispose()
    {
        Live.Updated -= Update;
        Live.Dispose();
    }
}
