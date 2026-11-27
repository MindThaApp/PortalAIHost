using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace PortalAIHost.App.Services;

/// <summary>What the window knows about the host right now. Connected is false when the host is not running.</summary>
/// <param name="Connected">True when the control pipe answered.</param>
/// <param name="Status">Latest host status, or null when not connected.</param>
/// <param name="SafeMode">True when the host started in safe mode after repeated unclean exits.</param>
public sealed record HostSnapshot(bool Connected, HostStatus? Status, bool SafeMode)
{
    public static HostSnapshot NoHost { get; } = new(false, null, false);

    /// <summary>Catalogue id of a recommended upgrade over the active chat model, or null.</summary>
    public string? UpgradeModelId { get; init; }

    /// <summary>Unclean exits in a row that led to safe mode.</summary>
    public int UncleanExits { get; init; }

    /// <summary>Why the host could not open its models folder (left untouched), or null.</summary>
    public string? StoreProblem { get; init; }
}

/// <summary>The model list plus the storage facts the Models page shows.</summary>
/// <param name="Models">Catalogue entries with install state.</param>
/// <param name="ExistingCopyIds">Models the host found already on this PC and reuses instead of downloading again.</param>
/// <param name="StorageFolder">Folder the host keeps models in.</param>
/// <param name="FreeBytes">Free space on that drive.</param>
/// <param name="UpgradeModelId">Catalogue id of a newer recommended model, if any.</param>
public sealed record ModelCatalogView(
    IReadOnlyList<CatalogueModelSummary> Models,
    IReadOnlySet<string> ExistingCopyIds,
    string StorageFolder,
    long FreeBytes,
    string? UpgradeModelId)
{
    /// <summary>Disk space the models in the current folder use.</summary>
    public long ModelsBytes { get; init; }

    /// <summary>False when the host has no trusted model list: only installed models are shown.</summary>
    public bool CatalogueAvailable { get; init; } = true;
}

/// <summary>Raised by a host control when the host cannot be reached.</summary>
public sealed class HostUnavailableException : Exception
{
    public HostUnavailableException() : base("The host is not running.") { }
}

/// <summary>Everything the window asks of the host. Admin requests only: the window never touches model files itself.</summary>
public interface IHostControl
{
    event Action<HostSnapshot>? StatusChanged;
    event Action<PendingApproval>? ApprovalPending;
    event Action<DownloadProgressEvent>? DownloadProgress;
    event Action<EngineStateEvent>? EngineStateChanged;

    /// <summary>Progress of a models-folder move or a file check.</summary>
    event Action<StoreProgressEvent>? StoreProgress;

    /// <summary>The host says the tray icon was clicked: show the small status pop-up.</summary>
    event Action<TrayPopupEvent>? TrayPopup;

    /// <summary>Paired devices, the pairing code or the network listener changed on the host: read <see cref="GetRemoteAsync"/> again.</summary>
    event Action? RemoteChanged;

    Task<HostSnapshot> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Today's per-app counts and which apps are generating now (counts and times only).</summary>
    Task<IReadOnlyList<AppActivity>> GetActivityAsync(CancellationToken ct = default);

    /// <summary>Loads the active chat model now.</summary>
    Task LoadEngineAsync(CancellationToken ct = default);

