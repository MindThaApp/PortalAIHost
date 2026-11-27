using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote.Client;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>The link to the home PC as the window shows it (docs/client-mode-v1.md 1.4).</summary>
public enum ClientLinkKind
{
    /// <summary>Not in client mode.</summary>
    None,
    Connecting,
    Connected,
    Unreachable,
    Revoked,

    /// <summary>The PC's certificate changed: the user has to pair again.</summary>
    NeedsRepair,
}

/// <summary>
/// One shared view of client mode for Home, Models, Settings and the tray: the state chip, the route, the "using this
/// laptop's model" note and the one-time fallback prompt. Every host call runs off the UI thread; bursts of change
/// pushes become one read; the prompt is raised once per run (and the host remembers it for good).
/// </summary>
public sealed partial class ClientLinkViewModel : ObservableObject, IDisposable
{
    private readonly IHostControl _host;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private readonly TimeProvider _time;
    private readonly TimeSpan _coalesce;
    private readonly CancellationTokenSource _cts = new();
    private int _refreshQueued;
    private int _promptRaised;
    private DateTimeOffset? _lastConnected;

    public ClientLinkViewModel(IHostControl host, IUiDispatcher ui, IStrings strings, TimeProvider? time = null, TimeSpan? coalesce = null)
    {
        _host = host;
        _ui = ui;
        _s = strings;
        _time = time ?? TimeProvider.System;
        _coalesce = coalesce ?? TimeSpan.FromMilliseconds(300);
        host.RemoteChanged += Poke;
        host.StatusChanged += OnHostStatus;
    }

    /// <summary>Raised once, on the UI thread, the first time the PC is unreachable and the host says the prompt is pending.</summary>
    public event Action? FallbackPromptRequested;

    /// <summary>The newest answer of the host (null until the first one).</summary>
    public ClientModeStatus? Status { get; private set; }

    /// <summary>Raised on the UI thread after every applied answer.</summary>
    public event Action<ClientModeStatus>? Applied;

    [ObservableProperty] private bool _isClientMode;
    [ObservableProperty] private bool _paired;
    [ObservableProperty] private string _pcName = "";
    [ObservableProperty] private ClientLinkKind _kind;
    [ObservableProperty] private string _chipText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _lastSeenText = "";
    [ObservableProperty] private bool _usingLaptopModel;
    [ObservableProperty] private string _usingLaptopText = "";
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private string _attentionText = "";

    /// <summary>The AI runs on the other PC and the laptop's own model is not serving: local-model sections stay hidden.</summary>
    public bool HideLocalModel => IsClientMode && !UsingLaptopModel;

    partial void OnIsClientModeChanged(bool value) => OnPropertyChanged(nameof(HideLocalModel));

    partial void OnUsingLaptopModelChanged(bool value) => OnPropertyChanged(nameof(HideLocalModel));

    private void OnHostStatus(HostSnapshot _) => Poke();

    /// <summary>Asks for a fresh read soon. Many calls in a short time make one read.</summary>
    public void Poke()
    {
        if (_cts.IsCancellationRequested || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_coalesce, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _refreshQueued, 0);
                await RefreshAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Reads the host now (off the UI thread) and applies the answer on the UI thread.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ClientModeStatus status;
        try
        {
            status = await _host.GetClientStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HostUnavailableException or HostRequestException)
        {
            return; // The shell shows that the host is away; the last known state stays.
        }

        if (ct.IsCancellationRequested) return;
        _ui.Post(() => Apply(status));
    }

