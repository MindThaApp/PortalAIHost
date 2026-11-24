namespace Portal.AiHost.Contracts.Control;

/// <summary>The "Other devices" settings, inside <see cref="HostSettings"/> (setting remote.enabled, remote.port).</summary>
public sealed class RemoteSettings
{
    /// <summary>Off by default: nothing listens on the network until the user turns this on.</summary>
    public bool Enabled { get; set; }

    /// <summary><see cref="RemoteApi.DefaultPort"/> to <see cref="RemoteApi.LastPort"/> (the range the firewall rule opens).</summary>
    public int Port { get; set; } = RemoteApi.DefaultPort;
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

    /// <summary>portalai://pair?h=...&amp;p=...&amp;f=...&amp;c=...&amp;n=...</summary>
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
}