    /// <summary>Unloads the chat model now (stops the engine; model files are never touched).</summary>
    Task UnloadEngineAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default);
    Task ApproveAsync(string requestId, CancellationToken ct = default);
    Task DenyAsync(string requestId, CancellationToken ct = default);
    Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default);
    Task RevokeClientAsync(string tokenId, CancellationToken ct = default);

    /// <summary>Pauses or resumes an approved app and/or sets its queue priority ("high", "normal", "low"). Null keeps the value. Approval is kept.</summary>
    Task SetAppControlAsync(string appId, bool? paused, string? priority, CancellationToken ct = default);
    Task<ModelCatalogView> ListModelsAsync(CancellationToken ct = default);
    Task StartDownloadAsync(string catalogueId, CancellationToken ct = default);
    Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Continues a paused or failed download from its partial file.</summary>
    Task ResumeDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Stops a download and has the host remove the partial files it made.</summary>
    Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Has the host remove a model (its own files only; a file added in place is only forgotten).</summary>
    Task RemoveModelAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>
    /// Has the host remove a model, including the one in use: the host unloads it first and, with
    /// <paramref name="switchToId"/>, switches to that installed model; without it the host has no model afterwards.
    /// </summary>
    Task RemoveModelAsync(string catalogueId, string? switchToId, CancellationToken ct = default);
    /// <summary>
    /// After the user confirmed: removes a model a check found damaged, also when its file changed size. Only that
    /// model's own files go, through the same store rules; nothing else is skipped.
    /// </summary>
    Task RemoveDamagedModelAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Has the host re-hash an installed model against the model list. Read-only; progress arrives as <see cref="StoreProgress"/>.</summary>
    Task VerifyModelAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>"Add your own model": has the host copy a .gguf file into its models folder. Progress and the result arrive as <see cref="StoreProgress"/> (operation import). The original is only read.</summary>
    Task ImportModelAsync(string path, CancellationToken ct = default);
    /// <summary>The models folder, to open in Explorer. The host changes nothing.</summary>
    Task<string> GetStoreFolderAsync(CancellationToken ct = default);
    /// <summary>Moves the models to a folder (the host adds its own "Portal AI Host Models" subfolder); progress arrives as <see cref="StoreProgress"/>.</summary>
    Task MoveStoreAsync(string parentFolder, CancellationToken ct = default);
    /// <summary>Cancels a running move or check.</summary>
    Task CancelStoreOperationAsync(CancellationToken ct = default);
    /// <summary>Reads only: what the host itself left behind and could remove (old unfinished downloads, old copies after a move, models a check found damaged).</summary>
    Task<CleanupScan> ScanCleanupAsync(CancellationToken ct = default);
    /// <summary>After the user confirmed: removes the scanned items with these ids. The host checks each item again right before deleting it.</summary>
    Task<CleanupResult> CleanupAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct = default);
    /// <summary>Has the host remove a model left in an earlier models folder, with the same store rules.</summary>
    Task RemoveOldModelAsync(string catalogueId, string storeFolder, CancellationToken ct = default);
    Task ActivateModelAsync(string catalogueId, CancellationToken ct = default);
    Task<HostSettings> GetSettingsAsync(CancellationToken ct = default);
    /// <summary>Saves settings. A new models folder never moves or deletes the models already downloaded.</summary>
    Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default);

    /// <summary>
    /// The performance limits in force, the hardware ranges and the fit preview. With <paramref name="candidate"/> the
    /// preview is for those settings; nothing is saved.
    /// </summary>
    Task<PerformanceView> GetPerformanceAsync(HostSettings? candidate = null, CancellationToken ct = default);

    /// <summary>The health checks: whether the host works, and a fix for what does not. Read-only.</summary>
    Task<HealthReport> GetHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams the reply to the conversation so far (oldest turn first, last turn from the user) in pieces. Thinking text and
    /// answer text come as separate pieces; the last piece has no text and carries the timing.
    /// </summary>
    IAsyncEnumerable<TryItPiece> TryItAsync(IReadOnlyList<TryItTurn> turns, CancellationToken ct = default);

    /// <summary>Has the host stop the running "Try it" request so it stops using the processor.</summary>
    Task CancelTryItAsync();

    /// <summary>"Other devices": whether it is on, the paired devices and the open pairing code.</summary>
    Task<RemoteStatus> GetRemoteAsync(CancellationToken ct = default);

    /// <summary>Opens a pairing code (valid 5 minutes). Needs "other devices" turned on.</summary>
    Task<PairingOfferInfo> StartPairingAsync(CancellationToken ct = default);

    Task CancelPairingAsync(CancellationToken ct = default);

    /// <summary>Makes a new relay access key. Devices paired with the old one must pair again to use the relay.</summary>
    Task RotateRelayKeyAsync(CancellationToken ct = default);

    /// <summary>Removes a paired device; its access stops at once.</summary>
    Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Turns Model control on or off for a paired device (takes effect at once).</summary>
    Task SetModelControlAsync(string deviceId, bool enabled, CancellationToken ct = default);

    /// <summary>Unloads the model a device loaded for this kind (memory only) and gives the PC's own choice back.</summary>
    Task TakeBackAsync(string kind, CancellationToken ct = default);

    /// <summary>Pauses or resumes a device (<paramref name="app"/> null) or one of its apps.</summary>
    Task PauseDeviceAsync(string deviceId, string? app, bool paused, CancellationToken ct = default);

    /// <summary>Replaces priority, limits, quiet hours and allowed models of a device.</summary>
    Task SetDeviceSettingsAsync(SetDeviceSettingsCommand settings, CancellationToken ct = default);

    /// <summary>Renames a paired device.</summary>
    Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default);

    /// <summary>The host mode and, in client mode, the link to the PC, whether this laptop has Model control and what it loaded there.</summary>
    Task<ClientModeStatus> GetClientStatusAsync(CancellationToken ct = default);

    /// <summary>The PC's installed models (client mode with Model control). A refusal comes back as an answer with a plain message.</summary>
    Task<RemoteModelsAnswer> ListRemoteModelsAsync(CancellationToken ct = default);

    /// <summary>Loads a model on the PC. <paramref name="placement"/> is auto, gpu or cpu (null: auto).</summary>
    Task<RemoteModelsAnswer> LoadRemoteModelAsync(string modelId, string? placement, CancellationToken ct = default);

    /// <summary>Unloads a model this laptop loaded on the PC. Never touches a model file.</summary>
    Task<RemoteModelsAnswer> UnloadRemoteModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>The PC model each local app asks for.</summary>
    Task<AppModelList> GetAppModelsAsync(CancellationToken ct = default);

    /// <summary>Chooses the PC model an app asks for; null clears the choice (the PC's choice).</summary>
    Task<AppModelList> SetAppModelAsync(string appId, string? modelId, CancellationToken ct = default);

}

