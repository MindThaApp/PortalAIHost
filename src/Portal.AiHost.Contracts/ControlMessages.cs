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

    /// <summary>Continues a paused or failed download from the partial file. downloads.start does the same for a paused model.</summary>
    public const string ResumeDownload = "downloads.resume";

    /// <summary>Removes an installed or unfinished model through the model store rules (host-created files only; external models are only forgotten).</summary>
    public const string RemoveModel = "models.remove";
    public const string ActivateModel = "models.activate";
    public const string GetSettings = "settings.get";
    public const string SetSettings = "settings.set";
    public const string TryItChat = "tryit.chat";
    public const string TryItCancel = "tryit.cancel";
    public const string Quit = "quit";

    /// <summary>Per-app activity today and which apps generate now. Answers <see cref="ActivityReport"/>.</summary>
    public const string GetActivity = "activity.get";

    /// <summary>Loads the active model of a kind now. Payload <see cref="EngineCommand"/>.</summary>
    public const string LoadEngine = "engine.load";

    /// <summary>Unloads the engine of a kind now (stops the engine process; model files are never touched).</summary>
    public const string UnloadEngine = "engine.unload";

    // Host -> UI answers.
    public const string Ok = "ok";
    public const string Error = "error";

    // Host -> UI pushes (no request Id).
    public const string EventEngineState = "event.engine_state";
    public const string EventApprovalPending = "event.approval_pending";
    public const string EventDownloadProgress = "event.download_progress";
    public const string EventStatusChanged = "event.status_changed";

    /// <summary>The tray icon was clicked: show the small pop-up. Payload <see cref="TrayPopupEvent"/>.</summary>
    public const string EventTrayPopup = "event.tray_popup";
}

/// <summary>Wire envelope. Payload is a JSON object whose type depends on Type.</summary>
public sealed class ControlEnvelope
{
    public string? Id { get; init; }
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; init; }
}

public sealed class ShowRequest
{
    /// <summary>Optional page to open: home, models, apps, settings, tryit.</summary>
    public string? Page { get; init; }
}

public sealed class ControlError
{
    public string Code { get; set; } = ErrorCodes.InternalError;
    public string Message { get; set; } = "";
}

public sealed class PendingApproval
{
    public string RequestId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string? AppVersion { get; init; }

    /// <summary>Publisher of the calling app (package publisher or file signer), when Windows can tell; null otherwise.</summary>
    public string? Publisher { get; init; }
    public string? Reason { get; init; }

    /// <summary>Verified package name, or an exe path with an "unverified" mark.</summary>
    public string CallerIdentity { get; set; } = "";

    /// <summary>Windows reported a package identity for the caller. Says nothing about who made the app.</summary>
    public bool CallerVerified { get; init; }

    /// <summary>
    /// The caller is a Store-installed package of the Portal publisher. Only this may be shown as a Portal app;
    /// a missing value (older host) reads as false.
    /// </summary>
    public bool CallerPortalApp { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class ApprovalList
{
    public List<PendingApproval> Pending { get; set; } = new();
}

public sealed class ApprovalDecision
{
    public string RequestId { get; set; } = "";
}

public sealed class ClientSummary
{
    public string TokenId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string DeviceId { get; set; } = "local";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public bool Revoked { get; init; }
}

public sealed class ClientList
{
    public List<ClientSummary> Clients { get; set; } = new();
}

public sealed class ClientRevoke
{
    public string TokenId { get; set; } = "";
}

public sealed class CatalogueModelSummary
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = ModelKinds.Chat;
    public string Engine { get; set; } = EngineKinds.LlamaServer;
    public long SizeBytes { get; init; }
    public double Quality { get; init; }
    public bool Recommended { get; init; }
    public bool Installed { get; init; }
    public bool Active { get; init; }
    public bool FitsThisPc { get; init; }

    /// <summary>True when the host found this model already on the PC (a file added in place) instead of downloading it.</summary>
    public bool ExistingCopy { get; init; }

    /// <summary>Download state when not finished: "downloading", "paused", "failed", or null when there is no partial download.</summary>
    public string? DownloadState { get; init; }

    /// <summary>Bytes already on disk for an unfinished download (resume continues from here).</summary>
    public long BytesDone { get; init; }

    /// <summary>Plain-language reason from the recommender.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Null for the current models folder. Set for a model left in an earlier models folder after a folder change:
    /// it can only be removed (send this folder with models.remove), not used or downloaded.
    /// </summary>
    public string? StoreFolder { get; init; }
}

public sealed class ModelListing
{
    public List<CatalogueModelSummary> Models { get; set; } = new();

    /// <summary>Folder the host keeps its models in.</summary>
    public string? ModelsFolder { get; init; }

    /// <summary>Free bytes on the drive of <see cref="ModelsFolder"/>.</summary>
    public long FreeBytes { get; init; }

    /// <summary>Catalogue id of the recommended upgrade over the active chat model, or null.</summary>
    public string? UpgradeModelId { get; init; }

    /// <summary>
    /// False when no trusted, signed model list could be loaded: only installed models are listed. True when the
    /// field is missing (an older host), so the window never shows a false "list unavailable".
    /// </summary>
    public bool CatalogueAvailable { get; set; } = true; // set, not init: generated readers would reset an init default
}

public sealed class DownloadCommand
{
    public string CatalogueId { get; set; } = "";
}

public sealed class RemoveModelCommand
{
    public string CatalogueId { get; set; } = "";

