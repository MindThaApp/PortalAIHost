using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Remote.Client;

/// <summary>
/// The laptop side of Model control (docs/client-mode-v1.md 4.7): lists, loads and unloads models on the paired PC and
/// reads or sets their placement, through the signed <see cref="RemoteHostLink"/>. Every call answers with a
/// <see cref="RemoteModelsAnswer"/>; a refusal or an unreachable PC is an answer with a short plain message, never an
/// exception. Only the four device routes are ever called. Nothing here touches a file.
/// </summary>
public sealed class RemoteModelClient(RemoteHostLink link)
{
    private readonly object _gate = new();
    private volatile int _grant; // 0 unknown, 1 granted, 2 refused
    private HashSet<string> _loadedByMe = [];
    private Task<RemoteModelsAnswer>? _listing;

    /// <summary>Whether this laptop has Model control on the PC, as last seen (null: not known yet).</summary>
    public bool? Granted => _grant switch { 1 => true, 2 => false, _ => null };

    /// <summary>Ids of the models this laptop has loaded on the PC, as last seen.</summary>
    public IReadOnlyCollection<string> LoadedByThisLaptop
    {
        get
        {
            lock (_gate)
            {
                return _loadedByMe.ToArray();
            }
        }
    }

    /// <summary>Raised when the grant or the list of this laptop's loaded models changed.</summary>
    public event Action? Changed;

    /// <summary>The capabilities answer carries <c>model_control</c>; the backend reports it here.</summary>
    public void NoteGrant(bool? granted)
    {
        if (granted is null || Granted == granted)
        {
            return;
        }

        _grant = granted == true ? 1 : 2;
        if (granted == false)
        {
            SetLoaded([]);
        }

        Changed?.Invoke();
    }

    /// <summary>Lists the PC's installed models. Calls that arrive while one is running share its answer (bursts coalesce).</summary>
    public Task<RemoteModelsAnswer> ListAsync(CancellationToken ct = default)
    {
        Task<RemoteModelsAnswer> task;
        lock (_gate)
        {
            _listing ??= ListSharedAsync();
            task = _listing;
        }

        return task.WaitAsync(ct);
    }

