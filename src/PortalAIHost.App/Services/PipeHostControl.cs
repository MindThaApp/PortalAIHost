using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace PortalAIHost.App.Services;

/// <summary>
/// The real host connection over the per-user control pipe. Placeholder until the pipe client is wired in:
/// every call reports "host not running", so the window shows its start-the-host screen.
/// </summary>
public sealed class PipeHostControl : IHostControl
{
    // TODO(integration): create a ControlPipeClient (Platform.Windows/Pipes) for the per-user control pipe.
    // TODO(integration): map ControlTypes.GetStatus to HostSnapshot (Connected = pipe answered), and listen for
    //   EventStatusChanged, EventApprovalPending, EventDownloadProgress and EventEngineState to raise the events below.
    // TODO(integration): ListApprovals/Approve/Deny use approvals.*; ListClients/RevokeClient use clients.*;
    //   ListModels/StartDownload/PauseDownload/CancelDownload/ActivateModel use models.* and downloads.*;
    //   GetSettings/SetSettings use settings.*; TryItAsync uses tryit.chat with a streamed ChatCompletionRequest.
    // TODO(integration): the host does not yet report safe mode, existing-copy matches, free disk space or a newer
    //   recommended model; fill HostSnapshot.SafeMode and ModelCatalogView once those fields exist.
#pragma warning disable CS0067 // events are raised once the pipe client is wired in
    public event Action<HostSnapshot>? StatusChanged;
    public event Action<PendingApproval>? ApprovalPending;
    public event Action<DownloadProgressEvent>? DownloadProgress;
    public event Action<EngineStateEvent>? EngineStateChanged;
#pragma warning restore CS0067

    public Task<HostSnapshot> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(HostSnapshot.NoHost);
    public Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default) => throw new HostUnavailableException();
    public Task ApproveAsync(string requestId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task DenyAsync(string requestId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default) => throw new HostUnavailableException();
    public Task RevokeClientAsync(string tokenId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task<ModelCatalogView> ListModelsAsync(CancellationToken ct = default) => throw new HostUnavailableException();
    public Task StartDownloadAsync(string catalogueId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default) => throw new HostUnavailableException();
    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default) => throw new HostUnavailableException();
    public Task SetSettingsAsync(HostSettings settings, CancellationToken ct = default) => throw new HostUnavailableException();
    public IAsyncEnumerable<string> TryItAsync(string prompt, CancellationToken ct = default) => throw new HostUnavailableException();
}
