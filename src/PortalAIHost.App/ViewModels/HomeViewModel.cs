using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Home page: is the host running, which model is loaded, what is waiting, and safe mode.</summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly IStrings _s;

    public HomeViewModel(ShellViewModel shell, IStrings strings)
    {
        _shell = shell;
        _s = strings;
        _shell.SnapshotChanged += Update;
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.Starting) or nameof(ShellViewModel.StartMessage)) Update();
        };
        Update();
    }

    [ObservableProperty] private bool _noHost;
    [ObservableProperty] private bool _safeMode;
    [ObservableProperty] private bool _hasStoreProblem;
    [ObservableProperty] private string _storeProblem = "";
    [ObservableProperty] private bool _showStatus;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _modelLine = "";
    [ObservableProperty] private string _queueLine = "";
    [ObservableProperty] private string _startMessage = "";
    [ObservableProperty] private bool _canStart = true;
    [ObservableProperty] private bool _upgradeAvailable;

    [RelayCommand]
    private Task StartHostAsync() => _shell.StartHostAsync();

    private void Update()
    {
        var snap = _shell.Snapshot;
        NoHost = !snap.Connected;
        ShowStatus = snap.Connected;
        SafeMode = snap.Connected && snap.SafeMode;
        StoreProblem = snap.Connected ? snap.StoreProblem ?? "" : "";
        HasStoreProblem = StoreProblem.Length > 0;
        StartMessage = _shell.StartMessage;
        CanStart = !_shell.Starting;
        if (snap.Status is not { } st)
        {
            StatusTitle = _s.Get("Vm_NoHostTitle");
            StatusDetail = _s.Get("Vm_NoHostBody");
            ModelLine = "";
            QueueLine = "";
            UpgradeAvailable = false;
            return;
        }
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
        st.Models.TryGetValue(ModelKinds.Chat, out var chat);
        ModelLine = chat is null || chat.State == ModelStates.NotInstalled || string.IsNullOrEmpty(chat.DisplayName)
            ? _s.Get("Vm_ModelNone")
            : _s.Format(chat.State switch
            {
                ModelStates.Loaded => "Vm_ModelLoaded",
                ModelStates.Loading => "Vm_ModelLoading",
                _ => "Vm_ModelUnloaded",
            }, chat.DisplayName);
        QueueLine = st.Queue.Running + st.Queue.Waiting == 0
            ? _s.Get("Vm_QueueEmpty")
            : _s.Format("Vm_QueueSome", st.Queue.Running, st.Queue.Waiting);
        UpgradeAvailable = st.UpgradeAvailable;
    }

    public void Dispose() => _shell.SnapshotChanged -= Update;
}
