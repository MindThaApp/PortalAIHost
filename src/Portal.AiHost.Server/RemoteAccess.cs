using System.Net;
using System.Net.Sockets;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

public sealed class RemoteAccessOptions
{
    /// <summary>The host's own state folder: devices.json, the certificate and its protected key.</summary>
    public required string DataDirectory { get; init; }

    public required IHostKeyProtector KeyProtector { get; init; }

    /// <summary>Revokes every token issued to a device (called when the device is removed).</summary>
    public required Func<string, int> RevokeDevice { get; init; }

    public TimeProvider? Time { get; init; }

    /// <summary>Name shown to a new device. Default: the computer name.</summary>
    public string? HostName { get; init; }

    /// <summary>Tests: listen on this address (null: every interface).</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Tests: which source addresses are served (default: private networks only).</summary>
    public Func<IPAddress?, bool>? AllowSource { get; init; }

    /// <summary>Tests: use this port instead of the setting (0: system chosen).</summary>
    public int? PortOverride { get; init; }

    /// <summary>Tests: addresses to put in the QR code instead of this PC's network addresses.</summary>
    public Func<List<string>>? Addresses { get; init; }
}

/// <summary>
/// "Other devices": owns the paired-device list, the pairing code and the LAN listener, and starts or stops the
/// listener as the setting changes. Off by default; while it is off nothing listens and no code can be opened. The
/// paired-device list is kept (and can be edited) either way.
/// </summary>
public sealed class RemoteAccess : IAsyncDisposable
{
    private readonly RemoteAccessOptions _o;
    private readonly AiHostRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly string _hostName;
    private LanServer? _server;
    private PairingService? _pairing;
    private DeviceRequestVerifier? _verifier;
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _certificate;
    private byte[]? _fingerprint;
    private RemoteSettings _settings = new();
    private string? _problem;
    private bool _disposed;

    public RemoteAccess(AiHostRuntime runtime, RemoteAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        _runtime = runtime;
        _o = options;
        _time = options.Time ?? TimeProvider.System;
        _hostName = options.HostName ?? Environment.MachineName;
        Devices = new DeviceStore(options.DataDirectory, _time);
        Devices.Changed += RaiseChanged;
    }

    public DeviceStore Devices { get; }

    /// <summary>Raised when the devices, the pairing code or the listener changed.</summary>
    public event Action? Changed;

    public bool Listening => _server is not null;

    /// <summary>The port in use, or the configured one while not listening.</summary>
    public int Port => _server?.Port ?? EffectivePort(_settings);

    private void RaiseChanged() => Changed?.Invoke();

    private int EffectivePort(RemoteSettings settings) =>
        _o.PortOverride ?? (settings.Port is >= RemoteApi.DefaultPort and <= RemoteApi.LastPort ? settings.Port : RemoteApi.DefaultPort);