    /// <summary>Applies a host answer (also used for the answer of a command).</summary>
    public void Apply(ClientModeStatus status)
    {
        Status = status;
        IsClientMode = status.Mode == HostModes.Client;
        Paired = status.Paired;
        PcName = status.PcName ?? "";
        var kind = IsClientMode ? KindFor(status.State) : ClientLinkKind.None;
        if (kind == ClientLinkKind.Connected) _lastConnected = _time.GetUtcNow();
        Kind = kind;
        ChipText = kind switch
        {
            ClientLinkKind.Connected => _s.Get(status.Route == RemoteRoutes.Relay ? "Vm_LinkConnectedRelay" : "Vm_LinkConnectedLan"),
            ClientLinkKind.Connecting => _s.Get("Vm_LinkConnecting"),
            ClientLinkKind.Unreachable => _s.Get("Vm_LinkUnreachable"),
            ClientLinkKind.Revoked => _s.Get("Vm_LinkRevoked"),
            ClientLinkKind.NeedsRepair => _s.Get("Vm_LinkRepair"),
            _ => "",
        };
        DetailText = kind == ClientLinkKind.Connected && status.RttMs is { } rtt ? _s.Format("Vm_LinkRtt", PcName, rtt) : PcName;
        NeedsAttention = kind is ClientLinkKind.Unreachable or ClientLinkKind.Revoked or ClientLinkKind.NeedsRepair;
        AttentionText = kind switch
        {
            ClientLinkKind.Revoked => _s.Format("Vm_LinkRevokedHelp", PcName),
            ClientLinkKind.NeedsRepair => _s.Format("Vm_LinkRepairHelp", PcName),
            ClientLinkKind.Unreachable => _s.Format("Vm_LinkUnreachableHelp", PcName),
            _ => "",
        };
        LastSeenText = kind != ClientLinkKind.Connected && _lastConnected is { } seen
            ? _s.Format("Vm_LinkLastSeen", SeenText(seen)) : "";
        UsingLaptopModel = IsClientMode && status.UsingLaptopModel;
        UsingLaptopText = UsingLaptopModel ? _s.Get("Vm_LinkUsingLaptop") : "";

        Applied?.Invoke(status);

        if (IsClientMode && NeedsAttention && status.FallbackPromptPending && Interlocked.Exchange(ref _promptRaised, 1) == 0)
        {
            FallbackPromptRequested?.Invoke();
        }
    }

    private static ClientLinkKind KindFor(string? state) => state switch
    {
        "connected" => ClientLinkKind.Connected,
        "connecting" or null => ClientLinkKind.Connecting,
        "revoked" => ClientLinkKind.Revoked,
        "fingerprint_changed" => ClientLinkKind.NeedsRepair,
        _ => ClientLinkKind.Unreachable,
    };

    private string SeenText(DateTimeOffset at)
    {
        var age = _time.GetUtcNow() - at;
        if (age < TimeSpan.FromMinutes(1)) return _s.Get("Vm_SeenNow");
        if (age < TimeSpan.FromHours(1)) return _s.Format("Vm_SeenMinutes", (int)age.TotalMinutes);
        return _s.Format("Vm_SeenHours", (int)age.TotalHours);
    }

    /// <summary>
    /// The answer to the one-time prompt: saves the choice, then tells the host the prompt was shown so it never comes
    /// back. A closed prompt counts as None.
    /// </summary>
    public async Task AnswerFallbackPromptAsync(bool useLaptopModel, CancellationToken ct = default)
    {
        try
        {
            await _host.SetClientFallbackAsync(useLaptopModel ? ClientFallbackModes.Local : ClientFallbackModes.None, ct).ConfigureAwait(false);
            var status = await _host.MarkFallbackPromptShownAsync(ct).ConfigureAwait(false);
            _ui.Post(() => Apply(status));
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException or OperationCanceledException)
        {
        }
    }

    /// <summary>"Run AI here": leaves client mode (the pairing stays saved).</summary>
    [RelayCommand]
    private async Task RunHereAsync()
    {
        try
        {
            var status = await _host.SetClientModeAsync(HostModes.Server, _cts.Token).ConfigureAwait(false);
            _ui.Post(() => Apply(status));
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException or OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _host.RemoteChanged -= Poke;
        _host.StatusChanged -= OnHostStatus;
        _cts.Cancel();
        _cts.Dispose();
    }
}
