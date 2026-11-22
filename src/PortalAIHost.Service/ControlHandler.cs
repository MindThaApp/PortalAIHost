using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

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
                        CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt, Revoked = c.Revoked,
                    }).ToList(),
                }, AiHostJsonContext.Default.ClientList);

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
                    await shell.Models.RemoveAsync(remove.CatalogueId, ct).ConfigureAwait(false);
                }
                return ControlPipe.Ok(id);

            case ControlTypes.ActivateModel:
                await shell.ActivateAsync(Payload(r, AiHostJsonContext.Default.ActivateModelCommand)!.CatalogueId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.GetSettings:
                return Ok(id, shell.Settings.Current, AiHostJsonContext.Default.HostSettings);

            case ControlTypes.SetSettings:
                return Ok(id, SaveSettings(Payload(r, AiHostJsonContext.Default.HostSettings)!), AiHostJsonContext.Default.SettingsSaved);

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

            case ControlTypes.TryItCancel:
                CancelQuietly(Volatile.Read(ref _tryIt));
                return ControlPipe.Ok(id);

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
    /// Saves settings. Idle unload applies at once; memory cap, backend and the models folder at the next host start.
    /// A new models folder never moves or deletes the models already downloaded: they stay where they are.
    /// </summary>
    internal SettingsSaved SaveSettings(HostSettings next)
    {
        var old = shell.Settings.Current;
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
        var restart = folderChanged || old.MaxMemoryPercent != next.MaxMemoryPercent
            || !string.Equals(old.Backend, next.Backend, StringComparison.Ordinal);
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

    /// <summary>Publisher from the caller's file version info, only for a verified caller; otherwise unknown.</summary>
    internal static string? PublisherOf(CallerIdentity caller)
    {
        if (!caller.Verified || caller.ExePath is not { Length: > 0 } exe)
        {
            return null;
        }

        try
        {
            var company = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static PendingApproval ToPending(AccessRequestInfo info) => new()
    {
        Publisher = PublisherOf(info.Caller),
        RequestId = info.RequestId,
        AppId = info.AppId,
        AppName = info.AppName,
        AppVersion = info.AppVersion,
        Reason = info.Reason,
        CallerIdentity = info.Caller.PackageFamilyName ?? info.Caller.ExePath ?? "process " + info.Caller.ProcessId,
        CallerVerified = info.Caller.Verified,
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
}
