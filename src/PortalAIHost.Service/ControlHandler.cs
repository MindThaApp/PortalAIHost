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
                var listing = new ModelListing();
                if (shell.Store is { } store)
                {
                    foreach (var m in await store.ListAsync(ct).ConfigureAwait(false))
                    {
                        listing.Models.Add(new CatalogueModelSummary
                        {
                            Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine,
                            SizeBytes = m.TotalBytes, Installed = m.IsComplete,
                            Active = runtime.GetActive(m.Kind)?.ModelId == m.Id, FitsThisPc = true,
                        });
                    }
                }

                return Ok(id, listing, AiHostJsonContext.Default.ModelListing);

            case ControlTypes.ActivateModel:
                await shell.ActivateAsync(Payload(r, AiHostJsonContext.Default.ActivateModelCommand)!.CatalogueId, ct)
                    .ConfigureAwait(false);
                return ControlPipe.Ok(id);

            case ControlTypes.StartDownload or ControlTypes.PauseDownload or ControlTypes.CancelDownload:
                return ControlPipe.Error(id, ErrorCodes.FeatureDisabled, "Downloads are not available in this build yet.");

            case ControlTypes.GetSettings:
                return Ok(id, shell.Settings.Current, AiHostJsonContext.Default.HostSettings);

            case ControlTypes.SetSettings:
                // Saved only: memory cap, backend and keep-loaded take effect at the next host start (the planner
                // and auto-load read them once at startup). Engines already loaded are not touched.
                shell.Settings.Save(Payload(r, AiHostJsonContext.Default.HostSettings)!);
                return ControlPipe.Ok(id);

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

    internal static PendingApproval ToPending(AccessRequestInfo info) => new()
    {
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
