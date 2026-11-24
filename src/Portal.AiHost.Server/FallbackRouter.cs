using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Server;

/// <summary>
/// Client mode's backend behind the model routes (docs/client-mode-v1.md section 1.3). Every new request goes to the
/// paired PC. When the PC cannot be reached and <c>client_fallback</c> is <c>local</c>, the request runs on this
/// laptop's own engine instead (the engine loads on demand through its normal path, so memory checks and
/// <c>insufficient_memory</c> answers apply and are passed on). With <c>none</c>, apps get
/// <c>503 remote_unreachable</c> unchanged. The choice is made once per new request: a request already running keeps
/// the backend it started on, and as soon as the link reports reachable again new requests return to the PC. Nothing
/// here loads, unloads, deletes or writes a file; the local engine's own idle rule unloads the model.
/// </summary>
public sealed class FallbackRouter : IInferenceBackend
{
    private readonly RemoteHostLink _link;
    private readonly IInferenceBackend _local;
    private readonly RemoteBackend _remote;
    private readonly Func<string?> _fallback;
    private volatile bool _usingLocal;

    /// <param name="link">The link to the paired PC.</param>
    /// <param name="local">This laptop's own engine.</param>
    /// <param name="fallback">Reads the current <c>client_fallback</c> setting (null or <c>none</c> = no fallback).</param>
    /// <param name="options">Options of the forwarding backend; its own unreachable hook is replaced.</param>
    /// <param name="time">Clock for the forwarding backend.</param>
    public FallbackRouter(RemoteHostLink link, IInferenceBackend local, Func<string?> fallback,
        RemoteBackendOptions? options = null, TimeProvider? time = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        var o = options ?? new RemoteBackendOptions();
        _remote = new RemoteBackend(link, new RemoteBackendOptions
        {
            CapabilitiesCache = o.CapabilitiesCache,
            QueuedCommentInterval = o.QueuedCommentInterval,
            DeadlineMargin = o.DeadlineMargin,
            MaxErrorBytes = o.MaxErrorBytes,
            AppName = o.AppName,
            OnUnreachable = OnUnreachableAsync,
        }, time);
    }

    /// <summary>True while the laptop's own model answered the latest new request (the Home chip: "Using this laptop's model").</summary>
    public bool UsingLocal => _usingLocal;

    /// <summary>Raised when <see cref="UsingLocal"/> changes.</summary>
    public event Action? UsingLocalChanged;

    /// <summary>The routes the laptop engine can answer in place of the PC. Ease-off belongs to the PC and is never faked.</summary>
    public static bool CanRunLocally(InferenceRoute route) =>
        route is not (InferenceRoute.EaseOffSet or InferenceRoute.EaseOffClear);

    private bool LocalWanted(InferenceRoute route) =>
        CanRunLocally(route) && string.Equals(_fallback(), ClientFallbackModes.Local, StringComparison.Ordinal);

    private bool LinkDown() =>
        _link.Status.State is RemoteLinkState.Unreachable or RemoteLinkState.Revoked or RemoteLinkState.FingerprintChanged;

    public async Task HandleAsync(InferenceRoute route, ApiContext context)
    {
        if (LocalWanted(route) && LinkDown())
        {
            // The PC is known to be away: do not make the app wait for another connect attempt.
            SetUsingLocal(true);
            await _local.HandleAsync(route, context).ConfigureAwait(false);
            return;
        }

        if (!LinkDown())
        {
            SetUsingLocal(false);
        }

        await _remote.HandleAsync(route, context).ConfigureAwait(false);
    }

    /// <summary>The PC turned out to be unreachable while this request was being sent, before any byte reached the app.</summary>
    private async Task<bool> OnUnreachableAsync(InferenceRoute route, ApiContext context, ApiException error)
    {
        if (!LocalWanted(route))
        {
            return false;
        }

        SetUsingLocal(true);
        await _local.HandleAsync(route, context).ConfigureAwait(false);
        return true;
    }

    /// <summary>Called when the link reports a new state: back to the PC for new requests once it is reachable.</summary>
    public void LinkChanged()
    {
        if (_link.Status.State == RemoteLinkState.Connected)
        {
            SetUsingLocal(false);
        }
    }

    private void SetUsingLocal(bool value)
    {
        if (_usingLocal != value)
        {
            _usingLocal = value;
            UsingLocalChanged?.Invoke();
        }
    }
}
