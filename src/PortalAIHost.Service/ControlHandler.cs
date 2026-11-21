using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>Answers admin pipe requests. This is the only place admin actions exist; HTTP has none.</summary>
internal sealed class ControlHandler(HostShell shell)
{
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
                await shell.Models.StartAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.PauseDownload:
                await shell.Models.PauseAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.CancelDownload:
                await shell.Models.CancelAsync(Payload(r, AiHostJsonContext.Default.DownloadCommand)!.CatalogueId, ct).ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.RemoveModel:
                await shell.Models.RemoveAsync(Payload(r, AiHostJsonContext.Default.RemoveModelCommand)!.CatalogueId, ct).ConfigureAwait(false);
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
                var tryIt = Payload(r, AiHostJsonContext.Default.TryItRequest)!;
                var answer = await runtime.TryItChatAsync(tryIt.Request, ct).ConfigureAwait(false);
                return Ok(id, answer, AiHostJsonContext.Default.ChatCompletionResponse);

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
        shell.Settings.Save(next);
        var folderChanged = !string.Equals(old.ModelsFolder ?? "", next.ModelsFolder ?? "", StringComparison.OrdinalIgnoreCase);
        var restart = folderChanged || old.MaxMemoryPercent != next.MaxMemoryPercent
            || !string.Equals(old.Backend, next.Backend, StringComparison.Ordinal);
        return new SettingsSaved
        {
            ModelsFolderChanged = folderChanged,
            AppliesAtRestart = restart,
            Note = folderChanged
                ? "New downloads go to the new folder after the host restarts. Models already downloaded are not moved or deleted."
                : null,
        };
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
