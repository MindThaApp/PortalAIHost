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

    /// <summary>
    /// Tests only: listen on this address instead of every interface. Must be a loopback address, so that no test
    /// ever opens a port on the real network (that raises a Windows Firewall prompt).
    /// </summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>Tests: which source addresses are served (default: private networks only).</summary>
    public Func<IPAddress?, bool>? AllowSource { get; init; }

    /// <summary>Tests: use this port instead of the setting (0: system chosen).</summary>
    public int? PortOverride { get; init; }

    /// <summary>Tests: addresses to put in the QR code instead of this PC's network addresses.</summary>
    public Func<List<string>>? Addresses { get; init; }

    /// <summary>
    /// Use the built-in relay address when the setting names none. Off unless the real host turns it on, so that no
    /// test ever reaches the internet by accident.
    /// </summary>
    public bool UseDefaultRelay { get; init; }

    /// <summary>Tests: wait this long before the first reconnect to the relay (default 2 s).</summary>
    public TimeSpan? RelayInitialDelay { get; init; }

    /// <summary>Tests: longest wait between reconnects to the relay (default 5 minutes).</summary>
    public TimeSpan? RelayMaxDelay { get; init; }
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
    private bool _serverHasBridge;
    private RelayBridge? _bridge;
    private string? _bridgeUrl;
    private PairingService? _pairing;
    private DeviceRequestVerifier? _verifier;
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _certificate;
    private byte[]? _fingerprint;
    private RemoteSettings _settings = new();
    private string? _problem;
    private bool _disposed;

    public RemoteAccess(AiHostRuntime runtime, RemoteAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BindAddress is { } bind && !IPAddress.IsLoopback(bind))
        {
            throw new ArgumentException("A test listener may only bind a loopback address.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _o = options;
        _time = options.Time ?? TimeProvider.System;
        _hostName = options.HostName ?? Environment.MachineName;
        Devices = new DeviceStore(options.DataDirectory, _time);
        Sessions = new DeviceSessions(Devices);
        Devices.Changed += RaiseChanged;
    }

    public DeviceStore Devices { get; }

    /// <summary>What each paired device has in flight; cancelled when the device is removed.</summary>
    public DeviceSessions Sessions { get; }

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
            _settings = new RemoteSettings
            {
                Enabled = settings.Enabled, Port = settings.Port, Relay = settings.Relay, RelayUrl = settings.RelayUrl,
            };
            var wantRelay = wantOn && settings.Relay;
            _problem = settings.Enabled && !gate.Allowed ? gate.Reason ?? "Other devices are not available." : null;
            var url = wantRelay ? RelayUrl(settings, _o.UseDefaultRelay) : null;
            var noAddress = string.IsNullOrWhiteSpace(settings.RelayUrl) && !_o.UseDefaultRelay;
            if (wantRelay && url is null && !noAddress)
            {
                _problem = "The relay address is not valid, so this PC can only be reached at home.";
            }

            if (_bridge is not null && (!wantRelay || url is null || url != _bridgeUrl))
            {
                await StopBridgeAsync().ConfigureAwait(false);
            }

            if (running && (!wantOn || _server!.Port != port && _o.PortOverride is null || _serverHasBridge != wantRelay))
            {
                await StopServerAsync().ConfigureAwait(false);
                running = false;
            }

            if (wantOn && !running)
            {
                await StartServerAsync(port, wantRelay, ct).ConfigureAwait(false);
            }

            if (wantRelay && _bridge is null && _server?.BridgeEndPoint is { } target && url is not null && _certificate is not null)
            {
                _bridgeUrl = url;
                _bridge = RelayBridge.Start(new RelayBridgeOptions
                {
                    Url = new Uri(url),
                    Certificate = _certificate,
                    LocalTarget = target,
                    InitialDelay = _o.RelayInitialDelay ?? TimeSpan.FromSeconds(2),
                    MaxDelay = _o.RelayMaxDelay ?? TimeSpan.FromMinutes(5),
                });
                _bridge.StateChanged += RaiseChanged;
            }
        }
        finally
        {
            _applyGate.Release();
        }

        RaiseChanged();
    }

    /// <summary>The relay address to use: the setting if it is valid, else the default. Null if neither is usable.</summary>
    internal static string? RelayUrl(RemoteSettings settings, bool useDefault)
    {
        if (string.IsNullOrWhiteSpace(settings.RelayUrl) && !useDefault)
        {
            return null;
        }

        var text = string.IsNullOrWhiteSpace(settings.RelayUrl) ? RemoteApi.RelayDefaultUrl : settings.RelayUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
        {
            return null;
        }

        // https only; plain http is for a relay on this very PC (tests).
        var ok = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return ok ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    private async Task StopBridgeAsync()
    {
        var bridge = _bridge;
        _bridge = null;
        _bridgeUrl = null;
        if (bridge is not null)
        {
            bridge.StateChanged -= RaiseChanged;
            await bridge.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StartServerAsync(int port, bool bridgeEndpoint, CancellationToken ct)
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
            _server = await LanServer.StartAsync(_runtime, _pairing, _verifier, Sessions, new LanServerOptions
            {
                Certificate = _certificate,
                Port = port,
                BindAddress = _o.BindAddress,
                AllowSource = _o.AllowSource ?? RemoteNetwork.IsPrivateAddress,
                EnableBridgeEndpoint = bridgeEndpoint,
            }, ct).ConfigureAwait(false);
            _serverHasBridge = bridgeEndpoint;
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
        await StopBridgeAsync().ConfigureAwait(false);
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
            QrText = RemoteNetwork.QrText(address, _server.Port, fingerprint, PairingCrypto.Normalize(offer.Code)!, ShortName(_hostName), PairingRelayUrl()),
        };
    }

    public void CancelPairing() => _pairing?.Cancel();

    /// <summary>Forgets the device and revokes its tokens. The device's next request is refused. False for an unknown id.</summary>
    public bool RemoveDevice(string deviceId)
    {
        var removed = Devices.Remove(deviceId);
        if (removed)
        {
            Sessions.Revoke(deviceId);
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
            Relay = RelayState(),
            Pairing = offer is null || _server is null ? null : new PairingOfferInfo
            {
                Code = offer.Code,
                ExpiresAt = offer.ExpiresAt,
                HostName = _hostName,
                Address = addresses.FirstOrDefault() ?? "",
                Port = _server.Port,
                Fingerprint = fingerprint ?? "",
                QrText = RemoteNetwork.QrText(addresses.FirstOrDefault() ?? "", _server.Port, fingerprint ?? "",
                    PairingCrypto.Normalize(offer.Code)!, ShortName(_hostName), PairingRelayUrl()),
            },
        };
    }

    /// <summary>The relay address that goes into the pairing QR code: null while the relay is off or has no usable address.</summary>
    private string? PairingRelayUrl() => _settings.Enabled && _settings.Relay ? RelayUrl(_settings, _o.UseDefaultRelay) : null;

    private string RelayState() => _bridge?.State ?? RelayStates.Off;

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
