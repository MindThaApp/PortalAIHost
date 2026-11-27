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

    /// <summary>Detects a full-screen game or heavy GPU use for gaming mode. Null: the PC is never busy.</summary>
    public IBusyDetector? BusyDetector { get; init; }

    /// <summary>Tests: the time zone that decides "today" and quiet hours (default: this PC's).</summary>
    public TimeZoneInfo? Zone { get; init; }

    /// <summary>The model store as Model control needs it. Without one a device sees no models.</summary>
    public IModelControlHost? ModelHost { get; init; }

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
    private bool _suspended;

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
        ModelControl = new ModelControl(runtime, Devices, options.ModelHost ?? new NoModelHost(), () => _settings.AllowReplaceModels);
        ModelControl.MapRoutes(runtime.Pipeline);
        ModelControl.Changed += RaiseChanged;
        runtime.DeviceModelControl = id => Devices.Find(id)?.ModelControl == true;
        runtime.HasDeviceClaim = key => ModelControl.HasClaim(key);
        runtime.DeviceAutoLoad = (device, model, kind, ct) => ModelControl.AutoLoadAsync(device, model, kind, ct);
        Sessions = new DeviceSessions(Devices);
        Stats = new DeviceStatsStore(options.DataDirectory, id => Devices.Find(id) is not null, _time, options.Zone);
        Gate = new DeviceGate(Devices, Stats, _time, options.Zone, () => _settings.GamingMode, options.BusyDetector);
        Gate.Alert += a => Alert?.Invoke(a);
        Gate.ActivityChanged += RaiseChanged;
        Devices.Changed += RaiseChanged;
    }

    /// <summary>Per-device usage stats (one small file per device).</summary>
    public DeviceStatsStore Stats { get; }

    /// <summary>Pause, quiet hours, gaming mode, limits and the running calls of the devices.</summary>
    public DeviceGate Gate { get; }

    /// <summary>A new pairing, or a device connecting through the relay from away. The tray shows a notification.</summary>
    public event Action<DeviceAlert>? Alert;

    /// <summary>Pauses or resumes a whole device (<paramref name="app"/> null) or one app of it. False for an unknown device.</summary>
    public bool SetPaused(string deviceId, string? app, bool paused)
    {
        var label = app is null ? null : Core.Remote.Client.RemoteBackend.SanitizeAppName(app);
        return Devices.Update(deviceId, d =>
        {
            if (label is null)
            {
                d.Paused = paused;
            }
            else
            {
                d.PausedApps.RemoveAll(a => string.Equals(a, label, StringComparison.Ordinal));
                if (paused)
                {
                    d.PausedApps.Add(label);
                }
            }
        });
    }

    /// <summary>
    /// Replaces priority, limits, quiet hours and allowed models. A limit of zero or less means none; quiet hours need
    /// both times as HH:mm, otherwise they are cleared. False for an unknown device.
    /// </summary>
    public bool SetDeviceSettings(SetDeviceSettingsCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var quiet = QuietHours.Parse(cmd.QuietStart) is not null && QuietHours.Parse(cmd.QuietEnd) is not null;
        return Devices.Update(cmd.DeviceId, d =>
        {
            d.PriorityEqual = cmd.PriorityEqual;
            d.MaxRequestsPerDay = cmd.MaxRequestsPerDay is > 0 ? cmd.MaxRequestsPerDay : null;
            d.MaxTokensPerDay = cmd.MaxTokensPerDay is > 0 ? cmd.MaxTokensPerDay : null;
            d.QuietStart = quiet ? cmd.QuietStart!.Trim() : null;
            d.QuietEnd = quiet ? cmd.QuietEnd!.Trim() : null;
            d.AllowedModels = [.. cmd.AllowedModels.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal).Take(64)];
        });
    }

    /// <summary>Renames a device. False for an unknown device or an empty name.</summary>
    public bool RenameDevice(string deviceId, string name) => Devices.Rename(deviceId, name);

    public DeviceStore Devices { get; }

    /// <summary>The models paired devices with the grant loaded on this PC.</summary>
    public ModelControl ModelControl { get; }

    private sealed class NoModelHost : IModelControlHost
    {
        public Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InstalledModel>>([]);

        public Task<Portal.AiHost.Core.Runtime.ActiveModel?> ResolveAsync(string modelId, CancellationToken ct) =>
            Task.FromResult<Portal.AiHost.Core.Runtime.ActiveModel?>(null);

        public Task ChangedAsync() => Task.CompletedTask;
    }

    /// <summary>Turns Model control on or off for a device (saved at once, atomic). False for an unknown device.</summary>
    public bool SetModelControl(string deviceId, bool enabled)
    {
        var done = Devices.SetModelControl(deviceId, enabled);
        if (done && !enabled)
        {
            ModelControl.ForgetDevice(deviceId);
        }

        return done;
    }

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

    /// <summary>True while client mode keeps the listener and the relay bridge off whatever the settings say.</summary>
    public bool Suspended => _suspended;

    /// <summary>
    /// Client mode: while suspended nothing listens on the network and no relay connects (a laptop is never a hop to
    /// the home PC). The settings and the paired-device list are kept; clearing the flag restores what they say.
    /// </summary>
    public async Task SuspendAsync(bool suspended, CancellationToken ct = default)
    {
        _suspended = suspended;
        await ApplyAsync(_settings, ct).ConfigureAwait(false);
    }

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
            var wantOn = settings.Enabled && gate.Allowed && !_suspended;
            var port = EffectivePort(settings);
            var running = _server is not null;
            _settings = new RemoteSettings
            {
                Enabled = settings.Enabled, Port = settings.Port, Relay = settings.Relay, RelayUrl = settings.RelayUrl,
                AllowReplaceModels = settings.AllowReplaceModels, GamingMode = settings.GamingMode,
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
                Gate = Gate,
                Time = _time,
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
            ModelControl.ForgetDevice(deviceId);
            Stats.Delete(deviceId); // this device's own stats file only
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
                DeviceId = d.Id, Name = d.Name, CreatedAt = d.CreatedAt, LastSeenAt = d.LastSeenAt, ModelControl = d.ModelControl,
                Paused = d.Paused, PausedApps = [.. d.PausedApps], PriorityEqual = d.PriorityEqual,
                MaxRequestsPerDay = d.MaxRequestsPerDay, MaxTokensPerDay = d.MaxTokensPerDay,
                QuietStart = d.QuietStart, QuietEnd = d.QuietEnd, AllowedModels = [.. d.AllowedModels],
                Status = Gate.StatusOf(d), Route = Gate.RouteOf(d.Id) ?? Stats.View(d.Id).LastRoute,
                Apps = Gate.AppsOf(d),
                CurrentModel = Stats.View(d.Id).LastModel, CurrentModelName = Stats.View(d.Id).LastModelName,
                TokensPerSecond = Stats.View(d.Id).TokensPerSecond, LatencyMs = Stats.View(d.Id).LatencyMs,
            })],
            Relay = RelayState(),
            AllowReplaceModels = _settings.AllowReplaceModels,
            GamingBusy = Gate.GamingBusyNow,
            LoadedModels = [.. ModelControl.Loaded()],
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
