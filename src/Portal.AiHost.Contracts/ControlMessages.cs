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

    /// <summary>Pauses, resumes or ranks one approved app. Payload <see cref="AppControlCommand"/>. Approval is kept.</summary>
    public const string SetAppControl = "apps.set_control";
    public const string ListModels = "models.list";
    public const string StartDownload = "downloads.start";
    public const string PauseDownload = "downloads.pause";
    public const string CancelDownload = "downloads.cancel";

    /// <summary>Continues a paused or failed download from the partial file. downloads.start does the same for a paused model.</summary>
    public const string ResumeDownload = "downloads.resume";

    /// <summary>Removes an installed or unfinished model through the model store rules (host-created files only; external models are only forgotten).</summary>
    public const string RemoveModel = "models.remove";
    public const string ActivateModel = "models.activate";

    /// <summary>Re-hashes an installed model against the model list. Payload <see cref="VerifyModelCommand"/>; progress arrives as <see cref="EventStoreProgress"/>. Nothing is deleted.</summary>
    public const string VerifyModel = "models.verify";

    /// <summary>
    /// "Add your own model": copies a GGUF file the user picked into the host's models folder. Payload
    /// <see cref="ImportModelCommand"/>; answers once the copy has started, progress and the result arrive as
    /// <see cref="EventStoreProgress"/> (operation "import"). The original file is only read. Cancel with <see cref="CancelStoreOperation"/>.
    /// </summary>
    public const string ImportModel = "models.import";

    /// <summary>Answers <see cref="StoreFolderInfo"/>: the models folder the window may open in Explorer. The host changes nothing.</summary>
    public const string OpenStoreFolder = "store.open_folder";

    /// <summary>Moves the models to another drive or folder. Payload <see cref="MoveStoreCommand"/>; progress arrives as <see cref="EventStoreProgress"/>.</summary>
    public const string MoveStore = "store.move";

    /// <summary>Cancels the running move or check. A cancelled move leaves the old models folder as it was.</summary>
    public const string CancelStoreOperation = "store.cancel";

    /// <summary>Answers <see cref="CleanupScan"/>: what the host itself left behind and could remove. Reads only; nothing is deleted.</summary>
    public const string ScanCleanup = "store.cleanup_scan";

    /// <summary>Removes the items of an earlier scan the user confirmed (<see cref="CleanupCommand"/>); answers <see cref="CleanupResult"/>. Every item is checked again right before it is deleted.</summary>
    public const string RunCleanup = "store.cleanup";
    public const string GetSettings = "settings.get";

    /// <summary>
    /// Answers <see cref="PerformanceView"/>: the limits in force, hardware ranges and the fit preview. An optional
    /// <see cref="HostSettings"/> payload previews those values without saving them.
    /// </summary>
    public const string GetPerformance = "performance.get";

    /// <summary>Answers <see cref="HealthReport"/>: the health checks. Read-only.</summary>
    public const string GetHealth = "health.get";
    public const string SetSettings = "settings.set";
    public const string TryItChat = "tryit.chat";
    public const string TryItCancel = "tryit.cancel";

    /// <summary>
    /// Streaming "Try it". Payload <see cref="TryItRequest"/> (with a run id). The answer, <see cref="TryItDone"/>, comes when
    /// the reply has finished; meanwhile the reply arrives as <see cref="EventTryItChunk"/> pushes carrying the same run id.
    /// </summary>
    public const string TryItStream = "tryit.stream";
    public const string Quit = "quit";

    /// <summary>Per-app activity today and which apps generate now. Answers <see cref="ActivityReport"/>.</summary>
    public const string GetActivity = "activity.get";

    /// <summary>Loads the active model of a kind now. Payload <see cref="EngineCommand"/>.</summary>
    public const string LoadEngine = "engine.load";

    /// <summary>Unloads the engine of a kind now (stops the engine process; model files are never touched).</summary>
    public const string UnloadEngine = "engine.unload";

    /// <summary>Answers <see cref="RemoteStatus"/>: the "Other devices" state, paired devices and the open pairing code.</summary>
    public const string RemoteGet = "remote.get";

    /// <summary>Opens a pairing code (valid 5 minutes) and answers <see cref="PairingOfferInfo"/>. Needs remote access turned on.</summary>
    public const string RemotePairStart = "remote.pair_start";

    /// <summary>Voids the open pairing code.</summary>
    public const string RemotePairCancel = "remote.pair_cancel";

    /// <summary>Removes a paired device and revokes its tokens at once. Payload <see cref="RemoveDeviceCommand"/>.</summary>
    public const string RemoteRemoveDevice = "remote.device_remove";

    /// <summary>Answers <see cref="ClientModeStatus"/>: the host mode and, in client mode, the state of the link to the PC.</summary>
    public const string ClientStatus = "client.status";

    /// <summary>Switches between <c>server</c> and <c>client</c> mode. Payload <see cref="SetModeCommand"/>; answers <see cref="ClientModeStatus"/>.</summary>
    public const string ClientSetMode = "client.setmode";

    // Host -> UI answers.
    public const string Ok = "ok";
    public const string Error = "error";

    // Host -> UI pushes (no request Id).
    public const string EventEngineState = "event.engine_state";
    public const string EventApprovalPending = "event.approval_pending";
    public const string EventDownloadProgress = "event.download_progress";
    public const string EventStatusChanged = "event.status_changed";

    /// <summary>A piece of a streaming "Try it" reply. Payload <see cref="TryItChunkEvent"/>.</summary>
    public const string EventTryItChunk = "event.tryit_chunk";

    /// <summary>Progress of a move or a check. Payload <see cref="StoreProgressEvent"/>.</summary>
    public const string EventStoreProgress = "event.store_progress";

    /// <summary>The tray icon was clicked: show the small pop-up. Payload <see cref="TrayPopupEvent"/>.</summary>
    public const string EventTrayPopup = "event.tray_popup";

    /// <summary>Paired devices, the pairing code or the listener changed: ask <see cref="RemoteGet"/> again. No payload.</summary>
    public const string EventRemoteChanged = "event.remote_changed";
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

    /// <summary>Name of the paired device the request came from; null for this PC.</summary>
    public string? DeviceName { get; init; }
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

    /// <summary>Name of the paired device; null for this PC.</summary>
    public string? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public bool Revoked { get; init; }

    /// <summary>True while the user has paused this app. Its new requests get app_paused.</summary>
    public bool Paused { get; init; }

    /// <summary>"high", "normal" or "low" (<see cref="AppPriorities"/>).</summary>
    public string Priority { get; init; } = AppPriorities.Normal;
}