/// <summary>One piece of a "Try it" reply: the model's thinking text or its answer.</summary>
public sealed record TryItPiece(string Text, bool IsThinking = false, TryItStats? Stats = null);

/// <summary>One turn of the "Try it" conversation (kept in memory only).</summary>
public sealed record TryItTurn(bool IsUser, string Text);

/// <summary>Timing of one finished "Try it" answer. Counts and times only.</summary>
public sealed record TryItStats(long? FirstTokenMs, long TotalMs, long Tokens, double? TokensPerSecond, bool Cancelled);

/// <summary>Lets the user pick a model file. Returns the full path, or null when they cancel.</summary>
public interface IModelFilePicker
{
    Task<string?> PickModelFileAsync();
}

/// <summary>Shows a folder in Explorer. Opening only: nothing is changed.</summary>
public interface IFolderOpener
{
    void Open(string path);
}

/// <summary>Starts the host process when the control pipe is absent.</summary>
public interface IHostLauncher
{
    /// <summary>Returns true when a start was requested. The caller retries the connection afterwards.</summary>
    Task<bool> StartHostAsync(CancellationToken ct = default);

    /// <summary>Resource key of a plain-language reason for the last failed start, or null.</summary>
    string? FailureKey { get; }
}

/// <summary>Start with Windows (a Windows StartupTask). States are <see cref="StartupStates"/> values.</summary>
public interface IStartupControl
{
    Task<string> GetStateAsync();

    /// <summary>Asks Windows to turn it on or off and returns the resulting state. Windows may refuse (disabled by the user in Task Manager).</summary>
    Task<string> SetEnabledAsync(bool enabled);
}

/// <summary>Start with Windows for builds that are not packaged: always unavailable.</summary>
public sealed class UnavailableStartupControl : IStartupControl
{
    public Task<string> GetStateAsync() => Task.FromResult(StartupStates.Unavailable);
    public Task<string> SetEnabledAsync(bool enabled) => Task.FromResult(StartupStates.Unavailable);
}

/// <summary>Runs work on the UI thread. View models never touch window types directly.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Looks up user-facing text by resource key.</summary>
public interface IStrings
{
    string Get(string key);
    string Format(string key, params object[] args);
}

/// <summary>Asks the user a yes/no question before anything risky.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string body, string confirmText, string cancelText);
}

/// <summary>Small values the window remembers between runs (for example that first-run setup is done).</summary>
public interface IAppState
{
    bool FirstRunDone { get; }
    void MarkFirstRunDone();

    /// <summary>How the window looks (theme, material, corners, density, text size, motion).</summary>
    AppearancePrefs Appearance { get; }
    void SaveAppearance(AppearancePrefs prefs);

    /// <summary>Version whose What's new was last shown, or null when none was.</summary>
    string? LastSeenVersion { get; }
    void MarkVersionSeen(string version);

    /// <summary>Send scrubbed crash reports. On unless the person turned it off.</summary>
    bool SendCrashReports { get; }
    void SaveSendCrashReports(bool on);
}
