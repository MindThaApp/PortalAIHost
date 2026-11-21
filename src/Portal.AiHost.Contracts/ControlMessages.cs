using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.AiHost.Contracts.Control;

// Admin messages between the window and the host over the per-user named pipe
// (\\.\pipe\PortalAIHost.Control.<UserSID>). There is no admin route on HTTP.
// Framing: one UTF-8 JSON ControlEnvelope per line.

/// <summary>Message type names.</summary>
public static class ControlTypes
{
    // UI -> host requests (each is answered by one envelope with the same Id).
    public const string Show = "show";
    public const string GetStatus = "status.get";
    public const string ListApprovals = "approvals.list";
    public const string Approve = "approvals.approve";
    public const string Deny = "approvals.deny";
    public const string ListClients = "clients.list";
    public const string RevokeClient = "clients.revoke";
    public const string ListModels = "models.list";
    public const string StartDownload = "downloads.start";
    public const string PauseDownload = "downloads.pause";
    public const string CancelDownload = "downloads.cancel";
    public const string ActivateModel = "models.activate";
    public const string GetSettings = "settings.get";
    public const string SetSettings = "settings.set";
    public const string TryItChat = "tryit.chat";
    public const string Quit = "quit";

    // Host -> UI answers.
    public const string Ok = "ok";
    public const string Error = "error";

    // Host -> UI pushes (no request Id).
    public const string EventEngineState = "event.engine_state";
    public const string EventApprovalPending = "event.approval_pending";
    public const string EventDownloadProgress = "event.download_progress";
    public const string EventStatusChanged = "event.status_changed";
}

/// <summary>Wire envelope. Payload is a JSON object whose type depends on Type.</summary>
public sealed class ControlEnvelope
{
    public string? Id { get; init; }
    public string Type { get; init; } = "";
    public JsonElement? Payload { get; init; }
}

public sealed class ShowRequest
{
    /// <summary>Optional page to open: home, models, apps, settings, tryit.</summary>
    public string? Page { get; init; }
}

public sealed class ControlError
{
    public string Code { get; init; } = ErrorCodes.InternalError;
    public string Message { get; init; } = "";
}

public sealed class PendingApproval
{
    public string RequestId { get; init; } = "";
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string? AppVersion { get; init; }
    public string? Reason { get; init; }

    /// <summary>Verified package name, or an exe path with an "unverified" mark.</summary>
    public string CallerIdentity { get; init; } = "";

    public bool CallerVerified { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class ApprovalList
{
    public List<PendingApproval> Pending { get; init; } = new();
}

public sealed class ApprovalDecision
{
    public string RequestId { get; init; } = "";
}

public sealed class ClientSummary
{
    public string TokenId { get; init; } = "";
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string DeviceId { get; init; } = "local";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public bool Revoked { get; init; }
}

public sealed class ClientList
{
    public List<ClientSummary> Clients { get; init; } = new();
}

public sealed class ClientRevoke
{
    public string TokenId { get; init; } = "";
}

public sealed class CatalogueModelSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Kind { get; init; } = ModelKinds.Chat;
    public string Engine { get; init; } = EngineKinds.LlamaServer;
    public long SizeBytes { get; init; }
    public double Quality { get; init; }
    public bool Recommended { get; init; }
    public bool Installed { get; init; }
    public bool Active { get; init; }
    public bool FitsThisPc { get; init; }

    /// <summary>Plain-language reason from the recommender.</summary>
    public string? Reason { get; init; }
}

public sealed class ModelListing
{
    public List<CatalogueModelSummary> Models { get; init; } = new();
}

public sealed class DownloadCommand
{
    public string CatalogueId { get; init; } = "";
}

public sealed class ActivateModelCommand
{
    public string CatalogueId { get; init; } = "";
}

public sealed class DownloadProgressEvent
{
    public string CatalogueId { get; init; } = "";

    /// <summary>queued, downloading, paused, verifying, done, failed.</summary>
    public string State { get; init; } = "queued";

    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double? EtaSeconds { get; init; }
    public string? Message { get; init; }
}

public sealed class EngineStateEvent
{
    public string Kind { get; init; } = ModelKinds.Chat;

    /// <summary>stopped, starting, loading, ready, stopping, failed.</summary>
    public string State { get; init; } = "stopped";

    public double? Progress { get; init; }
    public string? Message { get; init; }
}

public sealed class HostSettings
{
    public bool StartWithWindows { get; init; }

    /// <summary>auto, gpu or cpu.</summary>
    public string Backend { get; init; } = "auto";

    public string? ModelsFolder { get; init; }
    public int MaxMemoryPercent { get; init; } = 80;
    public bool PreventSleepWhileDownloading { get; init; } = true;
    public long? BandwidthCapBytesPerSecond { get; init; }

    /// <summary>Per model kind: keep loaded and idle timeout.</summary>
    public Dictionary<string, KindSettings> Kinds { get; init; } = new();
}

public sealed class KindSettings
{
    public bool KeepLoaded { get; init; }
    public int IdleUnloadSeconds { get; init; } = 600;
}

/// <summary>"Try it" request: a chat request the window sends through the host's own pipeline.</summary>
public sealed class TryItRequest
{
    public ChatCompletionRequest Request { get; init; } = new();
}

/// <summary>Answer to status.get and payload of event.status_changed: the HTTP status plus host-only state.</summary>
public sealed class ControlStatus
{
    [JsonPropertyName("status")]
    public HostStatus Status { get; init; } = new();

    /// <summary>True after two unclean exits in a row: nothing is loaded automatically.</summary>
    [JsonPropertyName("safe_mode")]
    public bool SafeMode { get; init; }

    /// <summary>Unclean exits in a row before this start.</summary>
    [JsonPropertyName("unclean_exits")]
    public int UncleanExits { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }
}
