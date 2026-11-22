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

    public async Task<IReadOnlyList<PendingApproval>> ListApprovalsAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.ListApprovals, AiHostJsonContext.Default.ApprovalList, ct).ConfigureAwait(false)).Pending;

    public Task ApproveAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Approve, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public Task DenyAsync(string requestId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.Deny, new ApprovalDecision { RequestId = requestId }, AiHostJsonContext.Default.ApprovalDecision, ct);

    public async Task<IReadOnlyList<ClientSummary>> ListClientsAsync(CancellationToken ct = default) =>
        (await RequestAsync(ControlTypes.ListClients, AiHostJsonContext.Default.ClientList, ct).ConfigureAwait(false)).Clients;

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
        CatalogueAvailable = listing.CatalogueAvailable,
    };

    public Task StartDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.StartDownload, catalogueId, ct);

    public Task PauseDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.PauseDownload, catalogueId, ct);

    public Task ResumeDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.ResumeDownload, catalogueId, ct);

    public Task CancelDownloadAsync(string catalogueId, CancellationToken ct = default) => Download(ControlTypes.CancelDownload, catalogueId, ct);

    public Task RemoveModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task RemoveOldModelAsync(string catalogueId, string storeFolder, CancellationToken ct = default) =>
        SendAsync(ControlTypes.RemoveModel, new RemoveModelCommand { CatalogueId = catalogueId, StoreFolder = storeFolder },
            AiHostJsonContext.Default.RemoveModelCommand, ct);

    public Task ActivateModelAsync(string catalogueId, CancellationToken ct = default) =>
        SendAsync(ControlTypes.ActivateModel, new ActivateModelCommand { CatalogueId = catalogueId }, AiHostJsonContext.Default.ActivateModelCommand, ct);

    public Task<HostSettings> GetSettingsAsync(CancellationToken ct = default) =>
        RequestAsync(ControlTypes.GetSettings, AiHostJsonContext.Default.HostSettings, ct);

    public async Task<SettingsSaved> SetSettingsAsync(HostSettings settings, CancellationToken ct = default)
    {
        var answer = await CallAsync(ControlTypes.SetSettings, JsonSerializer.SerializeToElement(settings, AiHostJsonContext.Default.HostSettings), ct)
            .ConfigureAwait(false);
        return answer.Payload is { ValueKind: JsonValueKind.Object } p
            ? p.Deserialize(AiHostJsonContext.Default.SettingsSaved) ?? new SettingsSaved()
            : new SettingsSaved();
    }

    public async IAsyncEnumerable<TryItPiece> TryItAsync(string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new TryItRequest
        {
            Request = new ChatCompletionRequest { Messages = [new ChatMessage { Role = ChatRoles.User, Content = prompt }] },
        };
        var answer = await CallAsync(ControlTypes.TryItChat, JsonSerializer.SerializeToElement(request, AiHostJsonContext.Default.TryItRequest), ct)
            .ConfigureAwait(false);
        var response = Read(answer, AiHostJsonContext.Default.ChatCompletionResponse);
        // The host answers in one piece today; the window still reads it as pieces.
        var message = response.Choices.FirstOrDefault()?.Message;
        var (thinking, answer2) = TryItText.Split(message?.ReasoningContent, message?.Content);
        if (thinking.Length > 0)
            yield return new TryItPiece(thinking, true);
        yield return new TryItPiece(answer2);
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