/// <summary>Values of the per-app priority.</summary>
public static class AppPriorities
{
    public const string High = "high";
    public const string Normal = "normal";
    public const string Low = "low";

    public static bool IsValid(string? value) => value is High or Normal or Low;
}

/// <summary>Payload of apps.set_control. A null field keeps its current value.</summary>
public sealed class AppControlCommand
{
    public string AppId { get; set; } = "";
    public bool? Paused { get; set; }
    public string? Priority { get; set; }
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

    /// <summary>True for a model file the user added ("Add your own model"). The host holds its own copy; the memory fit is not estimated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserAdded { get; init; }

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

    /// <summary>When the model was last loaded by the host (UTC), or null when never or not tracked.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>True after a check found a file that does not match the model list. Nothing was deleted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Damaged { get; init; }

    /// <summary>Why the check marked it damaged, or null.</summary>
    public string? DamagedReason { get; init; }

    /// <summary>One sentence on what the model is good at (model list advice), or null.</summary>
    public string? AdviceSummary { get; init; }

    /// <summary>Short tasks it suits (model list advice).</summary>
    public List<string>? GoodFor { get; init; }

    /// <summary>Short honest limits (model list advice).</summary>
    public List<string>? Caveats { get; init; }

    /// <summary>Memory (RAM plus graphics memory) the model needs on this PC according to the planner, or 0 when unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long MemoryNeededBytes { get; init; }

    public string? LicenceName { get; init; }
    public string? LicenceUrl { get; init; }
}

public sealed class ModelListing
{
    public List<CatalogueModelSummary> Models { get; set; } = new();

    /// <summary>Folder the host keeps its models in.</summary>
    public string? ModelsFolder { get; init; }

