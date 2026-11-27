using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>Sends the pieces of a streaming "Try it" reply to the windows, a few at a time so the pipe is not flooded.</summary>
internal sealed class TryItBatcher(ControlPipeServer pipe, string runId, TimeProvider time)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);
    private readonly System.Text.StringBuilder _content = new();
    private readonly System.Text.StringBuilder _reasoning = new();
    private long _last = time.GetTimestamp();

    public int Sent { get; private set; }

    public void Add(string? content, string? reasoning)
    {
        if (content is not null) _content.Append(content);
        if (reasoning is not null) _reasoning.Append(reasoning);
        if (time.GetElapsedTime(_last) >= Interval)
        {
            Flush();
        }
    }

    public void Flush()
    {
        _last = time.GetTimestamp();
        if (_content.Length == 0 && _reasoning.Length == 0)
        {
            return;
        }

        var piece = new TryItChunkEvent
        {
            RunId = runId,
            Seq = ++Sent,
            Content = _content.Length > 0 ? _content.ToString() : null,
            Reasoning = _reasoning.Length > 0 ? _reasoning.ToString() : null,
        };
        _content.Clear();
        _reasoning.Clear();
        _ = pipe.BroadcastAsync(ControlTypes.EventTryItChunk,
            JsonSerializer.SerializeToElement(piece, AiHostJsonContext.Default.TryItChunkEvent));
    }
}

/// <summary>Answers admin pipe requests. This is the only place admin actions exist; HTTP has none.</summary>
internal sealed class ControlHandler(HostShell shell)
{
    private CancellationTokenSource? _tryIt;

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task<ControlEnvelope> HandleAsync(ControlEnvelope request, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (ControlPipeException ex)
        {
            return ControlPipe.Error(request.Id, ex.Code, ex.Message);
        }
        catch (EngineException ex)
        {
            return ControlPipe.Error(request.Id, ex.ErrorCode, ex.Message);
        }
        catch (ApiException ex)
        {
            return ControlPipe.Error(request.Id, ex.Code, ex.Message);
        }
        catch (JsonException)
        {
            return ControlPipe.Error(request.Id, ErrorCodes.InvalidRequest, "The payload is not valid for this message.");
        }
        catch (KeyNotFoundException)
        {
            return ControlPipe.Error(request.Id, ErrorCodes.NotFound, "Unknown id.");
        }
    }

