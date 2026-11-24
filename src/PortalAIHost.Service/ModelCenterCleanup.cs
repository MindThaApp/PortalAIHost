using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>
/// "Free up space": a read-only scan of what the host itself left behind, and the removal of the items the user
/// ticked. Only the store decides what is removable (see <see cref="IModelStoreCleaner"/>); this class adds the
/// models a check marked damaged and refuses while a move, check, clean or download is running.
/// </summary>
internal sealed partial class ModelCenter
{
    /// <summary>An unfinished download younger than this is kept so it can still resume.</summary>
    public static readonly TimeSpan PartialKeepAge = TimeSpan.FromDays(7);

    private const string DamagedPrefix = "damaged|";

    private string? CleanupBusyReason()
    {
        lock (_opSync)
        {
            if (_op is not null)
            {
                return _opName == StoreOperations.Move ? "Your models are being moved. Wait until that finishes."
                    : _opName == StoreOperations.Cleanup ? "Space is already being freed up."
                    : "A model is being checked. Wait until that finishes.";
            }
        }

        return _running.IsEmpty ? null : "A download is running. Pause it first, then free up space.";
    }

    /// <summary>Reads only: what could be removed, and its size.</summary>
    public async Task<CleanupScan> ScanCleanupAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        }

        if (CleanupBusyReason() is { } busy)
        {
            return new CleanupScan { BusyReason = busy };
        }

        var items = await CollectCleanupAsync(ct).ConfigureAwait(false);
        return new CleanupScan { Items = items, TotalBytes = items.Sum(i => i.Bytes) };
    }

    private async Task<List<CleanupItemInfo>> CollectCleanupAsync(CancellationToken ct)
    {
        var items = new List<CleanupItemInfo>();
        if (_store is IModelStoreCleaner cleaner)
        {
            foreach (var c in await cleaner.ScanLeftoversAsync(PartialKeepAge, ct).ConfigureAwait(false))
            {
                items.Add(new CleanupItemInfo
                {
                    Id = c.Id, Label = c.Label, Detail = c.Detail, Bytes = c.Bytes, FileCount = c.FileCount,
                    Kind = c.Kind == CleanupKind.OldCopies ? CleanupKinds.OldCopies : CleanupKinds.Partial,
                });
            }
        }

        items.AddRange(await DamagedCandidatesAsync(ct).ConfigureAwait(false));
        return items;
    }

    /// <summary>Host-downloaded, complete models a check marked damaged that are not the active, loaded or downloading model.</summary>
    private async Task<List<CleanupItemInfo>> DamagedCandidatesAsync(CancellationToken ct)
    {
        var list = new List<CleanupItemInfo>();
        if (_store is not IModelStoreRepair || Hooks.Notes is not { } notes)
        {
            return list;
        }

        foreach (var m in await _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (m.Origin != ModelOrigin.Catalogue || !m.IsComplete || notes.DamagedReason(m.Id) is not { } reason
                || _activeId(m.Kind) == m.Id || _isLoaded(m.Id) || _running.ContainsKey(m.Id))
            {
                continue;
            }

            list.Add(new CleanupItemInfo
            {
                Id = $"{DamagedPrefix}{m.Id}|{m.TotalBytes}", Kind = CleanupKinds.Damaged, Label = m.DisplayName,
                Detail = reason, Bytes = m.TotalBytes, FileCount = m.Files.Count,
            });
        }

        return list;
    }

    /// <summary>
    /// Removes the ticked items. The store and the damaged-model list are asked again for each item right before it is
    /// deleted; an item that changed, came into use or is no longer a candidate is skipped and reported.
    /// </summary>
    public async Task<CleanupResult> CleanupAsync(IReadOnlyCollection<string> itemIds, CancellationToken ct)
    {
        if (_store is null)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        }

        if (CleanupBusyReason() is { } busy)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, busy);
        }

        var cts = BeginOperation(StoreOperations.Cleanup);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            var token = linked.Token;
            var previous = (_store as IModelStoreMover)?.PreviousLocation;
            long freed = 0;
            var removed = new List<string>();
            var skipped = new List<CleanupSkipped>();

            var storeIds = itemIds.Where(i => !i.StartsWith(DamagedPrefix, StringComparison.Ordinal)).ToList();
            if (storeIds.Count > 0 && _store is IModelStoreCleaner cleaner)
            {
                var outcome = await cleaner.CleanLeftoversAsync(storeIds, PartialKeepAge, token).ConfigureAwait(false);
                freed += outcome.FreedBytes;
                removed.AddRange(outcome.Removed);
                skipped.AddRange(outcome.Skipped.Select(s => new CleanupSkipped { Label = s.Label, Reason = s.Reason }));
            }

            var damagedIds = itemIds.Where(i => i.StartsWith(DamagedPrefix, StringComparison.Ordinal)).ToList();
            if (damagedIds.Count > 0)
            {
                var fresh = (await DamagedCandidatesAsync(token).ConfigureAwait(false)).ToDictionary(i => i.Id, StringComparer.Ordinal);
                foreach (var id in damagedIds)
                {
                    if (!fresh.TryGetValue(id, out var item))
                    {
                        skipped.Add(new CleanupSkipped { Label = id.Split('|')[1], Reason = "It changed or came into use since it was listed, so it was left alone." });
                        continue;
                    }

                    var modelId = id.Split('|')[1];
                    try
                    {
                        await ((IModelStoreRepair)_store).RemoveDamagedAsync(modelId, token).ConfigureAwait(false);
                        Hooks.Notes?.ClearDamaged(modelId, forgetUsage: true);
                        _lastState.TryRemove(modelId, out _);
                        freed += item.Bytes;
                        removed.Add(item.Label);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
                    {
                        skipped.Add(new CleanupSkipped { Label = item.Label, Reason = ex.Message });
                    }
                }
            }

            _lastStored = null;
            if (previous is not null && (_store as IModelStoreMover)?.PreviousLocation is null)
            {
                Hooks.ForgetFolder?.Invoke(previous); // the old folder's copies are gone: it is no longer an "earlier folder"
            }

            return new CleanupResult { FreedBytes = freed, Removed = removed, Skipped = skipped };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Freeing up space stopped: " + ex.Message);
        }
        finally
        {
            EndOperation(cts);
        }
    }
}
