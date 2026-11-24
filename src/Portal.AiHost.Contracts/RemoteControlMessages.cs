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

    /// <summary>Set by client.setmode when the switch was refused: <c>not_paired</c> or <c>failed</c>.</summary>
    public string? Refused { get; set; }
}
