namespace Portal.AiHost.Contracts.Control;

/// <summary>The "Other devices" settings, inside <see cref="HostSettings"/> (setting remote.enabled, remote.port).</summary>
public sealed class RemoteSettings
{
    /// <summary>Off by default: nothing listens on the network until the user turns this on.</summary>
    public bool Enabled { get; set; }

    /// <summary><see cref="RemoteApi.DefaultPort"/> to <see cref="RemoteApi.LastPort"/> (the range the firewall rule opens).</summary>
    public int Port { get; set; } = RemoteApi.DefaultPort;

    /// <summary>
    /// Also reach this PC away from home through the relay (remote.relay). On by default, but it only does anything
    /// while <see cref="Enabled"/> is on. The relay only passes encrypted bytes along.
    /// </summary>
    public bool Relay { get; set; } = true;

    /// <summary>Replaces <see cref="RemoteApi.RelayDefaultUrl"/> (testing). Null or empty uses the default.</summary>
    public string? RelayUrl { get; set; }

    /// <summary>
    /// "Devices may replace models" (PC-wide, off by default): a device with Model control may unload a model the PC's
    /// own apps are using in order to load another. Off: such a load is refused with <c>in_use_by_host</c>.
    /// </summary>
    public bool AllowReplaceModels { get; set; }

    /// <summary>
    /// Gaming mode (PC-wide, off by default): while a full-screen game or heavy GPU use is detected, calls from devices
    /// are refused with <c>503 host_busy_gaming</c> (docs/client-mode-v1.md 5.7).
    /// </summary>
    public bool GamingMode { get; set; }
}

/// <summary>Payload of remote.device_remove.</summary>
public sealed class RemoveDeviceCommand
{
    public string DeviceId { get; set; } = "";
}

public sealed class PairedDeviceSummary
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The device may list, load and unload this PC's models (off by default, revocable).</summary>
    public bool ModelControl { get; set; }

    /// <summary>The whole device is paused: its calls get <c>503 device_paused</c>.</summary>
    public bool Paused { get; set; }

    /// <summary>Apps (by the name the laptop sent) that are paused on their own.</summary>
    public List<string> PausedApps { get; set; } = [];

    /// <summary>Priority option <c>Equal</c>; false (default) means this PC's own apps go first.</summary>
    public bool PriorityEqual { get; set; }

    public int? MaxRequestsPerDay { get; set; }
    public long? MaxTokensPerDay { get; set; }

    /// <summary>Quiet hours as local "HH:mm" (a window across midnight is fine). Both null: none.</summary>
    public string? QuietStart { get; set; }
    public string? QuietEnd { get; set; }

    /// <summary>Model ids this device may use; empty means every model.</summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary><c>online</c> (a call is running), <c>idle</c> (seen in the last 10 minutes) or <c>offline</c>.</summary>
    public string Status { get; set; } = DeviceStatuses.Offline;

    /// <summary><c>lan</c> or <c>relay</c> for the latest call; null when none yet.</summary>
    public string? Route { get; set; }

    public List<DeviceAppSummary> Apps { get; set; } = [];

    /// <summary>Latest model the device used (id and display name), if any.</summary>
    public string? CurrentModel { get; set; }
    public string? CurrentModelName { get; set; }

    /// <summary>Tokens per second and time to first byte of the latest call.</summary>
    public double? TokensPerSecond { get; set; }
    public int? LatencyMs { get; set; }
}

/// <summary>Values of <see cref="PairedDeviceSummary.Status"/>.</summary>
public static class DeviceStatuses
{
    public const string Online = "online";
    public const string Idle = "idle";
    public const string Offline = "offline";
}

/// <summary>One app of a device (the name it sent in X-Portal-Client-App) with today's counts. Counts only, no content.</summary>
public sealed class DeviceAppSummary
{
    public string Name { get; set; } = "";
    public bool Paused { get; set; }

    /// <summary>Calls running right now.</summary>
    public int Active { get; set; }

    /// <summary>Calls of this app waiting behind another one right now.</summary>
    public int Queued { get; set; }

    public int RequestsToday { get; set; }
    public long TokensToday { get; set; }

    /// <summary>Requests of the last 7 days, oldest first, today last (always 7 entries).</summary>
    public List<int> Last7Days { get; set; } = [];
}

/// <summary>Payload of remote.device_pause: pauses or resumes the whole device (App null) or one app of it.</summary>
public sealed class PauseDeviceCommand
{
    public string DeviceId { get; set; } = "";
    public string? App { get; set; }
    public bool Paused { get; set; }
}

/// <summary>Payload of remote.device_settings: replaces the priority, limits, quiet hours and allowed models of a device.</summary>
public sealed class SetDeviceSettingsCommand
{
    public string DeviceId { get; set; } = "";
    public bool PriorityEqual { get; set; }
    public int? MaxRequestsPerDay { get; set; }
    public long? MaxTokensPerDay { get; set; }
    public string? QuietStart { get; set; }
    public string? QuietEnd { get; set; }
    public List<string> AllowedModels { get; set; } = [];
}