    /// <summary>Earlier models folder to remove from (a listing's store_folder); null for the current folder.</summary>
    public string? StoreFolder { get; init; }
}

/// <summary>Answer to settings.set.</summary>
public sealed class SettingsSaved
{
    /// <summary>True when the models folder changed. Models already downloaded are not moved or deleted.</summary>
    public bool ModelsFolderChanged { get; init; }

    /// <summary>True when some saved values only apply after the host restarts.</summary>
    public bool AppliesAtRestart { get; init; }

    /// <summary>Plain-language note for the window, for example where the old models stay.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Set when the new models folder was refused (missing, FAT32, inside the current models folder). The other
    /// settings were saved; the models folder stayed as it was.
    /// </summary>
    public string? FolderRefused { get; init; }
}

/// <summary>Start-with-Windows states (Windows StartupTask). The window reads and changes these itself.</summary>
public static class StartupStates
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string DisabledByUser = "disabled_by_user";
    public const string DisabledByPolicy = "disabled_by_policy";
    public const string EnabledByPolicy = "enabled_by_policy";
    /// <summary>Not a packaged install (for example a development run): start with Windows cannot be set.</summary>
    public const string Unavailable = "unavailable";
}

public sealed class ActivateModelCommand
{
    public string CatalogueId { get; set; } = "";
}

public sealed class DownloadProgressEvent
{
    public string CatalogueId { get; set; } = "";

    /// <summary>queued, downloading, paused, verifying, done, failed.</summary>
    public string State { get; set; } = "queued";

    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double? EtaSeconds { get; init; }
    public string? Message { get; init; }
}

public sealed class EngineStateEvent
{
    public string Kind { get; set; } = ModelKinds.Chat;

    /// <summary>stopped, starting, loading, ready, stopping, failed.</summary>
    public string State { get; set; } = "stopped";

    public double? Progress { get; init; }
    public string? Message { get; init; }
}

public sealed class HostSettings
{
    public bool StartWithWindows { get; init; }

    /// <summary>auto, gpu or cpu.</summary>
    public string Backend { get; set; } = "auto";

    /// <summary>Folder chosen for models; the host keeps them in its own "Portal AI Host Models" subfolder there.</summary>
    public string? ModelsFolder { get; set; }

    /// <summary>
    /// Earlier models folders (store folders) kept after a folder change, so their models stay listed and can be
    /// removed. Owned by the host: the value sent with settings.set is ignored.
    /// </summary>
    public List<string>? PreviousStoreFolders { get; set; }

    public int MaxMemoryPercent { get; set; } = 80;
    public bool PreventSleepWhileDownloading { get; set; } = true;
    public long? BandwidthCapBytesPerSecond { get; init; }

    /// <summary>Per model kind: keep loaded and idle timeout.</summary>
    public Dictionary<string, KindSettings> Kinds { get; set; } = new();
}

public sealed class KindSettings
{
    public bool KeepLoaded { get; init; }
    public int IdleUnloadSeconds { get; set; } = 600;
}

/// <summary>"Try it" request: a chat request the window sends through the host's own pipeline.</summary>
public sealed class TryItRequest
{
    public ChatCompletionRequest Request { get; set; } = new();
}

/// <summary>Answer to status.get and payload of event.status_changed: the HTTP status plus host-only state.</summary>
public sealed class ControlStatus
{
    [JsonPropertyName("status")]
    public HostStatus Status { get; set; } = new();

    /// <summary>True after two unclean exits in a row: nothing is loaded automatically.</summary>
    [JsonPropertyName("safe_mode")]
    public bool SafeMode { get; init; }

    /// <summary>Unclean exits in a row before this start.</summary>
    [JsonPropertyName("unclean_exits")]
    public int UncleanExits { get; init; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; init; }

    /// <summary>Catalogue id of the recommended upgrade over the active chat model, or null.</summary>
    [JsonPropertyName("upgrade_model_id")]
    public string? UpgradeModelId { get; init; }

    /// <summary>True while the PC runs on battery (idle unload uses the shorter timeout).</summary>
    [JsonPropertyName("on_battery")]
    public bool OnBattery { get; init; }

    /// <summary>
    /// Plain-language reason the models folder could not be opened (for example a folder without the host's marker
    /// or a damaged store.json, both left untouched), or null. Downloads are off while this is set.
    /// </summary>
    [JsonPropertyName("store_problem")]
    public string? StoreProblem { get; init; }
}

/// <summary>Payload of engine.load and engine.unload.</summary>
public sealed class EngineCommand
{
    /// <summary>Model kind; chat when missing.</summary>
    public string? Kind { get; init; }
}

/// <summary>One app's activity today. Counts and times only, never content.</summary>
public sealed class AppActivity
{
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public int Requests { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public long Tokens { get; init; }

    /// <summary>Requests of this app running right now.</summary>
    public int Running { get; init; }
}

/// <summary>Answer to activity.get. Reset at local midnight; not saved.</summary>
public sealed class ActivityReport
{
    public List<AppActivity> Apps { get; set; } = new();
}

/// <summary>Where the tray icon was clicked, in screen pixels.</summary>
public sealed class TrayPopupEvent
{
    public int X { get; init; }
    public int Y { get; init; }
}
