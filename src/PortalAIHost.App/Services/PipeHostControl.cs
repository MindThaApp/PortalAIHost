using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace PortalAIHost.App.Services;

/// <summary>
/// The real host connection over the per-user control pipe. Connects lazily: every call (and the shell's status
/// poll) tries to connect when no connection is open, so a host that starts later is picked up on the next poll.
/// When the connection ends the window gets a "host not running" snapshot straight away.
/// </summary>
public sealed class PipeHostControl : IHostControl, IAsyncDisposable
{
    private readonly IControlChannelFactory _factory;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private IControlChannel? _channel;
    private bool _disposed;

    public PipeHostControl(IControlChannelFactory factory) => _factory = factory;

    public event Action<HostSnapshot>? StatusChanged;
    public event Action<PendingApproval>? ApprovalPending;
    public event Action<DownloadProgressEvent>? DownloadProgress;
    public event Action<EngineStateEvent>? EngineStateChanged;
    public event Action<TrayPopupEvent>? TrayPopup;
    public event Action<StoreProgressEvent>? StoreProgress;
    public event Action? RemoteChanged;

    /// <summary>True while a connection is open.</summary>
    public bool IsConnected => Volatile.Read(ref _channel) is not null;

    public async Task<HostSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return ToSnapshot(await RequestAsync(ControlTypes.GetStatus, AiHostJsonContext.Default.ControlStatus, ct)
                .ConfigureAwait(false));
        }
        catch (HostUnavailableException)
        {
            return HostSnapshot.NoHost;
        }
    }

    public async Task<IReadOnlyList<AppActivity>> GetActivityAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.GetActivity, AiHostJsonContext.Default.ActivityReport, ct).ConfigureAwait(false)).Apps;

    public Task LoadEngineAsync(CancellationToken ct = default) =>
        SendAsync(ControlTypes.LoadEngine, new EngineCommand { Kind = ModelKinds.Chat }, AiHostJsonContext.Default.EngineCommand, ct);

    public Task UnloadEngineAsync(CancellationToken ct = default) =>
        SendAsync(ControlTypes.UnloadEngine, new EngineCommand { Kind = ModelKinds.Chat }, AiHostJsonContext.Default.EngineCommand, ct);

    public async Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.ListApprovals, AiHostJsonContext.Default.ApprovalList, ct).ConfigureAwait(false)).Pending;

    public Task ApproveAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Approve, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public Task DenyAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Deny, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public async Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.ListClients, AiHostJsonContext.Default.ClientList, ct).ConfigureAwait(false)).Clients;

    public Task SetAppControlAsync(string appId, bool? paused, string? priority, CancellationToken ct = default) =>
        SendAsync(ControlTypes.SetAppControl, new AppControlCommand { AppId = appId, Paused = paused, Priority = priority },
            AiHostJsonContext.Default.AppControlCommand, ct);

    public Task RevokeClientAsync(string tokenId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RevokeClient, new ClientRevoke { TokenId = tokenId }, AiHostJsonContext.Default.ClientRevoke, ct);

    public async Task<ModelCatalogView> ListModelsAsync(CancellationToken ct = default)
    {
        var listing = await RequestAsync(ControlTypes.ListModels, AiHostJsonContext.Default.ModelListing, ct).ConfigureAwait(false);
        return ToView(listing);
    }

    internal static ModelCatalogView ToView(ModelListing listing) => new(
        listing.Models,
        listing.Models.Where(m => m.ExistingCopy).Select(m => m.Id).ToHashSet(StringComparer.Ordinal),
        listing.ModelsFolder ?? "",
        listing.FreeBytes,
        listing.UpgradeModelId)
    {
        ModelsBytes = listing.ModelsBytes,
        CatalogueAvailable = listing.CatalogueAvailable,
    };

    public Task StartDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.StartDownload, catalogueId, ct);

    public Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.PauseDownload, catalogueId, ct);

    public Task ResumeDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.ResumeDownload, catalogueId, ct);

    public Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.CancelDownload, catalogueId, ct);

    public Task RemoveModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task RemoveModelAsync(string catalogueId, string? switchToId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId, SwitchToId = switchToId },
            AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task RemoveDamagedModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId, RemoveDamaged = true },
            AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task VerifyModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.VerifyModel, new VerifyModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.VerifyModelCommand, ct);

    public Task ImportModelAsync(string path, CancellationToken ct = default) =>
        SendAsync(ControlTypes.ImportModel, new ImportModelCommand { Path = path }, AiHostJsonContext.Default.ImportModelCommand, ct);

    public async Task<string> GetStoreFolderAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.OpenStoreFolder, AiHostJsonContext.Default.StoreFolderInfo, ct).ConfigureAwait(false)).Path;

    public Task MoveStoreAsync(string parentFolder, CancellationToken ct = default) =>
        SendAsync(ControlTypes.MoveStore, new MoveStoreCommand { ParentFolder = parentFolder }, AiHostJsonContext.Default.MoveStoreCommand, ct);

    public Task<CleanupScan> ScanCleanupAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.ScanCleanup, AiHostJsonContext.Default.CleanupScan, ct);

    public async Task<CleanupResult> CleanupAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct = default) =>
        Read(await CallAsync(ControlTypes.RunCleanup,
            JsonSerializer.SerializeToElement(new CleanupCommand { ItemIds = [.. itemIds] }, AiHostJsonContext.Default.CleanupCommand), ct).ConfigureAwait(false),
            AiHostJsonContext.Default.CleanupResult);

    public Task CancelStoreOperationAsync(CancellationToken ct = default) => CallAsync(ControlTypes.CancelStoreOperation, null, ct);

    public Task RemoveOldModelAsync(string catalogueId, string storeFolder, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId, StoreFolder = storeFolder },
            AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.ActivateModel, new ActivateModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.ActivateModelCommand, ct);

    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.GetSettings, AiHostJsonContext.Default.HostSettings, ct);

    public async Task<PerformanceView> GetPerformanceAsync(HostSettings? candidate = null, CancellationToken ct = default)
    {
        var answer = await CallAsync(ControlTypes.GetPerformance,
            candidate is null ? null : JsonSerializer.SerializeToElement(candidate, AiHostJsonContext.Default.HostSettings), ct)
            .ConfigureAwait(false);
        return answer.Payload is { ValueKind: JsonValueKind.Object } p
            ? p.Deserialize(AiHostJsonContext.Default.PerformanceView) ?? new PerformanceView()
            : new PerformanceView();
    }

    public Task<HealthReport> GetHealthAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.GetHealth, AiHostJsonContext.Default.HealthReport, ct);

    public async Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default)
    {
        var answer = await CallAsync(ControlTypes.SetSettings, JsonSerializer.SerializeToElement(settings, AiHostJsonContext.Default.HostSettings), ct)
            .ConfigureAwait(false);
        return answer.Payload is { ValueKind: JsonValueKind.Object } p
            ? p.Deserialize(AiHostJsonContext.Default.SettingsSaved) ?? new SettingsSaved()
            : new SettingsSaved();
    }

    private readonly object _tryItGate = new();
    private string? _tryItRun;
    private Action<TryItChunkEvent>? _tryItSink;

    public async IAsyncEnumerable<TryItPiece> TryItAsync(IReadOnlyList<TryItTurn> turns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var request = new TryItRequest
        {
            RunId = runId,
            Request = new ChatCompletionRequest
            {
                Messages = turns.Select(t => new ChatMessage { Role = t.IsUser ? ChatRoles.User : ChatRoles.Assistant, Content = t.Text }).ToList(),
            },
        };
        var pieces = System.Threading.Channels.Channel.CreateUnbounded<TryItPiece>(new() { SingleReader = true });
        var received = 0;
        void Sink(TryItChunkEvent c)
        {
            if (c.Reasoning is { Length: > 0 } r) pieces.Writer.TryWrite(new TryItPiece(r, true));
            if (c.Content is { Length: > 0 } t) pieces.Writer.TryWrite(new TryItPiece(t));
            Interlocked.Increment(ref received);
        }

        lock (_tryItGate)
        {
            _tryItRun = runId;
            _tryItSink = Sink;
        }

        // The exchange is not tied to ct: Stop asks the host to cancel, and the host answers with what ran.
        var call = CallAsync(ControlTypes.TryItStream, JsonSerializer.SerializeToElement(request, AiHostJsonContext.Default.TryItRequest),
            CancellationToken.None);
        _ = call.ContinueWith(async t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                TryItDone done;
                try
                {
                    done = Read(t.Result, AiHostJsonContext.Default.TryItDone);
                }
                catch (Exception ex)
                {
                    pieces.Writer.TryComplete(ex);
                    return;
                }

                // The answer can overtake the last chunk pushes; give them a moment to arrive.
                for (var i = 0; i < 600 && Volatile.Read(ref received) < done.Chunks; i++)
                    await Task.Delay(5).ConfigureAwait(false);
                pieces.Writer.TryWrite(new TryItPiece("", false,
                    new TryItStats(done.FirstTokenMs, done.TotalMs, done.Tokens, done.TokensPerSecond, done.Cancelled)));
                pieces.Writer.TryComplete();
            }
            else
            {
                pieces.Writer.TryComplete(t.Exception?.GetBaseException());
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        try
        {
            while (await pieces.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (pieces.Reader.TryRead(out var piece))
                {
                    yield return piece;
                }
            }
        }
        finally
        {
            lock (_tryItGate)
            {
                if (_tryItRun == runId)
                {
                    _tryItRun = null;
                    _tryItSink = null;
                }
            }
        }
    }

    public async Task CancelTryItAsync()
    {
        try
        {
            await CallAsync(ControlTypes.TryItCancel, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HostUnavailableException or IOException or HostRequestException)
        {
        }
    }

    public Task<RemoteStatus> GetRemoteAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.RemoteGet, AiHostJsonContext.Default.RemoteStatus, ct);

    public Task<PairingOfferInfo> StartPairingAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.RemotePairStart, AiHostJsonContext.Default.PairingOfferInfo, ct);

    public async Task CancelPairingAsync(CancellationToken ct = default) =>
        await CallAsync(ControlTypes.RemotePairCancel, null, ct).ConfigureAwait(false);

    public Task RemoveDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteRemoveDevice, new RemoveDeviceCommand { DeviceId = deviceId },
            AiHostJsonContext.Default.RemoveDeviceCommand, ct);

    public Task SetModelControlAsync(string deviceId, bool enabled, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteSetModelControl, new SetModelControlCommand { DeviceId = deviceId, Enabled = enabled },
            AiHostJsonContext.Default.SetModelControlCommand, ct);

    public Task TakeBackAsync(string kind, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteTakeBack, new TakeBackCommand { Kind = kind },
            AiHostJsonContext.Default.TakeBackCommand, ct);

    public Task PauseDeviceAsync(string deviceId, string? app, bool paused, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteDevicePause, new PauseDeviceCommand { DeviceId = deviceId, App = app, Paused = paused },
            AiHostJsonContext.Default.PauseDeviceCommand, ct);

    public Task SetDeviceSettingsAsync(SetDeviceSettingsCommand settings, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteDeviceSettings, settings, AiHostJsonContext.Default.SetDeviceSettingsCommand, ct);

    public Task RenameDeviceAsync(string deviceId, string name, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoteDeviceRename, new RenameDeviceCommand { DeviceId = deviceId, Name = name },
            AiHostJsonContext.Default.RenameDeviceCommand, ct);

    private Task Download(string type, string id, CancellationToken ct) =>
        SendAsync(type, new DownloadCommand { CatalogueId = id }, AiHostJsonContext.Default.DownloadCommand, ct);

    internal static HostSnapshot ToSnapshot(ControlStatus s) => new(true, s.Status, s.SafeMode)
    {
        UpgradeModelId = s.UpgradeModelId,
        UncleanExits = s.UncleanExits,
        StoreProblem = s.StoreProblem,
    };

    // ---- plumbing -------------------------------------------------------------------------------

    private async Task SendAsync<T>(string type, T payload, JsonTypeInfo<T> info, CancellationToken ct) =>
        await CallAsync(type, JsonSerializer.SerializeToElement(payload, info), ct).ConfigureAwait(false);

    private async Task<T> RequestAsync<T>(string type, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class =>
        Read(await CallAsync(type, null, ct).ConfigureAwait(false), info);

    private static T Read<T>(ControlEnvelope answer, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return answer.Payload is { ValueKind: JsonValueKind.Object } p && p.Deserialize(info) is { } value
                ? value
                : throw new HostRequestException(ErrorCodes.InternalError, "The host sent an empty answer.");
        }
        catch (JsonException)
        {
            throw new HostRequestException(ErrorCodes.InternalError, "The host sent an answer this window cannot read.");
        }
    }

    private async Task<ControlEnvelope> CallAsync(string type, JsonElement? payload, CancellationToken ct)
    {
        var channel = await GetChannelAsync(ct).ConfigureAwait(false) ?? throw new HostUnavailableException();
        try
        {
            return await channel.ExchangeAsync(type, payload, ct).ConfigureAwait(false);
        }
        catch (HostUnavailableException)
        {
            Drop(channel);
            throw;
        }
    }

    private async Task<IControlChannel?> GetChannelAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _channel) is { } open)
        {
            return open;
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return null;
            }

            if (_channel is null && await _factory.TryConnectAsync(ct).ConfigureAwait(false) is { } fresh)
            {
                fresh.EventReceived += OnEvent;
                Volatile.Write(ref _channel, fresh);
                // After the channel is stored, so a connection that already ended is dropped at once (Drop matches it).
                fresh.Disconnected += () => Drop(fresh);
            }

            return _channel;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void Drop(IControlChannel channel)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _channel, null, channel), channel))
        {
            return;
        }

        channel.EventReceived -= OnEvent;
        _ = Task.Run(async () =>
        {
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });
        StatusChanged?.Invoke(HostSnapshot.NoHost);
    }

    private void OnEvent(ControlEnvelope e)
    {
        if (e.Type == ControlTypes.EventRemoteChanged)
        {
            RemoteChanged?.Invoke();
            return;
        }

        if (e.Payload is not { ValueKind: JsonValueKind.Object } p)
        {
            return;
        }

        try
        {
            switch (e.Type)
            {
                case ControlTypes.EventStatusChanged when p.Deserialize(AiHostJsonContext.Default.ControlStatus) is { } s:
                    StatusChanged?.Invoke(ToSnapshot(s));
                    break;
                case ControlTypes.EventApprovalPending when p.Deserialize(AiHostJsonContext.Default.PendingApproval) is { } a:
                    ApprovalPending?.Invoke(a);
                    break;
                case ControlTypes.EventDownloadProgress when p.Deserialize(AiHostJsonContext.Default.DownloadProgressEvent) is { } d:
                    DownloadProgress?.Invoke(d);
                    break;
                case ControlTypes.EventStoreProgress when p.Deserialize(AiHostJsonContext.Default.StoreProgressEvent) is { } sp:
                    StoreProgress?.Invoke(sp);
                    break;
                case ControlTypes.EventTrayPopup when p.Deserialize(AiHostJsonContext.Default.TrayPopupEvent) is { } tp:
                    TrayPopup?.Invoke(tp);
                    break;
                case ControlTypes.EventTryItChunk when p.Deserialize(AiHostJsonContext.Default.TryItChunkEvent) is { } chunk:
                    Action<TryItChunkEvent>? sink;
                    lock (_tryItGate) sink = chunk.RunId == _tryItRun ? _tryItSink : null;
                    sink?.Invoke(chunk);
                    break;
                case ControlTypes.EventEngineState when p.Deserialize(AiHostJsonContext.Default.EngineStateEvent) is { } en:
                    EngineStateChanged?.Invoke(en);
                    break;
            }
        }
        catch (JsonException)
        {
            // A newer host may send shapes this window does not know; ignore them.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        IControlChannel? channel;
        try
        {
            _disposed = true;
            channel = Interlocked.Exchange(ref _channel, null);
        }
        finally
        {
            _connectGate.Release();
        }

        if (channel is not null)
        {
            channel.EventReceived -= OnEvent;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