    private async Task<ControlEnvelope> DispatchAsync(ControlEnvelope r, CancellationToken ct)
    {
        var id = r.Id;
        var runtime = shell.Runtime;
        switch (r.Type)
        {
            case ControlTypes.Show:
                shell.RequestShow(Payload(r, AiHostJsonContext.Default.ShowRequest, optional: true)?.Page);
                return ControlPipe.Ok(id);

            case ControlTypes.GetStatus:
                return Ok(id, shell.BuildControlStatus(), AiHostJsonContext.Default.ControlStatus);

            case ControlTypes.ListApprovals:
                return Ok(id, new ApprovalList { Pending = runtime.Registry.PendingRequests.Select(ToPending).ToList() },
                    AiHostJsonContext.Default.ApprovalList);

            case ControlTypes.Approve:
                await runtime.Registry.ApproveAsync(Payload(r, AiHostJsonContext.Default.ApprovalDecision)!.RequestId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.Deny:
                await runtime.Registry.DenyAsync(Payload(r, AiHostJsonContext.Default.ApprovalDecision)!.RequestId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.ListClients:
                var clients = await runtime.Registry.ListAsync(ct).ConfigureAwait(false);
                return Ok(id, new ClientList
                {
                    Clients = clients.Select(c => new ClientSummary
                    {
                        TokenId = c.TokenId, AppId = c.AppId, AppName = c.AppName, DeviceId = c.DeviceId,
                        DeviceName = shell.Remote.Devices.Find(c.DeviceId)?.Name,
                        CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt, Revoked = c.Revoked,
                        Paused = ControlOf(runtime, c.AppId).Paused,
                        Priority = ControlOf(runtime, c.AppId).Priority.ToWire(),
                    }).ToList(),
                }, AiHostJsonContext.Default.ClientList);

            case ControlTypes.SetAppControl:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.AppControlCommand)!;
                if (runtime.AppControls is not { } controls)
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "App controls are not available.");
                }

                if (cmd.Priority is not null && AppPriorityWire.Parse(cmd.Priority) is null)
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "priority must be high, normal or low.");
                }

                await controls.SetAsync(cmd.AppId, cmd.Paused, AppPriorityWire.Parse(cmd.Priority), ct).ConfigureAwait(false);
                return ControlPipe.Ok(id);
            }

            case ControlTypes.RevokeClient:
                await runtime.Registry.RevokeAsync(Payload(r, AiHostJsonContext.Default.ClientRevoke)!.TokenId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.ListModels:
                return Ok(id, await shell.Models.ListAsync(ct).ConfigureAwait(false), AiHostJsonContext.Default.ModelListing);

            case ControlTypes.StartDownload or ControlTypes.ResumeDownload:
                // Start on a paused model resumes from its partial file, exactly like resume.
                await shell.Models.StartAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId, ct).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.PauseDownload:
                await shell.Models.PauseAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.CancelDownload:
                await shell.Models.CancelAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId, ct).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.RemoveModel:
                var remove = Payload(r, AiHostJsonContext.Default.RemoveModelCommand)!;
                if (remove.StoreFolder is { Length: > 0 } oldFolder)
                {
                    await shell.Models.RemoveFromPreviousAsync(remove.CatalogueId, oldFolder, ct).ConfigureAwait(false);
                }
                else
                {
                    await shell.Models.RemoveAsync(remove.CatalogueId, ct, remove.SwitchToId, remove.RemoveDamaged).ConfigureAwait(false);
                    await shell.PushStatusAsync().ConfigureAwait(false);
                }
                return ControlPipe.Ok(id);

            case ControlTypes.VerifyModel:
                // Answers once the check has started; progress and the result arrive as event.store_progress.
                _ = await shell.Models.StartVerifyAsync(Payload(r, AiHostJsonContext.Default.VerifyModelCommand)!.CatalogueId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.ImportModel:
                // Answers once the copy has started; progress and the result arrive as event.store_progress.
                _ = await shell.Models.StartImportAsync(Payload(r, AiHostJsonContext.Default.ImportModelCommand)!.Path, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.OpenStoreFolder:
                return Ok(id, new StoreFolderInfo { Path = shell.Models.StoreFolder() }, AiHostJsonContext.Default.StoreFolderInfo);

            case ControlTypes.ScanCleanup:
                return Ok(id, await shell.Models.ScanCleanupAsync(ct).ConfigureAwait(false), AiHostJsonContext.Default.CleanupScan);

            case ControlTypes.RunCleanup:
            {
                var cleaned = await shell.Models.CleanupAsync(Payload(r, AiHostJsonContext.Default.CleanupCommand)!.ItemIds, ct)
                    .ConfigureAwait(false);
                await shell.PushStatusAsync().ConfigureAwait(false);
                return Ok(id, cleaned, AiHostJsonContext.Default.CleanupResult);
            }

            case ControlTypes.MoveStore:
                _ = await shell.Models.StartMoveAsync(Payload(r, AiHostJsonContext.Default.MoveStoreCommand)!.ParentFolder, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.CancelStoreOperation:
                shell.Models.CancelOperation();
                return ControlPipe.Ok(id);

            case ControlTypes.ActivateModel:
                await shell.ActivateAsync(Payload(r, AiHostJsonContext.Default.ActivateModelCommand)!.CatalogueId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.GetSettings:
                return Ok(id, shell.Settings.Current, AiHostJsonContext.Default.HostSettings);

            case ControlTypes.SetSettings:
            {
                var next = Payload(r, AiHostJsonContext.Default.HostSettings)!;
                var saved = SaveSettings(next);
                await shell.Remote.ApplyAsync(next.Remote, ct).ConfigureAwait(false);
                // Performance limits apply at once: the planner re-plans and a loaded model reloads if it must.
                var applied = await runtime.ApplyPerformanceAsync(ct).ConfigureAwait(false);
                saved.PerformanceReloaded = applied.Reloaded.Count > 0 || applied.Unloaded.Count > 0
                    ? new PerformanceReloadInfo { Reloaded = applied.Reloaded.ToList(), Unloaded = applied.Unloaded.ToList() }
                    : null;
                return Ok(id, saved, AiHostJsonContext.Default.SettingsSaved);
            }

            case ControlTypes.GetPerformance:
            {
                var candidate = Payload(r, AiHostJsonContext.Default.HostSettings, optional: true);
                var view = await runtime.BuildPerformanceViewAsync(candidate, withStops: true, ct).ConfigureAwait(false);
                return Ok(id, view, AiHostJsonContext.Default.PerformanceView);
            }

            case ControlTypes.GetHealth:
                return Ok(id, await runtime.BuildHealthAsync(shell.Server is not null, ct).ConfigureAwait(false),
                    AiHostJsonContext.Default.HealthReport);

            case ControlTypes.TryItChat:
            {
                var tryIt = Payload(r, AiHostJsonContext.Default.TryItRequest)!;
                using var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var previous = Interlocked.Exchange(ref _tryIt, mine);
                CancelQuietly(previous);
                try
                {
                    var answer = await runtime.TryItChatAsync(tryIt.Request, mine.Token).ConfigureAwait(false);
                    return Ok(id, answer, AiHostJsonContext.Default.ChatCompletionResponse);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _tryIt, null, mine);
                }
            }

            case ControlTypes.TryItStream:
            {
                var tryIt = Payload(r, AiHostJsonContext.Default.TryItRequest)!;
                var runId = tryIt.RunId ?? "";
                using var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var previous = Interlocked.Exchange(ref _tryIt, mine);
                CancelQuietly(previous);
                var batch = new TryItBatcher(shell.Pipe, runId, TimeProvider.System);
                try
                {
                    var done = await runtime.TryItStreamAsync(tryIt.Request, runId, batch.Add, mine.Token).ConfigureAwait(false);
                    batch.Flush();
                    done.Chunks = batch.Sent;
                    return Ok(id, done, AiHostJsonContext.Default.TryItDone);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _tryIt, null, mine);
                }
            }

            case ControlTypes.TryItCancel:
                CancelQuietly(Volatile.Read(ref _tryIt));
                return ControlPipe.Ok(id);

            case ControlTypes.GetActivity:
            {
                var known = (await runtime.Registry.ListAsync(ct).ConfigureAwait(false))
                    .GroupBy(c => c.AppId).ToDictionary(g => g.Key, g => g.First().AppName);
                known[Portal.AiHost.Core.Runtime.AiHostRuntime.TryItAppId] = "Try it";
                var apps = runtime.Activity.Snapshot(a => known.GetValueOrDefault(a));
                return Ok(id, new ActivityReport { Apps = apps }, AiHostJsonContext.Default.ActivityReport);
            }

            case ControlTypes.LoadEngine:
            {
                var kind = Payload(r, AiHostJsonContext.Default.EngineCommand, optional: true)?.Kind ?? ModelKinds.Chat;
                await runtime.EnsureLoadedAsync(kind, ct).ConfigureAwait(false);
                await shell.PushStatusAsync().ConfigureAwait(false);
                return ControlPipe.Ok(id);
            }

            case ControlTypes.UnloadEngine:
            {
                // Stops the engine process only. Model files are never touched.
                var kind = Payload(r, AiHostJsonContext.Default.EngineCommand, optional: true)?.Kind ?? ModelKinds.Chat;
                await shell.Remote.ModelControl.OwnerChangedAsync(kind, ct).ConfigureAwait(false); // the owner's action wins
                await runtime.UnloadAsync(kind, ct).ConfigureAwait(false);
                await shell.PushStatusAsync().ConfigureAwait(false);
                return ControlPipe.Ok(id);
            }

            case ControlTypes.RemoteGet:
                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);

            case ControlTypes.RemotePairStart:
                try
                {
                    return Ok(id, shell.Remote.StartPairing(), AiHostJsonContext.Default.PairingOfferInfo);
                }
                catch (InvalidOperationException ex)
                {
                    return ControlPipe.Error(id, ErrorCodes.FeatureDisabled, ex.Message);
                }

            case ControlTypes.RemotePairCancel:
                shell.Remote.CancelPairing();
                return ControlPipe.Ok(id);

            case ControlTypes.RemoteRotateRelayKey:
                return await shell.Remote.RotateRelayKeyAsync(ct).ConfigureAwait(false)
                    ? ControlPipe.Ok(id)
                    : ControlPipe.Error(id, ErrorCodes.InternalError, "The new relay access key could not be saved. Nothing was changed.");

            case ControlTypes.RemoteRemoveDevice:
                if (!shell.Remote.RemoveDevice(Payload(r, AiHostJsonContext.Default.RemoveDeviceCommand)!.DeviceId))
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown device.");
                }

                return ControlPipe.Ok(id);

            case ControlTypes.RemoteSetModelControl:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.SetModelControlCommand)!;
                if (!shell.Remote.SetModelControl(cmd.DeviceId, cmd.Enabled))
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown device.");
                }

                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);
            }

            case ControlTypes.RemoteTakeBack:
            {
                var kind = Payload(r, AiHostJsonContext.Default.TakeBackCommand)!.Kind;
                if (kind is not (ModelKinds.Chat or ModelKinds.Embedding))
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "kind must be chat or embedding.");
                }

                await shell.Remote.ModelControl.TakeBackAsync(kind, ct).ConfigureAwait(false);
                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);
            }

            case ControlTypes.RemoteDevicePause:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.PauseDeviceCommand)!;
                if (!shell.Remote.SetPaused(cmd.DeviceId, cmd.App, cmd.Paused))
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown device.");
                }

                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);
            }

            case ControlTypes.RemoteDeviceSettings:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.SetDeviceSettingsCommand)!;
                if (!shell.Remote.SetDeviceSettings(cmd))
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown device.");
                }

                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);
            }

            case ControlTypes.RemoteDeviceRename:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.RenameDeviceCommand)!;
                if (!shell.Remote.RenameDevice(cmd.DeviceId, cmd.Name))
                {
                    return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown device, or the name is empty.");
                }

                return Ok(id, shell.Remote.Snapshot(), AiHostJsonContext.Default.RemoteStatus);
            }

            case ControlTypes.ClientStatus:
                return Ok(id, shell.ClientMode.Snapshot(), AiHostJsonContext.Default.ClientModeStatus);

            case ControlTypes.ClientSetMode:
            {
                var mode = Payload(r, AiHostJsonContext.Default.SetModeCommand)!.Mode;
                if (mode is not (HostModes.Server or HostModes.Client))
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "mode must be server or client.");
                }

                var status = await shell.ClientMode.SetModeAsync(mode, ct).ConfigureAwait(false);
                await shell.PushStatusAsync().ConfigureAwait(false);
                return Ok(id, status, AiHostJsonContext.Default.ClientModeStatus);
            }

            case ControlTypes.ClientSetFallback:
            {
                var fallback = Payload(r, AiHostJsonContext.Default.SetFallbackCommand)!.Fallback;
                if (fallback is not (ClientFallbackModes.None or ClientFallbackModes.Local))
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "fallback must be none or local.");
                }

                shell.ClientMode.SetFallback(fallback);
                await shell.PushStatusAsync().ConfigureAwait(false);
                return Ok(id, shell.ClientMode.Snapshot(), AiHostJsonContext.Default.ClientModeStatus);
            }

            case ControlTypes.ClientSetRelayMode:
            {
                var relayMode = Payload(r, AiHostJsonContext.Default.SetRelayModeCommand)!.RelayMode;
                if (relayMode is not (RelayModes.Auto or RelayModes.Manual))
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "relay mode must be auto or manual.");
                }

                shell.ClientMode.SetRelayMode(relayMode);
                return Ok(id, shell.ClientMode.Snapshot(), AiHostJsonContext.Default.ClientModeStatus);
            }

            case ControlTypes.ClientUseRelay:
                shell.ClientMode.SetUseRelay(Payload(r, AiHostJsonContext.Default.UseRelayCommand)!.Enabled);
                return Ok(id, shell.ClientMode.Snapshot(), AiHostJsonContext.Default.ClientModeStatus);

            case ControlTypes.ClientPromptShown:
                shell.ClientMode.MarkPromptShown();
                return Ok(id, shell.ClientMode.Snapshot(), AiHostJsonContext.Default.ClientModeStatus);

            case ControlTypes.ClientRemoteModels:
            {
                var models = shell.ClientMode.Models;
                if (models is null)
                {
                    return Ok(id, NotClient(), AiHostJsonContext.Default.RemoteModelsAnswer);
                }

                return Ok(id, await models.ListAsync(ct).ConfigureAwait(false), AiHostJsonContext.Default.RemoteModelsAnswer);
            }

            case ControlTypes.ClientRemoteLoad:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.DeviceLoadRequest)!;
                var models = shell.ClientMode.Models;
                return Ok(id, models is null ? NotClient() : await models.LoadAsync(cmd.ModelId, cmd.Placement, ct).ConfigureAwait(false),
                    AiHostJsonContext.Default.RemoteModelsAnswer);
            }

            case ControlTypes.ClientRemoteUnload:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.DeviceUnloadRequest)!;
                var models = shell.ClientMode.Models;
                return Ok(id, models is null ? NotClient() : await models.UnloadAsync(cmd.ModelId, ct).ConfigureAwait(false),
                    AiHostJsonContext.Default.RemoteModelsAnswer);
            }

            case ControlTypes.ClientRemotePlacement:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.RemotePlacementCommand, optional: true);
                var models = shell.ClientMode.Models;
                RemoteModelsAnswer answer;
                if (models is null)
                {
                    answer = NotClient();
                }
                else if (cmd?.Set is { } set)
                {
                    answer = await models.SetPlacementAsync(set, ct).ConfigureAwait(false);
                }
                else
                {
                    answer = await models.GetPlacementsAsync(ct).ConfigureAwait(false);
                }

                return Ok(id, answer, AiHostJsonContext.Default.RemoteModelsAnswer);
            }

            case ControlTypes.ClientAppModel:
            {
                var cmd = Payload(r, AiHostJsonContext.Default.AppModelCommand, optional: true);
                var store = shell.ClientMode.AppModels;
                if (store is null)
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "App models are not available.");
                }

                if (!string.IsNullOrEmpty(cmd?.AppId) && !store.Set(cmd.AppId, cmd.ModelId))
                {
                    return ControlPipe.Error(id, ErrorCodes.InvalidRequest, "That app or model id is not valid.");
                }

                return Ok(id, new AppModelList
                {
                    Apps = store.All().OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => new AppModelEntry { AppId = p.Key, ModelId = p.Value }).ToList(),
                }, AiHostJsonContext.Default.AppModelList);
            }

            case ControlTypes.Quit:
                // Answer first, then quit, so the window sees the acknowledgement.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                    shell.RequestQuit();
                }, CancellationToken.None);
                return ControlPipe.Ok(id);

            default:
                return ControlPipe.Error(id, ErrorCodes.NotFound, "Unknown control message.");
        }
    }

    /// <summary>
    /// Saves settings. Idle unload and the performance limits apply at once; a new models folder at the next host start.
    /// A new models folder never moves or deletes the models already downloaded: they stay where they are.
    /// </summary>
    internal SettingsSaved SaveSettings(HostSettings next)
    {
        var old = shell.Settings.Current;
        next.Mode = old.Mode; // Host-owned: only client.setmode changes the mode.
        next.ClientFallback = old.ClientFallback; // Host-owned: client.setfallback.
        next.FallbackPromptShown = old.FallbackPromptShown;
        next.RelayMode = old.RelayMode; // Host-owned: client.setrelaymode.
        var folderChanged = !string.Equals(old.ModelsFolder ?? "", next.ModelsFolder ?? "", StringComparison.OrdinalIgnoreCase);
        var refused = folderChanged ? CheckModelsFolder(next.ModelsFolder, shell.Store?.Location.Path) : null;
        if (refused is not null)
        {
            next.ModelsFolder = old.ModelsFolder; // the other settings still save
            folderChanged = false;
        }

        // Host-owned: earlier store folders stay listed (and removable) after a change; nothing is moved or deleted.
        var previous = new List<string>(old.PreviousStoreFolders ?? []);
        if (folderChanged && shell.Store?.Location.Path is { } currentRoot
            && !previous.Contains(currentRoot, StringComparer.OrdinalIgnoreCase))
        {
            previous.Insert(0, currentRoot);
        }

        next.PreviousStoreFolders = previous.Count == 0 ? null : previous.Take(MaxPreviousStoreFolders).ToList();
        shell.Settings.Save(next);
        var restart = folderChanged;
        return new SettingsSaved
        {
            ModelsFolderChanged = folderChanged,
            AppliesAtRestart = restart,
            FolderRefused = refused,
            Note = folderChanged
                ? $"New downloads go to the '{ModelStore.UserFolderName}' folder inside the new folder after the host restarts. "
                  + "Models already downloaded are not moved or deleted; they stay listed so you can remove them."
                : null,
        };
    }

    private static RemoteModelsAnswer NotClient() => new()
    {
        Ok = false,
        ErrorCode = "not_client",
        Message = "This computer is not using another PC.",
    };

    /// <summary>Earlier models folders kept in settings.</summary>
    internal const int MaxPreviousStoreFolders = 5;

    /// <summary>Why a chosen models folder cannot be used, or null. Reads only; nothing is created.</summary>
    internal static string? CheckModelsFolder(string? chosen, string? currentStoreRoot)
    {
        if (string.IsNullOrEmpty(chosen))
        {
            return null; // back to the default folder
        }

        if (!Path.IsPathFullyQualified(chosen))
        {
            return "Pick a full folder path, for example D:\\Models.";
        }

        if (ModelStore.CheckParentFolder(chosen) is { } reason)
        {
            return reason;
        }

        if (currentStoreRoot is not null)
        {
            var full = Path.GetFullPath(chosen).TrimEnd(Path.DirectorySeparatorChar);
            var current = Path.GetFullPath(currentStoreRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(current, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return "That folder is inside the current models folder. Pick a folder outside it.";
            }
        }

        return null;
    }

    /// <summary>
    /// Publisher shown in the approval prompt: the display name from a Store or Inbox package, set by the identity
    /// resolver. Never taken from the caller's own file version info, which any exe can fill in.
    /// </summary>
    internal static string? PublisherOf(CallerIdentity caller) =>
        caller.Verified && !string.IsNullOrWhiteSpace(caller.PackagePublisher) ? caller.PackagePublisher : null;

    internal static PendingApproval ToPending(AccessRequestInfo info) => new()
    {
        Publisher = PublisherOf(info.Caller),
        RequestId = info.RequestId,
        AppId = info.AppId,
        AppName = info.AppName,
        AppVersion = info.AppVersion,
        Reason = info.Reason,
        CallerIdentity = info.Caller.Remote is { } remote
            ? "unverified: an app on the paired device " + remote.Name
            : info.Caller.PackageFamilyName ?? info.Caller.ExePath ?? "process " + info.Caller.ProcessId,
        DeviceName = info.Caller.Remote?.Name,
        CallerVerified = info.Caller.Verified,
        CallerPortalApp = info.Caller.Verified && info.Caller.PortalApp,
        ExpiresAt = info.ExpiresAt,
    };

    private static ControlEnvelope Ok<T>(string? id, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        ControlPipe.Ok(id, JsonSerializer.SerializeToElement(value, info));

    private static T? Payload<T>(ControlEnvelope r, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, bool optional = false)
        where T : class
    {
        if (r.Payload is not { ValueKind: JsonValueKind.Object } p)
        {
            return optional ? null : throw new ControlPipeException(ErrorCodes.InvalidRequest, "This message needs a payload.");
        }

        return p.Deserialize(info) ?? throw new ControlPipeException(ErrorCodes.InvalidRequest, "This message needs a payload.");
    }

    private static AppControl ControlOf(Portal.AiHost.Core.Runtime.AiHostRuntime runtime, string appId) =>
        runtime.AppControls?.Get(appId) ?? AppControl.Default;
}
