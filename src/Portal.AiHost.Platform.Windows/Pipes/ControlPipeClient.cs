using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Platform.Windows.Pipes;

/// <summary>
/// Window side of the admin pipe. Connect, then call one method per control message; pushes from the host
/// arrive as events (raised on a thread-pool thread, so marshal to the UI thread yourself).
/// </summary>
public sealed class ControlPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ControlEnvelope>> _waiting = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private long _nextId;

    private ControlPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Any push (event.*), raw.</summary>
    public event EventHandler<ControlEnvelope>? EventReceived;

    public event EventHandler<EngineStateEvent>? EngineStateChanged;

    public event EventHandler<PendingApproval>? ApprovalPending;

    public event EventHandler<DownloadProgressEvent>? DownloadProgress;

    public event EventHandler<ControlStatus>? StatusChanged;

    /// <summary>Progress of a models-folder move or a file check.</summary>
    public event EventHandler<StoreProgressEvent>? StoreProgress;

    /// <summary>The connection to the host ended.</summary>
    public event EventHandler? Disconnected;

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>Connects to the current user's host. Throws <see cref="TimeoutException"/> when no host answers.</summary>
    public static async Task<ControlPipeClient> ConnectAsync(string? pipeName = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName ?? ControlPipe.NameForCurrentUser(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var client = new ControlPipeClient(pipe);
        client._reader = client.ReadLoopAsync();
        return client;
    }

    // ---- one method per control message ---------------------------------------------------------

    public Task ShowAsync(string? page = null, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Show, new ShowRequest { Page = page }, AiHostJsonContext.Default.ShowRequest, ct);

    public Task<ControlStatus> GetStatusAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.GetStatus, AiHostJsonContext.Default.ControlStatus, ct);

    public Task<ApprovalList> ListApprovalsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.ListApprovals, AiHostJsonContext.Default.ApprovalList, ct);

    public Task ApproveAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Approve, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public Task DenyAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Deny, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public Task<ClientList> ListClientsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.ListClients, AiHostJsonContext.Default.ClientList, ct);

    public Task RevokeClientAsync(string tokenId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RevokeClient, new ClientRevoke { TokenId = tokenId }, AiHostJsonContext.Default.ClientRevoke, ct);

    public Task<ModelListing> ListModelsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.ListModels, AiHostJsonContext.Default.ModelListing, ct);

    public Task StartDownloadAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.StartDownload, new DownloadCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.DownloadCommand, ct);

    public Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.PauseDownload, new DownloadCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.DownloadCommand, ct);

    public Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.CancelDownload, new DownloadCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.DownloadCommand, ct);

    public Task ImportModelAsync(string path, CancellationToken ct = default) =>
        SendAsync(ControlTypes.ImportModel, new ImportModelCommand { Path = path }, AiHostJsonContext.Default.ImportModelCommand, ct);

    public Task VerifyModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.VerifyModel, new VerifyModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.VerifyModelCommand, ct);

    public Task<StoreFolderInfo> GetStoreFolderAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.OpenStoreFolder, AiHostJsonContext.Default.StoreFolderInfo, ct);

    public Task MoveStoreAsync(string parentFolder, CancellationToken ct = default) =>
        SendAsync(ControlTypes.MoveStore, new MoveStoreCommand { ParentFolder = parentFolder }, AiHostJsonContext.Default.MoveStoreCommand, ct);

    public Task<CleanupScan> ScanCleanupAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.ScanCleanup, AiHostJsonContext.Default.CleanupScan, ct);

    public async Task<CleanupResult> CleanupAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct = default) =>
        Read(await ExchangeAsync(ControlTypes.RunCleanup,
            JsonSerializer.SerializeToElement(new CleanupCommand { ItemIds = [.. itemIds] }, AiHostJsonContext.Default.CleanupCommand), ct).ConfigureAwait(false),
            AiHostJsonContext.Default.CleanupResult);

    public Task CancelStoreOperationAsync(CancellationToken ct = default) => ExchangeAsync(ControlTypes.CancelStoreOperation, null, ct);

    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.ActivateModel, new ActivateModelCommand { CatalogueId = catalogueId },
            AiHostJsonContext.Default.ActivateModelCommand, ct);

    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.GetSettings, AiHostJsonContext.Default.HostSettings, ct);

    public Task SetSettingsAsync(HostSettings settings, CancellationToken ct = default) =>
        SendAsync(ControlTypes.SetSettings, settings, AiHostJsonContext.Default.HostSettings, ct);

    public async Task<ChatCompletionResponse> TryItChatAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var answer = await ExchangeAsync(ControlTypes.TryItChat,
            JsonSerializer.SerializeToElement(new TryItRequest { Request = request }, AiHostJsonContext.Default.TryItRequest), ct)
            .ConfigureAwait(false);
        return Read(answer, AiHostJsonContext.Default.ChatCompletionResponse);
    }

    /// <summary>
    /// Streaming "Try it": <paramref name="onPiece"/> gets each piece (answer text, thinking text) as it arrives; the result
    /// holds the timing. Cancelling <paramref name="ct"/> sends tryit.cancel so the host stops generating at once.
    /// </summary>
    public async Task<TryItDone> TryItStreamAsync(ChatCompletionRequest request, Action<string?, string?> onPiece,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var received = 0;
        var gate = new object();
        void Handler(object? _, ControlEnvelope e)
        {
            if (e.Type == ControlTypes.EventTryItChunk && e.Payload?.Deserialize(AiHostJsonContext.Default.TryItChunkEvent) is { } c
                && c.RunId == runId)
            {
                onPiece(c.Content, c.Reasoning);
                lock (gate) received++;
            }
        }

        EventReceived += Handler;
        try
        {
            using var stopper = ct.Register(() => _ = ExchangeAsync(ControlTypes.TryItCancel, null, CancellationToken.None));
            var answer = await ExchangeAsync(ControlTypes.TryItStream,
                JsonSerializer.SerializeToElement(new TryItRequest { Request = request, RunId = runId }, AiHostJsonContext.Default.TryItRequest),
                CancellationToken.None).ConfigureAwait(false);
            var done = Read(answer, AiHostJsonContext.Default.TryItDone);
            // The answer can overtake the last pushes; wait (briefly) until every piece has arrived.
            var patience = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < patience)
            {
                lock (gate)
                {
                    if (received >= done.Chunks) break;
                }

                await Task.Delay(5).ConfigureAwait(false);
            }

            return done;
        }
        finally
        {
            EventReceived -= Handler;
        }
    }

    public Task QuitAsync(CancellationToken ct = default) => ExchangeAsync(ControlTypes.Quit, null, ct);

    // ---- plumbing -------------------------------------------------------------------------------

    private async Task SendAsync<T>(string type, T payload, JsonTypeInfo<T> info, CancellationToken ct) =>
        await ExchangeAsync(type, JsonSerializer.SerializeToElement(payload, info), ct).ConfigureAwait(false);

    private async Task<T> RequestAsync<T>(string type, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class =>
        Read(await ExchangeAsync(type, null, ct).ConfigureAwait(false), info);

    private static T Read<T>(ControlEnvelope answer, JsonTypeInfo<T> info)
        where T : class =>
        answer.Payload is { } p ? p.Deserialize(info) ?? throw new InvalidDataException("Empty answer.")
            : throw new InvalidDataException("The host sent no payload.");

    /// <summary>Sends any envelope type and waits for the matching answer. Error answers throw.</summary>
    public async Task<ControlEnvelope> ExchangeAsync(string type, JsonElement? payload, CancellationToken ct = default)
    {
        var id = "c" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<ControlEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = tcs;
        try
        {
            await ControlPipe.WriteAsync(_pipe, new ControlEnvelope { Id = id, Type = type, Payload = payload }, _writeGate, ct)
                .ConfigureAwait(false);
            var answer = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            if (answer.Type == ControlTypes.Error)
            {
                var error = answer.Payload?.Deserialize(AiHostJsonContext.Default.ControlError) ?? new ControlError();
                throw new ControlPipeException(error.Code, error.Message);
            }

            return answer;
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var pending = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var envelope = await ControlPipe.ReadAsync(_pipe, pending, buffer, _stop.Token).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.Id is { } id)
                {
                    if (_waiting.TryGetValue(id, out var tcs))
                    {
                        tcs.TrySetResult(envelope);
                    }

                    continue;
                }

                Dispatch(envelope);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
        }
        finally
        {
            await pending.DisposeAsync().ConfigureAwait(false);
            foreach (var tcs in _waiting.Values)
            {
                tcs.TrySetException(new IOException("The connection to Portal AI Host ended."));
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Dispatch(ControlEnvelope e)
    {
        EventReceived?.Invoke(this, e);
        if (e.Payload is not { } p)
        {
            return;
        }

        try
        {
            switch (e.Type)
            {
                case ControlTypes.EventEngineState when p.Deserialize(AiHostJsonContext.Default.EngineStateEvent) is { } s:
                    EngineStateChanged?.Invoke(this, s);
                    break;
                case ControlTypes.EventApprovalPending when p.Deserialize(AiHostJsonContext.Default.PendingApproval) is { } a:
                    ApprovalPending?.Invoke(this, a);
                    break;
                case ControlTypes.EventDownloadProgress when p.Deserialize(AiHostJsonContext.Default.DownloadProgressEvent) is { } d:
                    DownloadProgress?.Invoke(this, d);
                    break;
                case ControlTypes.EventStoreProgress when p.Deserialize(AiHostJsonContext.Default.StoreProgressEvent) is { } sp:
                    StoreProgress?.Invoke(this, sp);
                    break;
                case ControlTypes.EventStatusChanged when p.Deserialize(AiHostJsonContext.Default.ControlStatus) is { } st:
                    StatusChanged?.Invoke(this, st);
                    break;
            }
        }
        catch (JsonException)
        {
            // Unknown or newer payload shape: the raw event was still raised.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        _stop.Dispose();
        _writeGate.Dispose();
    }
}
