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

    Task<HostSnapshot> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default);
    Task ApproveAsync(string requestId, CancellationToken ct = default);
    Task DenyAsync(string requestId, CancellationToken ct = default);
    Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default);
    Task RevokeClientAsync(string tokenId, CancellationToken ct = default);
    Task<ModelCatalogView> ListModelsAsync(CancellationToken ct = default);
    Task StartDownloadAsync(string catalogueId, CancellationToken ct = default);
    Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Continues a paused or failed download from its partial file.</summary>
    Task ResumeDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Stops a download and has the host remove the partial files it made.</summary>
    Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Has the host remove a model (its own files only; a file added in place is only forgotten).</summary>
    Task RemoveModelAsync(string catalogueId, CancellationToken ct = default);
    /// <summary>Has the host remove a model left in an earlier models folder, with the same store rules.</summary>
    Task RemoveOldModelAsync(string catalogueId, string storeFolder, CancellationToken ct = default);
    Task ActivateModelAsync(string catalogueId, CancellationToken ct = default);
    Task<HostSettings> GetSettingsAsync(CancellationToken ct = default);
    /// <summary>Saves settings. A new models folder never moves or deletes the models already downloaded.</summary>
    Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default);

    /// <summary>Streams the reply text of a short chat request in pieces.</summary>
    IAsyncEnumerable<string> TryItAsync(string prompt, CancellationToken ct = default);
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
}