    /// <summary>Free bytes on the drive of <see cref="ModelsFolder"/>.</summary>
    public long FreeBytes { get; init; }

    /// <summary>Disk space the installed and partial models use in the current models folder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ModelsBytes { get; init; }

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

    /// <summary>
    /// When the removed model is in use: the installed model of the same kind to switch to first. Null: the host goes
    /// to "no model" for that kind. Ignored when the model is not in use.
    /// </summary>
    public string? SwitchToId { get; init; }

    /// <summary>
    /// The user confirmed removing a model a file check marked damaged. Only then does the store skip its size and
    /// file-ID checks (a truncated file still gets removed); all other store checks stay. Ignored for a model that is not marked damaged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RemoveDamaged { get; init; }
}

/// <summary>Payload of models.verify.</summary>
public sealed class VerifyModelCommand
{
    public string CatalogueId { get; set; } = "";
}

/// <summary>Payload of models.import: the full path of a .gguf file on this PC.</summary>
public sealed class ImportModelCommand
{
    public string Path { get; set; } = "";
}

/// <summary>Payload of store.move: the folder that will hold the host's own "Portal AI Host Models" subfolder.</summary>
public sealed class MoveStoreCommand
{
    public string ParentFolder { get; set; } = "";
}

/// <summary>Answer to store.open_folder: the path only. The window opens it in Explorer itself.</summary>
public sealed class StoreFolderInfo
{
    public string Path { get; init; } = "";
}

/// <summary>One thing "Free up space" could remove.</summary>
public sealed class CleanupItemInfo
{
    /// <summary>Opaque; sent back unchanged in <see cref="CleanupCommand"/>.</summary>
    public string Id { get; init; } = "";

    /// <summary><see cref="CleanupKinds"/> value.</summary>
    public string Kind { get; init; } = CleanupKinds.Partial;

    public string Label { get; init; } = "";
    public string? Detail { get; init; }
    public long Bytes { get; init; }
    public int FileCount { get; init; }
}

/// <summary>Group names of <see cref="CleanupItemInfo.Kind"/>.</summary>
public static class CleanupKinds
{
    /// <summary>An unfinished download nothing is resuming (older than the keep-for-resume age).</summary>
    public const string Partial = "partial";

    /// <summary>Old copies left in the earlier folder after a move, whose new copies are recorded as checked.</summary>
    public const string OldCopies = "old_copies";

    /// <summary>A model a check found damaged.</summary>
    public const string Damaged = "damaged";
}

/// <summary>Answer to store.cleanup_scan.</summary>
public sealed class CleanupScan
{
    public List<CleanupItemInfo> Items { get; init; } = new();
    public long TotalBytes { get; init; }

    /// <summary>Why nothing can be cleaned right now (a move, check or download is running); null otherwise.</summary>
    public string? BusyReason { get; init; }
}

/// <summary>Payload of store.cleanup: the ids from the scan the user ticked.</summary>
public sealed class CleanupCommand
{
    public List<string> ItemIds { get; set; } = new();
}