    private async Task<RemoteModelsAnswer> ListSharedAsync()
    {
        try
        {
            return await Task.Run(ListCoreAsync).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _listing = null;
            }
        }
    }

    public async Task<RemoteModelsAnswer> LoadAsync(string modelId, string? placement, CancellationToken ct = default)
    {
        if (!DevicePlacements.IsValid(placement ?? DevicePlacements.Auto))
        {
            return Refusal(ErrorCodes.InvalidRequest, null, "That placement is not available.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new DeviceLoadRequest { ModelId = modelId, Placement = placement },
            AiHostJsonContext.Default.DeviceLoadRequest);
        return await ChangeAsync(new RemoteRequest("POST", Routes.DeviceModelLoad, [], body), ct).ConfigureAwait(false);
    }

    public async Task<RemoteModelsAnswer> UnloadAsync(string modelId, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new DeviceUnloadRequest { ModelId = modelId },
            AiHostJsonContext.Default.DeviceUnloadRequest);
        return await ChangeAsync(new RemoteRequest("POST", Routes.DeviceModelUnload, [], body), ct).ConfigureAwait(false);
    }

    /// <summary>The placements of the models this laptop loaded (the answer's <c>Placements</c>).</summary>
    public async Task<RemoteModelsAnswer> GetPlacementsAsync(CancellationToken ct = default)
    {
        var sent = await TrySendAsync(new RemoteRequest("GET", Routes.DevicePlacement, []), ct).ConfigureAwait(false);
        if (sent.Failure is { } failure)
        {
            return failure;
        }

        var list = TryRead(sent.Reply!.Value.Body, AiHostJsonContext.Default.DevicePlacementList);
        return Success(null, list?.Placements ?? []);
    }

    public async Task<RemoteModelsAnswer> SetPlacementAsync(DevicePlacementRequest placement, CancellationToken ct = default)
    {
        if (!DevicePlacements.IsValid(placement.Compute))
        {
            return Refusal(ErrorCodes.InvalidRequest, null, "That placement is not available.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(placement, AiHostJsonContext.Default.DevicePlacementRequest);
        var sent = await TrySendAsync(new RemoteRequest("POST", Routes.DevicePlacement, [], body), ct).ConfigureAwait(false);
        if (sent.Failure is { } failure)
        {
            return await WithFreshListAsync(failure, ct).ConfigureAwait(false);
        }

        var fresh = await ListAsync(ct).ConfigureAwait(false);
        var placements = await GetPlacementsAsync(ct).ConfigureAwait(false);
        fresh.Placements = placements.Placements;
        return fresh;
    }

    /// <summary>Plain, short words for an error of the model routes (the Models page shows exactly this).</summary>
    public static string PlainMessage(string? code, string? reason) => (code, reason) switch
    {
        (ErrorCodes.ModelControlRequired, _) => "This laptop does not have Model control. Ask the PC owner.",
        (ErrorCodes.ModelConflict, ModelConflictReasons.OwnerChanged) => "The PC owner changed the model. The list is refreshed.",
        (ErrorCodes.ModelConflict, ModelConflictReasons.InUseByHost) => "The PC's own apps are using the model. Ask the PC owner to allow replacing models.",
        (ErrorCodes.ModelConflict, ModelConflictReasons.InUseByDevice) => "Another device is using the model.",
        (ErrorCodes.ModelConflict, ModelConflictReasons.NotLoadedByDevice) => "This laptop did not load that model, so it cannot unload it.",
        (ErrorCodes.ModelConflict, _) => "The model is busy on the PC.",
        (ErrorCodes.InsufficientMemory, _) => "The PC does not have enough free memory for that model.",
        (ErrorCodes.NotFound, _) => "The PC does not have that model.",
        (ErrorCodes.InvalidRequest, _) => "The PC did not accept that request.",
        ("remote_unreachable", _) => "The PC cannot be reached right now.",
        ("device_paused" or "device_quiet_hours" or "host_busy_gaming" or "device_limit_reached", _) =>
            "The PC is not accepting requests from this laptop right now.",
        _ => "The PC could not do that.",
    };

    private async Task<RemoteModelsAnswer> ListCoreAsync()
    {
        var sent = await TrySendAsync(new RemoteRequest("GET", Routes.DeviceModels, []), CancellationToken.None).ConfigureAwait(false);
        if (sent.Failure is { } failure)
        {
            return failure;
        }

        var list = TryRead(sent.Reply!.Value.Body, AiHostJsonContext.Default.DeviceModelList);
        return Success(list?.Models ?? [], null);
    }

    private async Task<RemoteModelsAnswer> ChangeAsync(RemoteRequest request, CancellationToken ct)
    {
        var sent = await TrySendAsync(request, ct).ConfigureAwait(false);
        if (sent.Failure is { } failure)
        {
            return await WithFreshListAsync(failure, ct).ConfigureAwait(false);
        }

        return await ListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>After a conflict the list is read again so the page shows what the PC really has.</summary>
    private async Task<RemoteModelsAnswer> WithFreshListAsync(RemoteModelsAnswer failure, CancellationToken ct)
    {
        if (failure.ErrorCode == ErrorCodes.ModelConflict)
        {
            var fresh = await ListAsync(ct).ConfigureAwait(false);
            failure.Models = fresh.Models;
        }

        return failure;
    }

    private readonly record struct Sent(RemoteReply? Reply, RemoteModelsAnswer? Failure);

    private async Task<Sent> TrySendAsync(RemoteRequest request, CancellationToken ct)
    {
        RemoteReply reply;
        try
        {
            reply = await link.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (RemoteUnreachableException)
        {
            return new Sent(null, Refusal("remote_unreachable", null, PlainMessage("remote_unreachable", null)));
        }

        if (reply.Status is >= 200 and < 300)
        {
            if (_grant != 1)
            {
                _grant = 1;
                Changed?.Invoke();
            }

            return new Sent(reply, null);
        }

        var (code, reason) = ReadError(reply.Body);
        if (code == ErrorCodes.ModelControlRequired)
        {
            NoteGrant(false);
        }

        return new Sent(null, Refusal(code ?? "error", reason, PlainMessage(code, reason)));
    }

    private RemoteModelsAnswer Refusal(string code, string? reason, string message) => new()
    {
        Ok = false,
        ModelControl = _grant == 1,
        PcName = link.Remote?.PcName,
        DeviceId = link.Remote?.DeviceId,
        ErrorCode = code,
        Reason = reason,
        Message = message,
    };

    private RemoteModelsAnswer Success(List<DeviceModelState>? models, List<DevicePlacementRequest>? placements)
    {
        var deviceId = link.Remote?.DeviceId;
        if (models is not null)
        {
            SetLoaded(models.Where(m => m.State != "unloaded" && m.LoadedBy is not null && m.LoadedBy == deviceId)
                .Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
        }

        return new RemoteModelsAnswer
        {
            Ok = true,
            ModelControl = true,
            PcName = link.Remote?.PcName,
            DeviceId = deviceId,
            Models = models ?? [],
            Placements = placements,
        };
    }

    private void SetLoaded(HashSet<string> next)
    {
        bool changed;
        lock (_gate)
        {
            changed = !_loadedByMe.SetEquals(next);
            _loadedByMe = next;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static T? TryRead<T>(string body, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Code, string? Reason) ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var e))
            {
                return (null, null);
            }

            var code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            string? reason = null;
            if (e.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reason = r.GetString();
            }

            return (code, reason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
