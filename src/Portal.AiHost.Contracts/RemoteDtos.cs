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

    /// <summary>Where the host connects out to by default (docs/remote-v1.md, section Relay).</summary>
    public const string RelayDefaultUrl = "https://portal-relay.portalai.workers.dev";
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

// ---- Model control (docs/client-mode-v1.md section 4.7) ---------------------------------------------------------

/// <summary>Placement words of a device load: <c>auto</c> (the PC decides), <c>gpu</c> (graphics card) or <c>cpu</c> (system memory).</summary>
public static class DevicePlacements
{
    public const string Auto = "auto";
    public const string Gpu = "gpu";
    public const string Cpu = "cpu";

    public static bool IsValid(string? value) => value is Auto or Gpu or Cpu;
}

/// <summary>details.reason of the model_conflict error.</summary>
public static class ModelConflictReasons
{
    public const string OwnerChanged = "owner_changed";
    public const string InUseByHost = "in_use_by_host";
    public const string InUseByDevice = "in_use_by_device";
    public const string NotLoadedByDevice = "not_loaded_by_device";
}

/// <summary>One installed model as a device sees it. No download or catalogue data.</summary>
public sealed class DeviceModelState
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "chat";
    public long SizeBytes { get; set; }
    public bool FitsThisPc { get; set; } = true;

    /// <summary><c>unloaded</c>, <c>loading</c> or <c>loaded</c>.</summary>
    public string State { get; set; } = "unloaded";

    public string? Placement { get; set; }

    /// <summary><c>pc</c> or the id of the device that loaded it; null while unloaded.</summary>
    public string? LoadedBy { get; set; }
}

/// <summary>GET /host/models.</summary>
public sealed class DeviceModelList
{
    public List<DeviceModelState> Models { get; set; } = [];
}

/// <summary>POST /host/models/load.</summary>
public sealed class DeviceLoadRequest
{
    public string ModelId { get; set; } = "";

    /// <summary><see cref="DevicePlacements"/>; missing means <c>auto</c>.</summary>
    public string? Placement { get; set; }
}

/// <summary>POST /host/models/unload.</summary>
public sealed class DeviceUnloadRequest
{
    public string ModelId { get; set; } = "";
}

/// <summary>POST /host/placement: the controls the PC has locally, scoped to a model this device loaded. Lowers limits only.</summary>
public sealed class DevicePlacementRequest
{
    public string ModelId { get; set; } = "";
    public string Compute { get; set; } = DevicePlacements.Auto;
    public long? GpuMemoryLimitBytes { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public int? CpuThreads { get; set; }
}

/// <summary>GET /host/placement: the placements of the models this device loaded.</summary>
public sealed class DevicePlacementList
{
    public List<DevicePlacementRequest> Placements { get; set; } = [];
}
