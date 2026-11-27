using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// The "Home PC" card (docs/client-mode-v1.md 1.4): the mode, the paired PC and how it is reached, what happens while
/// it is unreachable, the relay policy and Unpair. The host owns every value; a change that fails is shown again as the
/// host has it.
/// </summary>
public sealed partial class SettingsViewModel
{
    public const int ModeThisPc = 0;
    public const int ModeHomePc = 1;

    private bool _homeLoading;
    private bool _homeSubscribed;

    public IReadOnlyList<string> ModeChoices { get; private set; } = [];
    public IReadOnlyList<string> FallbackChoices { get; private set; } = [];
    public IReadOnlyList<string> RelayModeChoices { get; private set; } = [];

    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private bool _homePaired;
    [ObservableProperty] private string _homePcName = "";
    [ObservableProperty] private string _homeRouteText = "";
    [ObservableProperty] private string _homeLastSeenText = "";
    [ObservableProperty] private int _fallbackIndex;
    [ObservableProperty] private int _relayModeIndex;
    [ObservableProperty] private bool _useRelay;
    [ObservableProperty] private bool _homeBusy;
    [ObservableProperty] private string _homeMessage = "";

    /// <summary>The laptop is using the home PC now (fallback and relay rows apply).</summary>
    [ObservableProperty] private bool _homeInClientMode;

    /// <summary>The session "Use relay" switch only matters with a manual relay policy.</summary>
    public bool ShowUseRelay => HomeInClientMode && RelayModeIndex == 1;

    public bool HasHomeMessage => HomeMessage.Length > 0;

    /// <summary>Raised when the pairing dialog should open (no PC is paired, or Change PC).</summary>
    public event Action? PairRequested;

    partial void OnHomeMessageChanged(string value) => OnPropertyChanged(nameof(HasHomeMessage));

    partial void OnHomeInClientModeChanged(bool value) => OnPropertyChanged(nameof(ShowUseRelay));

    private void InitHomePc()
    {
        ModeChoices = [_s.Get("Vm_HomeModeThisPc"), _s.Get("Vm_HomeModeHomePc")];
        FallbackChoices = [_s.Get("Vm_HomeFallbackNone"), _s.Get("Vm_HomeFallbackLocal")];
        RelayModeChoices = [_s.Get("Vm_HomeRelayAuto"), _s.Get("Vm_HomeRelayManual")];
        if (!_homeSubscribed)
        {
            _homeSubscribed = true;
            _shell.ClientLink.Applied += ShowHomePc;
        }
    }

    private void DisposeHomePc()
    {
        if (_homeSubscribed) _shell.ClientLink.Applied -= ShowHomePc;
        _homeSubscribed = false;
    }

    /// <summary>Reads the host's client-mode answer for the card (called when the page opens).</summary>
    public Task LoadHomePcAsync() => _shell.ClientLink.RefreshAsync();

    private void ShowHomePc(ClientModeStatus status)
    {
        _homeLoading = true;
        try
        {
            var link = _shell.ClientLink;
            HomeInClientMode = status.Mode == HostModes.Client;
            ModeIndex = HomeInClientMode ? ModeHomePc : ModeThisPc;
            HomePaired = status.Paired;
            HomePcName = status.PcName ?? "";
            HomeRouteText = HomeInClientMode ? link.ChipText : "";
            HomeLastSeenText = link.LastSeenText;
            FallbackIndex = status.ClientFallback == ClientFallbackModes.Local ? 1 : 0;
            RelayModeIndex = status.RelayMode == RelayModes.Manual ? 1 : 0;
            UseRelay = status.UseRelay;
        }
        finally
        {
            _homeLoading = false;
        }
    }

    // One command at a time: each answer is posted to the UI before the next command runs, so a slow answer
    // can never overwrite the card with a value older than a later change.
    private readonly SemaphoreSlim _homeCalls = new(1, 1);

    private async Task HomeCallAsync(Func<CancellationToken, Task<ClientModeStatus>> call)
    {
        HomeBusy = true;
        HomeMessage = "";
        await _homeCalls.WaitAsync().ConfigureAwait(false);
        try
        {
            var status = await Task.Run(() => call(CancellationToken.None)).ConfigureAwait(false);
            _ui.Post(() =>
            {
                _shell.ClientLink.Apply(status);
                if (status.Refused is not null)
                {
                    HomeMessage = _s.Get("Vm_HomeNeedsPair");
                    PairRequested?.Invoke();
                }
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => HomeMessage = _s.Get("Vm_NoHostTitle"));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => HomeMessage = ex.Message);
        }
        finally
        {
            _homeCalls.Release();
            _ui.Post(() => HomeBusy = false);
            // Whatever the answer was, the card shows what the host has.
            _ = _shell.ClientLink.RefreshAsync();
        }
    }

    partial void OnModeIndexChanged(int value)
    {
        if (_homeLoading) return;
        if (value == ModeHomePc && !HomePaired)
        {
            // Nothing to use yet: show the pairing dialog; the switch goes back until a PC is paired.
            _homeLoading = true;
            ModeIndex = ModeThisPc;
            _homeLoading = false;
            PairRequested?.Invoke();
            return;
        }

        _ = HomeCallAsync(ct => Host.SetClientModeAsync(value == ModeHomePc ? HostModes.Client : HostModes.Server, ct));
    }

    partial void OnFallbackIndexChanged(int value)
    {
        if (_homeLoading) return;
        _ = HomeCallAsync(ct => Host.SetClientFallbackAsync(value == 1 ? ClientFallbackModes.Local : ClientFallbackModes.None, ct));
    }

    partial void OnRelayModeIndexChanged(int oldValue, int newValue)
    {
        OnPropertyChanged(nameof(ShowUseRelay));
        RelayChangedCore(newValue);
    }

    private void RelayChangedCore(int newValue)
    {
        if (_homeLoading) return;
        _ = HomeCallAsync(ct => Host.SetRelayModeAsync(newValue == 1 ? RelayModes.Manual : RelayModes.Auto, ct));
    }

    partial void OnUseRelayChanged(bool value)
    {
        if (_homeLoading) return;
        _ = HomeCallAsync(ct => Host.SetUseRelayAsync(value, ct));
    }

    private IHostControl Host => _shell.Host;

    /// <summary>Opens the pairing dialog (Pair a PC, or Change PC: the old PC is forgotten first).</summary>
    [RelayCommand]
    private void PairHomePc() => PairRequested?.Invoke();

    /// <summary>Forgets the paired PC after a question. Only this app's own pairing files go; models and downloads stay.</summary>
    [RelayCommand]
    private async Task UnpairAsync()
    {
        var ok = await _dialogs.ConfirmAsync(_s.Get("Vm_UnpairTitle"), _s.Format("Vm_UnpairBody", HomePcName),
            _s.Get("Vm_UnpairConfirm"), _s.Get("Vm_UnpairCancel")).ConfigureAwait(false);
        if (!ok) return;
        await HomeCallAsync(ct => Host.UnpairClientAsync(ct)).ConfigureAwait(false);
    }
}
