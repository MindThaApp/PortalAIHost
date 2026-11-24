using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One paired device in the list.</summary>
public sealed record DeviceRow(string DeviceId, string Name, string LastSeenText, string RemoveAutomation);

/// <summary>"Other devices": the switch, the pairing code and the paired-device list.</summary>
public sealed partial class SettingsViewModel
{
    private DateTimeOffset _pairingExpires;

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    [ObservableProperty] private bool _remoteEnabled;
    [ObservableProperty] private bool _remoteRelay = true;
    [ObservableProperty] private string _relayStatusText = "";
    [ObservableProperty] private bool _hasRemoteProblem;
    [ObservableProperty] private string _remoteProblem = "";
    [ObservableProperty] private bool _hasDevices;
    [ObservableProperty] private string _remoteAddressText = "";
    [ObservableProperty] private bool _pairingOpen;
    [ObservableProperty] private string _pairingCode = "";
    [ObservableProperty] private string _pairingQrText = "";
    [ObservableProperty] private string _pairingAddressText = "";
    [ObservableProperty] private string _pairingExpiryText = "";
    [ObservableProperty] private string _pairingFingerprintText = "";

    /// <summary>True while no device is paired (shows the hint).</summary>
    public bool NoDevices => !HasDevices;

    partial void OnHasDevicesChanged(bool value) => OnPropertyChanged(nameof(NoDevices));

    partial void OnRemoteEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = SetRemoteEnabledAsync();
    }

    partial void OnRemoteRelayChanged(bool value)
    {
        if (_loading) return;
        _ = SetRemoteEnabledAsync();
    }

    private async Task SetRemoteEnabledAsync()
    {
        await SaveAsync().ConfigureAwait(false);
        await RefreshRemoteAsync().ConfigureAwait(false);
    }

    private void OnRemoteChanged() => _ = RefreshRemoteAsync();

    /// <summary>Reads the host's other-devices state and shows it. Quiet when the host is not reachable.</summary>
    public async Task RefreshRemoteAsync()
    {
        try
        {
            var status = await _shell.Host.GetRemoteAsync().ConfigureAwait(false);
            _ui.Post(() => ShowRemote(status));
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
        }
    }

    private void ShowRemote(RemoteStatus status)
    {
        var wasLoading = _loading;
        _loading = true;
        RemoteEnabled = status.Enabled;
        _loading = wasLoading;

        RelayStatusText = !status.Enabled ? "" : status.Relay switch
        {
            RelayStates.Connected => _s.Get("Vm_RelayAway"),
            RelayStates.Connecting => _s.Get("Vm_RelayConnecting"),
            RelayStates.Unreachable => _s.Get("Vm_RelayUnreachable"),
            _ => _s.Get("Vm_RelayHomeOnly"),
        };
        RemoteProblem = status.Problem ?? "";
        HasRemoteProblem = status.Problem is { Length: > 0 };
        RemoteAddressText = status.Listening && status.Addresses.Count > 0
            ? _s.Format("Vm_RemoteAddress", status.Addresses[0], status.Port)
            : "";

        Devices.Clear();
        foreach (var d in status.Devices)
        {
            var seen = d.LastSeenAt is { } t ? _s.Format("Vm_RemoteLastSeen", t.LocalDateTime.ToString("g")) : _s.Get("Vm_RemoteNeverSeen");
            Devices.Add(new DeviceRow(d.DeviceId, d.Name, seen, _s.Format("Vm_AutoRemoveDevice", d.Name)));
        }

        HasDevices = Devices.Count > 0;

        if (status.Pairing is { } offer)
        {
            ShowOffer(offer);
        }
        else
        {
            PairingOpen = false;
            PairingCode = "";
            PairingQrText = "";
        }
    }

    private void ShowOffer(PairingOfferInfo offer)
    {
        _pairingExpires = offer.ExpiresAt;
        PairingCode = offer.Code;
        PairingQrText = offer.QrText;
        PairingAddressText = _s.Format("Vm_PairingAddress", offer.Address, offer.Port);
        PairingExpiryText = _s.Format("Vm_PairingExpires", offer.ExpiresAt.LocalDateTime.ToString("t"));
        PairingFingerprintText = _s.Format("Vm_PairingFingerprint", Short(offer.Fingerprint));
        PairingOpen = true;
        _ = CloseWhenExpiredAsync(offer.ExpiresAt);
    }

    /// <summary>First characters of the fingerprint in groups, for comparing by eye on the other device.</summary>
    internal static string Short(string fingerprint) =>
        fingerprint.Length <= 12 ? fingerprint : string.Join(' ', Enumerable.Range(0, 3).Select(i => fingerprint.Substring(i * 4, 4)));

    private async Task CloseWhenExpiredAsync(DateTimeOffset expires)
    {
        var wait = expires - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }

        if (expires == _pairingExpires)
        {
            await RefreshRemoteAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task PairDeviceAsync()
    {
        try
        {
            var offer = await _shell.Host.StartPairingAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Message = "";
                ShowOffer(offer);
            });
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Say(_s.Get("Vm_NoHostTitle"), MessageError));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Say(ex.Message, MessageError));
        }
    }

    [RelayCommand]
    private async Task CancelPairingAsync()
    {
        try
        {
            await _shell.Host.CancelPairingAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HostUnavailableException or HostRequestException)
        {
        }

        _ui.Post(() =>
        {
            PairingOpen = false;
            PairingCode = "";
            PairingQrText = "";
        });
    }

    /// <summary>Asks first, then removes the device. Its access stops at once.</summary>
    [RelayCommand]
    private async Task RemoveDeviceAsync(string? deviceId)
    {
        var row = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (row is null) return;
        var sure = await _dialogs.ConfirmAsync(_s.Format("Vm_RemoveDeviceTitle", row.Name), _s.Format("Vm_RemoveDeviceBody", row.Name),
            _s.Get("Vm_RemoveDeviceConfirm"), _s.Get("Vm_Cancel")).ConfigureAwait(false);
        if (!sure) return;
        try
        {
            await _shell.Host.RemoveDeviceAsync(row.DeviceId).ConfigureAwait(false);
            await RefreshRemoteAsync().ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Say(_s.Get("Vm_NoHostTitle"), MessageError));
        }
        catch (HostRequestException ex)
        {
            _ui.Post(() => Say(ex.Message, MessageError));
        }
    }
}