/// <summary>An item the clean left in place, with the plain-language reason.</summary>
public sealed class CleanupSkipped
{
    public string Label { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>Answer to store.cleanup.</summary>
public sealed class CleanupResult
{
    public long FreedBytes { get; init; }
    public List<string> Removed { get; init; } = new();
    public List<CleanupSkipped> Skipped { get; init; } = new();
}

/// <summary>Operation names of <see cref="StoreProgressEvent"/>.</summary>
public static class StoreOperations
{
    public const string Move = "move";
    public const string Verify = "verify";
    public const string Cleanup = "cleanup";

    /// <summary>"Add your own model": copying a GGUF file into the store. The finished event carries the new model id in CatalogueId.</summary>
    public const string Import = "import";
}

/// <summary>State names of <see cref="StoreProgressEvent"/>.</summary>
public static class StoreStates
{
    public const string Running = "running";

    /// <summary>Move only: the copies verified and the host switched; the old copies are being removed.</summary>
    public const string RemovingOld = "removing_old";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>Verify only: a file does not match the model list. Nothing was deleted.</summary>
    public const string Damaged = "damaged";
}

/// <summary>Progress of a move or a check (event.store_progress).</summary>
public sealed class StoreProgressEvent
{
    /// <summary><see cref="StoreOperations"/> value.</summary>
    public string Operation { get; set; } = StoreOperations.Move;

    /// <summary><see cref="StoreStates"/> value.</summary>
    public string State { get; set; } = StoreStates.Running;

    /// <summary>Model being checked (verify only).</summary>
    public string? CatalogueId { get; init; }

    /// <summary>0 to 1.</summary>
    public double Fraction { get; init; }

    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>Plain-language result or reason.</summary>
    public string? Message { get; init; }

    /// <summary>Move, done: old copies that were left in place because they did not pass the checks.</summary>
    public List<string>? LeftBehind { get; init; }
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

    /// <summary>Set when the new performance limits made the host reload or unload a model.</summary>
    public PerformanceReloadInfo? PerformanceReloaded { get; set; }
}

/// <summary>Which engines the host restarted (<see cref="Reloaded"/>) or stopped (<see cref="Unloaded"/>, no longer fits) after a limit change.</summary>
public sealed class PerformanceReloadInfo
{
    public List<string> Reloaded { get; set; } = new();
    public List<string> Unloaded { get; set; } = new();
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
    /// <summary>
    /// <c>server</c> (default) or <c>client</c> (docs/client-mode-v1.md section 1.1). Owned by the host: it changes only
    /// through client.setmode, and the value sent with settings.set is ignored.
    /// </summary>
    public string Mode { get; set; } = HostModes.Server;

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

    /// <summary>Older memory share. Kept so settings from earlier versions still read; <see cref="MemoryLimitBytes"/> replaces it.</summary>
    public int MaxMemoryPercent { get; set; } = 80;

    /// <summary>Performance preset: quiet, balanced, fast or custom (performance-and-tuning section 2).</summary>
    public string Preset { get; set; } = PerformancePresets.Balanced;

    /// <summary>Most memory the AI may use. Null: derived from <see cref="MaxMemoryPercent"/> (first read saves it).</summary>
    public long? MemoryLimitBytes { get; set; }

    /// <summary>Memory always left free for other programs. Null: 4 GB, or 8 GB on PCs with 32 GB or more.</summary>
    public long? KeepFreeBytes { get; set; }

    /// <summary>auto, gpu or cpu. When auto, the older <see cref="Backend"/> still applies.</summary>
    public string Compute { get; set; } = "auto";

    /// <summary>Most graphics memory the AI may use. Null: all of the card minus 0.5 GB.</summary>
    public long? GpuMemoryLimitBytes { get; set; }

    /// <summary>Processor cores for generation. Null: logical cores minus 2 (minimum 1).</summary>
    public int? CpuThreads { get; set; }

    /// <summary>low (below normal) or normal.</summary>
    public string Priority { get; set; } = PerformancePriorities.Low;

    /// <summary>Most of the processor the AI may use, 20 to 100 (100 is no limit).</summary>
    public int CpuCeilingPercent { get; set; } = 100;

    /// <summary>When the host eases off: any of fullscreen, gpu_busy, battery, app_busy.</summary>
    public List<string> EaseOff { get; set; } = [.. EaseOffReasons.Defaults];

    public bool PreventSleepWhileDownloading { get; set; } = true;

    /// <summary>Send scrubbed crash reports. On unless the user turns it off; the host follows the window's choice.</summary>
    public bool SendCrashReports { get; set; } = true;
    public long? BandwidthCapBytesPerSecond { get; init; }

    /// <summary>
    /// Notification kinds the user turned off (<see cref="NotificationKinds"/>). Null or empty: every kind is on.
    /// Unknown names are ignored, so a kind added later starts on.
    /// </summary>
    public List<string>? MutedNotifications { get; set; }

    /// <summary>Other devices on the home network (remote.enabled, remote.port).</summary>
    public RemoteSettings Remote { get; set; } = new();

    /// <summary>Per model kind: keep loaded and idle timeout.</summary>
    public Dictionary<string, KindSettings> Kinds { get; set; } = new();
}

/// <summary>Kinds of Windows notification the host can raise while its window is closed (<see cref="HostSettings.MutedNotifications"/>).</summary>
public static class NotificationKinds
{
    /// <summary>A model download finished or failed.</summary>
    public const string Downloads = "downloads";

    /// <summary>A new app is asking to use the host.</summary>
    public const string Approvals = "approvals";

    /// <summary>The engine stopped and could not restart, or the health check turned to fail.</summary>
    public const string Problems = "problems";

    public static readonly string[] All = [Downloads, Approvals, Problems];
}

/// <summary>Values of <see cref="HostSettings.Preset"/>.</summary>
public static class PerformancePresets
{
    public const string Quiet = "quiet";
    public const string Balanced = "balanced";
    public const string Fast = "fast";
    public const string Custom = "custom";
}

/// <summary>Values of <see cref="HostSettings.Priority"/>.</summary>
public static class PerformancePriorities
{
    public const string Low = "low";
    public const string Normal = "normal";
}

/// <summary>Reasons the host eases off (performance-and-tuning section 2).</summary>
public static class EaseOffReasons
{
    public const string Fullscreen = "fullscreen";
    public const string GpuBusy = "gpu_busy";
    public const string Battery = "battery";
    public const string AppBusy = "app_busy";

    /// <summary>The flags that are on by default.</summary>
    public static readonly string[] Defaults = [Fullscreen, Battery, AppBusy];

    /// <summary>Every known flag, in the order they are shown.</summary>
    public static readonly string[] All = [Fullscreen, GpuBusy, Battery, AppBusy];

    /// <summary>Keeps only known flags, once each, in the shown order. Null (never saved) gives the defaults.</summary>
    public static List<string> Normalize(IEnumerable<string>? flags)
    {
        if (flags is null)
        {
            return [.. Defaults];
        }

        var wanted = new HashSet<string>(flags.Select(f => f?.Trim().ToLowerInvariant() ?? ""), StringComparer.Ordinal);
        return [.. All.Where(wanted.Contains)];
    }
}

/// <summary>Values fit in <see cref="PerformanceInfo.Fit"/> (performance-and-tuning section 3).</summary>
public static class PerformanceFits
{
    public const string FitsFully = "fits_fully";
    public const string FitsPartial = "fits_partial";
    public const string CpuOnly = "cpu_only";
    public const string NeedsSmallerModel = "needs_smaller_model";
    public const string TooBig = "too_big";
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

    /// <summary>Chosen by the window; the chunk pushes of this run carry it so a window ignores other runs.</summary>
    public string? RunId { get; set; }
}

/// <summary>A piece of the reply (event.tryit_chunk). Pieces of one run arrive in order and none are dropped.</summary>
public sealed class TryItChunkEvent
{
    public string RunId { get; set; } = "";

    /// <summary>1, 2, 3 ... per run, so the window can tell when it has every piece (the final answer can overtake the last pushes).</summary>
    public int Seq { get; set; }

    /// <summary>Answer text, or null.</summary>
    public string? Content { get; set; }

    /// <summary>Thinking text, or null.</summary>
    public string? Reasoning { get; set; }
}

/// <summary>Result of a streaming "Try it" run (answer to tryit.stream). Counts and timing only, never text.</summary>
public sealed class TryItDone
{
    public string RunId { get; set; } = "";

    /// <summary>True when the run was stopped before the model finished; the numbers are then for what ran.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Why the model stopped (stop, length), or null.</summary>
    public string? FinishReason { get; set; }

    /// <summary>Milliseconds from the start of generation (after waiting in the queue and loading) to the first piece, or null if none came.</summary>
    public long? FirstTokenMs { get; set; }

    /// <summary>Milliseconds from the start of generation to the end.</summary>
    public long TotalMs { get; set; }

    /// <summary>Milliseconds spent waiting in the queue and loading the model before generation began.</summary>
    public long WaitMs { get; set; }

    public long Tokens { get; set; }

    /// <summary>How many chunk pushes this run sent; the window waits for that many before it shows the numbers.</summary>
    public int Chunks { get; set; }

    /// <summary>Tokens per second after the first piece, or null when too short to measure.</summary>
    public double? TokensPerSecond { get; set; }
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
