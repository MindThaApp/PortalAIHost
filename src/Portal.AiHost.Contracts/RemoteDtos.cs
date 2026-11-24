namespace Portal.AiHost.Contracts;

// LAN remote access (docs/remote-v1.md). Binary values are base64url without padding.

/// <summary>Fixed values of the LAN protocol.</summary>
public static class RemoteApi
{
    public const int DefaultPort = 48123;

    /// <summary>Last port the package firewall rule opens (the rule covers <see cref="DefaultPort"/> to this one).</summary>
    public const int LastPort = 48133;

    public const string PairStartPath = "/pair/start";
    public const string PairFinishPath = "/pair/finish";
    public const string PairingScheme = "portalai";
    public const int CodeLength = 12;
    public const int CodeLifetimeMinutes = 5;
    public const int MaxWrongAttempts = 5;
    public const int MaxClockSkewSeconds = 120;
    public const int MaxDeviceNameLength = 64;
    public const string SigningVersion = "PORTALAI1";
}

/// <summary>Signed-request headers of a paired device.</summary>
public static class RemoteHeaders
{
    public const string Device = "X-Portal-Device";
    public const string Timestamp = "X-Portal-Timestamp";
    public const string Nonce = "X-Portal-Nonce";
    public const string Signature = "X-Portal-Signature";
}

/// <summary>Why a pairing step was refused (details.reason of the pairing_failed error).</summary>
public static class PairingReasons
{
    public const string NoOffer = "no_offer";
    public const string Expired = "expired";
    public const string WrongCode = "wrong_code";
    public const string CodeVoided = "code_voided";
    public const string Invalid = "invalid";
    public const string NoSession = "no_session";
    public const string TooManyDevices = "too_many_devices";
}

/// <summary>Why a signed request was refused (details.reason of the device_unauthorized error).</summary>
public static class DeviceAuthReasons
{
    public const string Malformed = "malformed";
    public const string UnknownDevice = "unknown_device";
    public const string BadSignature = "bad_signature";
    public const string StaleTimestamp = "stale_timestamp";
    public const string Replayed = "replayed";

    /// <summary>Too many requests from this device (or too many remembered request ids); answered with 429.</summary>
    public const string RateLimited = "rate_limited";
}

/// <summary>POST /pair/start body.</summary>
public sealed class PairStartRequest
{
    public string ClientNonce { get; set; } = "";
    public string DeviceName { get; set; } = "";

    /// <summary>P-256 public key, SubjectPublicKeyInfo DER.</summary>
    public string DevicePublicKey { get; set; } = "";

    /// <summary>HMAC-SHA256 under the key derived from the code (proves the client knows the code).</summary>
    public string Mac { get; set; } = "";
}

public sealed class PairStartResponse
{
    public string HostNonce { get; set; } = "";

    /// <summary>SHA-256 of the host certificate, base64url.</summary>
    public string Fingerprint { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Mac { get; set; } = "";
}

public sealed class PairFinishRequest
{
    public string HostNonce { get; set; } = "";
    public string Mac { get; set; } = "";
}

public sealed class PairFinishResponse
{
    public string DeviceId { get; set; } = "";
    public string Mac { get; set; } = "";
}