/// <summary>Payload of remote.device_rename.</summary>
public sealed class RenameDeviceCommand
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Payload of event.device_alert: a new pairing, or a device that connected through the relay from away.</summary>
public sealed class DeviceAlert
{
    /// <summary><c>paired</c> or <c>relay_connect</c>.</summary>
    public string Kind { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

/// <summary>A pairing code to show to the user (XXXX-XXXX-XXXX), and the QR text that carries the same code.</summary>
public sealed class PairingOfferInfo
{
    public string Code { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>portalai://pair?h=...&amp;p=...&amp;f=...&amp;c=...&amp;n=...[&amp;r=...]</summary>
    public string QrText { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }

    /// <summary>Certificate fingerprint (base64url); shown so a person can compare it on the other device.</summary>
    public string Fingerprint { get; set; } = "";
}

/// <summary>Answer of remote.get.</summary>
public sealed class RemoteStatus
{
    public bool Enabled { get; set; }
    public bool Listening { get; set; }
    public int Port { get; set; }

    /// <summary>Why it is not listening although it is turned on, or why it cannot be turned on. Null when fine.</summary>
    public string? Problem { get; set; }
    public string HostName { get; set; } = "";

    /// <summary>This PC's private network addresses, best first.</summary>
    public List<string> Addresses { get; set; } = [];
    public string? Fingerprint { get; set; }
    public List<PairedDeviceSummary> Devices { get; set; } = [];

    /// <summary>The open pairing code, if any.</summary>
    public PairingOfferInfo? Pairing { get; set; }

    /// <summary>One of <see cref="RelayStates"/>.</summary>
    public string Relay { get; set; } = RelayStates.Off;

    /// <summary>"Devices may replace models" as saved.</summary>
    public bool AllowReplaceModels { get; set; }

    /// <summary>Gaming mode is on and a game or heavy GPU use is detected right now.</summary>
    public bool GamingBusy { get; set; }

    /// <summary>The models loaded right now and who loaded each (the PC or a device).</summary>
    public List<LoadedModelInfo> LoadedModels { get; set; } = [];
}

/// <summary>One loaded model and who loaded it. Shown as "Loaded by &lt;device&gt;" with a Take back button.</summary>
public sealed class LoadedModelInfo
{
    public string Kind { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Id of the device that loaded it, or null when this PC did.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Name of that device ("a removed device" when it was removed meanwhile), or null for this PC.</summary>
    public string? DeviceName { get; set; }

    /// <summary><c>auto</c>, <c>gpu</c> or <c>cpu</c> as the loader asked.</summary>
    public string Placement { get; set; } = "auto";
}

/// <summary>Payload of remote.model_control: turns Model control on or off for one device.</summary>
public sealed class SetModelControlCommand
{
    public string DeviceId { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>Payload of remote.take_back: unloads the model a device loaded (memory only; no file is touched).</summary>
public sealed class TakeBackCommand
{
    public string Kind { get; set; } = "chat";
}

/// <summary>Values of <see cref="RemoteStatus.Relay"/>.</summary>
public static class RelayStates
{
    /// <summary>The relay is turned off, or other devices are off: home network only.</summary>
    public const string Off = "off";
    public const string Connecting = "connecting";

    /// <summary>The host holds a verified connection to the relay: reachable away from home.</summary>
    public const string Connected = "connected";

    /// <summary>The relay could not be reached (it keeps trying).</summary>
    public const string Unreachable = "unreachable";
}

/// <summary>Modes of the host (docs/client-mode-v1.md section 1.1).</summary>
public static class HostModes
{
    public const string Server = "server";
    public const string Client = "client";
}

/// <summary>Payload of client.setmode: <c>server</c> (this PC runs the AI) or <c>client</c> (use the paired PC).</summary>
public sealed class SetModeCommand
{
    public string Mode { get; set; } = HostModes.Server;
}

/// <summary>Values of the <c>client_fallback</c> setting (docs/client-mode-v1.md section 1.3). Unset (null) reads as <c>none</c>.</summary>
public static class ClientFallbackModes
{
    /// <summary>Apps get <c>503 remote_unreachable</c> and use their own built-in AI (default).</summary>
    public const string None = "none";

    /// <summary>The laptop's own model answers while the home PC cannot be reached.</summary>
    public const string Local = "local";
}

/// <summary>Values of the <c>relay_mode</c> setting (docs/client-mode-v1.md section 3.1).</summary>
public static class RelayModes
{
    /// <summary>The relay is used whenever the home network fails and the pairing has one (default).</summary>
    public const string Auto = "auto";

    /// <summary>The relay is used only after the user turned it on for the current session.</summary>
    public const string Manual = "manual";
}

/// <summary>Payload of client.setfallback: <see cref="ClientFallbackModes.None"/> or <see cref="ClientFallbackModes.Local"/>.</summary>
public sealed class SetFallbackCommand
{
    public string Fallback { get; set; } = ClientFallbackModes.None;
}

/// <summary>Payload of client.setrelaymode: <see cref="RelayModes.Auto"/> or <see cref="RelayModes.Manual"/>.</summary>
public sealed class SetRelayModeCommand
{
    public string RelayMode { get; set; } = RelayModes.Auto;
}

/// <summary>Payload of client.userelay: turns the relay on or off for the current session (matters in Manual mode).</summary>
public sealed class UseRelayCommand
{
    public bool Enabled { get; set; }
}

/// <summary>Answer of client.status and client.setmode. Never holds the PC's address, key or token.</summary>
public sealed class ClientModeStatus
{
    /// <summary><c>server</c> or <c>client</c>.</summary>
    public string Mode { get; set; } = HostModes.Server;

    /// <summary>A PC is saved and could be used.</summary>
    public bool Paired { get; set; }

    public string? PcName { get; set; }

    /// <summary>Link state while in client mode: connecting, connected, unreachable, revoked, fingerprint_changed, stopped.</summary>
    public string? State { get; set; }

    /// <summary><c>lan</c>, <c>relay</c> or <c>down</c>. Null in server mode.</summary>
    public string? Route { get; set; }

    /// <summary>Why the link is down (the remote_unreachable reason), if it is.</summary>
    public string? Reason { get; set; }

    public int? RttMs { get; set; }

    /// <summary>The <c>client_fallback</c> setting: <c>none</c> or <c>local</c>.</summary>
    public string ClientFallback { get; set; } = ClientFallbackModes.None;

    /// <summary>True until the user has chosen a fallback (the setting is still unset).</summary>
    public bool FallbackUnset { get; set; } = true;

    /// <summary>The one-time "home PC unreachable" prompt is waiting to be shown; client.promptshown clears it.</summary>
    public bool FallbackPromptPending { get; set; }

    /// <summary>The laptop's own model is serving new requests right now ("Using this laptop's model").</summary>
    public bool UsingLaptopModel { get; set; }

    /// <summary>The <c>relay_mode</c> setting: <c>auto</c> or <c>manual</c>.</summary>
    public string RelayMode { get; set; } = RelayModes.Auto;

    /// <summary>The relay is switched on for this session (only meaningful in Manual mode; resets when the host quits).</summary>
    public bool UseRelay { get; set; }

    /// <summary>Set by client.setmode when the switch was refused: <c>not_paired</c> or <c>failed</c>.</summary>
    public string? Refused { get; set; }

    /// <summary>This laptop has Model control on the PC (as last seen). Null in server mode or before the PC answered.</summary>
    public bool? ModelControl { get; set; }

    /// <summary>Ids of the models this laptop has loaded on the PC (as last seen).</summary>
    public List<string>? LoadedOnPc { get; set; }
}

/// <summary>Payload of client.remote_placement: a placement to set, or none to read the laptop's placements.</summary>
public sealed class RemotePlacementCommand
{
    public DevicePlacementRequest? Set { get; set; }
}

/// <summary>
/// Answer of the client.remote_* messages. <c>Ok</c> is false when the PC refused or could not be reached; then
/// <c>ErrorCode</c>, <c>Reason</c> and a short plain <c>Message</c> say why. The list is the PC's fresh list when known.
/// </summary>
public sealed class RemoteModelsAnswer
{
    public bool Ok { get; set; }

    /// <summary>This laptop has Model control on the PC (as last seen).</summary>
    public bool ModelControl { get; set; }

    public string? PcName { get; set; }

    /// <summary>The device id this laptop has on the PC; <c>LoadedBy</c> of its own models carries it.</summary>
    public string? DeviceId { get; set; }

    public List<DeviceModelState> Models { get; set; } = [];

    public List<DevicePlacementRequest>? Placements { get; set; }

    public string? ErrorCode { get; set; }

    public string? Reason { get; set; }

    public string? Message { get; set; }
}

/// <summary>Payload of client.appmodel. A missing <c>AppId</c> only reads; a missing <c>ModelId</c> clears the choice (the PC's choice).</summary>
public sealed class AppModelCommand
{
    public string? AppId { get; set; }

    public string? ModelId { get; set; }
}

public sealed class AppModelEntry
{
    public string AppId { get; set; } = "";

    public string ModelId { get; set; } = "";
}

/// <summary>Answer of client.appmodel: every app with a chosen PC model.</summary>
public sealed class AppModelList
{
    public List<AppModelEntry> Apps { get; set; } = [];
}