    /// <summary>Starts, restarts or stops the listener to match the settings. Never throws for a bind failure; see <see cref="Problem"/>.</summary>
    public async Task ApplyAsync(RemoteSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var gate = RemoteAccessGate.Check(settings);
            var wantOn = settings.Enabled && gate.Allowed;
            var port = EffectivePort(settings);
            var running = _server is not null;
            _settings = new RemoteSettings { Enabled = settings.Enabled, Port = settings.Port };
            _problem = settings.Enabled && !gate.Allowed ? gate.Reason ?? "Other devices are not available." : null;
            if (running && (!wantOn || _server!.Port != port && _o.PortOverride is null))
            {
                await StopServerAsync().ConfigureAwait(false);
                running = false;
            }

            if (wantOn && !running)
            {
                await StartServerAsync(port, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _applyGate.Release();
        }

        RaiseChanged();
    }

    private async Task StartServerAsync(int port, CancellationToken ct)
    {
        try
        {
            if (_certificate is null)
            {
                _certificate = HostCertificate.LoadOrCreate(_o.DataDirectory, _o.KeyProtector, _time);
                _fingerprint = HostCertificate.Fingerprint(_certificate);
            }

            _pairing = new PairingService(Devices, _hostName, _fingerprint!, _time);
            _pairing.Changed += RaiseChanged;
            _verifier ??= new DeviceRequestVerifier(Devices, _time);
            _server = await LanServer.StartAsync(_runtime, _pairing, _verifier, new LanServerOptions
            {
                Certificate = _certificate,
                Port = port,
                BindAddress = _o.BindAddress,
                AllowSource = _o.AllowSource ?? RemoteNetwork.IsPrivateAddress,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException
            or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        {
            _problem = ex is IOException or SocketException
                ? $"Port {port} is in use or blocked. Pick another port, or close the program that uses it."
                : "The security certificate for this PC could not be opened.";
            _pairing = null;
        }
    }

    private async Task StopServerAsync()
    {
        var server = _server;
        _server = null;
        _pairing?.Cancel();
        _pairing = null;
        if (server is not null)
        {
            await server.StopAsync().ConfigureAwait(false);
        }
    }

    public string? Problem => _problem;

    /// <summary>Opens a pairing code. Throws <see cref="InvalidOperationException"/> when other devices are off.</summary>
    public PairingOfferInfo StartPairing()
    {
        var pairing = _pairing;
        if (_server is null || pairing is null)
        {
            throw new InvalidOperationException("Turn on \"Let other devices use this PC's AI\" first.");
        }

        var offer = pairing.Begin();
        var addresses = AddressList();
        var address = addresses.FirstOrDefault() ?? "";
        var fingerprint = RemoteCodec.Encode(_fingerprint!);
        return new PairingOfferInfo
        {
            Code = offer.Code,
            ExpiresAt = offer.ExpiresAt,
            HostName = _hostName,
            Address = address,
            Port = _server.Port,
            Fingerprint = fingerprint,
            QrText = RemoteNetwork.QrText(address, _server.Port, fingerprint, offer.Code, ShortName(_hostName)),
        };
    }

    public void CancelPairing() => _pairing?.Cancel();

    /// <summary>Forgets the device and revokes its tokens. The device's next request is refused. False for an unknown id.</summary>
    public bool RemoveDevice(string deviceId)
    {
        var removed = Devices.Remove(deviceId);
        if (removed)
        {
            _o.RevokeDevice(deviceId);
        }

        return removed;
    }

    public RemoteStatus Snapshot()
    {
        var offer = _pairing?.Current;
        var addresses = AddressList();
        string? fingerprint = _fingerprint is null ? null : RemoteCodec.Encode(_fingerprint);
        return new RemoteStatus
        {
            Enabled = _settings.Enabled,
            Listening = _server is not null,
            Port = Port,
            Problem = _problem,
            HostName = _hostName,
            Addresses = addresses,
            Fingerprint = fingerprint,
            Devices = [.. Devices.List().Select(d => new PairedDeviceSummary
            {
                DeviceId = d.Id, Name = d.Name, CreatedAt = d.CreatedAt, LastSeenAt = d.LastSeenAt,
            })],
            Pairing = offer is null || _server is null ? null : new PairingOfferInfo
            {
                Code = offer.Code,
                ExpiresAt = offer.ExpiresAt,
                HostName = _hostName,
                Address = addresses.FirstOrDefault() ?? "",
                Port = _server.Port,
                Fingerprint = fingerprint ?? "",
                QrText = RemoteNetwork.QrText(addresses.FirstOrDefault() ?? "", _server.Port, fingerprint ?? "",
                    offer.Code, ShortName(_hostName)),
            },
        };
    }

    private List<string> AddressList() => _o.Addresses?.Invoke() ?? RemoteNetwork.LanAddresses();

    /// <summary>The name in the QR code is cut to keep the code small.</summary>
    private static string ShortName(string name) => name.Length <= 24 ? name : name[..24];

    public async ValueTask DisposeAsync()
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopServerAsync().ConfigureAwait(false);
            _certificate?.Dispose();
            _certificate = null;
        }
        finally
        {
            _applyGate.Release();
        }
    }
}
