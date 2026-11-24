using System.Text.Json;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

public enum CleanupKind
{
    /// <summary>An unfinished model: a download nothing is resuming, with every file the host created for it.</summary>
    PartialDownload,

    /// <summary>The old store's copies of the models after a move whose new copies are recorded as verified.</summary>
    OldCopies,

    /// <summary>A complete model a check found damaged.</summary>
    DamagedModel,
}

/// <summary>
/// One thing the host created and could remove. <see cref="Id"/> is opaque: it carries the identity (file IDs, sizes,
/// time stamps) seen by the scan, and a clean only acts on an item whose id a fresh scan still produces unchanged.
/// </summary>
public sealed record CleanupCandidate(string Id, CleanupKind Kind, string Label, long Bytes, int FileCount, string Detail);

public sealed record CleanupSkip(string Label, string Reason);

public sealed record CleanupOutcome(long FreedBytes, IReadOnlyList<string> Removed, IReadOnlyList<CleanupSkip> Skipped,
    IReadOnlyList<string> RemovedModelIds);

/// <summary>"Free up space": reads what the host itself left behind, and removes the items the caller confirmed.</summary>
public interface IModelStoreCleaner : IModelStore
{
    /// <summary>Reads only. Unfinished models count only when every partial file is older than <paramref name="minPartialAge"/>.</summary>
    Task<IReadOnlyList<CleanupCandidate>> ScanLeftoversAsync(TimeSpan minPartialAge, CancellationToken ct);

    /// <summary>
    /// Re-scans, then removes only the items in <paramref name="ids"/> that the fresh scan still produces with the same
    /// id (so a file that changed, was replaced or came into use since the first scan is skipped), file by file.
    /// </summary>
    Task<CleanupOutcome> CleanLeftoversAsync(IReadOnlyCollection<string> ids, TimeSpan minPartialAge, CancellationToken ct);

    /// <summary>Reads only. Describes a complete host-downloaded model (its files as they are on disk now) as removable, or null.</summary>
    Task<CleanupCandidate?> DescribeDamagedAsync(string modelId, CancellationToken ct);

    /// <summary>
    /// Removes a damaged model's files as described by <paramref name="expectedId"/>, each with its size and file-ID check.
    /// Files already missing are skipped; anything that changed since the description leaves the whole model in place.
    /// </summary>
    Task<CleanupOutcome> CleanDamagedAsync(string modelId, string expectedId, CancellationToken ct);
}

public sealed partial class ModelStore : IModelStoreCleaner
{
    private sealed record UnfinishedHit(CleanupCandidate Item, ManifestModel Model, List<PartInfo> Parts);

    private sealed record PartInfo(string PartRel, string FileId, long PartLength, long SidecarLength);

    private sealed record OldHit(CleanupCandidate Item, List<(string Path, long Size)> Files);

    private static string NormRel(string? p) => (p ?? "").Replace('/', '\\').Trim('\\');

    /// <summary>A store-relative path that is a plain file name directly inside the model's own folder.</summary>
    private static bool DirectlyInFolder(string rel, ManifestModel m)
    {
        if (string.IsNullOrWhiteSpace(rel) || string.IsNullOrWhiteSpace(m.Folder) || Path.IsPathRooted(rel) || Path.IsPathRooted(m.Folder))
        {
            return false;
        }

        var parts = NormRel(rel).Split('\\');
        var folder = NormRel(m.Folder).Split('\\');
        return parts.Length == folder.Length + 1
            && parts.All(s => s.Length > 0 && s != "." && s != "..")
            && folder.All(s => s.Length > 0 && s != "." && s != "..")
            && parts.Take(folder.Length).SequenceEqual(folder, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<CleanupCandidate>> ScanLeftoversAsync(TimeSpan minPartialAge, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            var list = FindUnfinished(minPartialAge).Select(h => h.Item).ToList();
            if (FindOldCopies() is { } old)
            {
                list.Add(old.Item);
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CleanupOutcome> CleanLeftoversAsync(IReadOnlyCollection<string> ids, TimeSpan minPartialAge, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            var freed = 0L;
            var removed = new List<string>();
            var removedIds = new List<string>();
            var skipped = new List<CleanupSkip>();
            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);

            var unfinished = FindUnfinished(minPartialAge);
            var old = FindOldCopies();
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var hit in unfinished.Where(h => wanted.Contains(h.Item.Id)))
            {
                matched.Add(hit.Item.Id);
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var p in hit.Parts)
                    {
                        _folder.AssertDeletable(p.PartRel, p.PartLength, p.FileId);
                        var sidecarRel = p.PartRel + ".json";
                        if (p.SidecarLength < 0)
                        {
                            if (File.Exists(_folder.Resolve(sidecarRel)))
                            {
                                throw new StoreSafetyException($"A resume file appeared next to '{p.PartRel}' since it was listed; left in place.");
                            }
                        }
                        else
                        {
                            _folder.AssertDeletable(sidecarRel, p.SidecarLength);
                        }
                    }

                    // The whole unfinished model goes through the normal guarded removal: every file with its size and
                    // file-ID check, the folder only when empty, nothing recursive.
                    RemoveCoreLocked(hit.Model.Id, skipIdentity: false);
                    freed += hit.Item.Bytes;
                    removed.Add(hit.Item.Label);
                    removedIds.Add(hit.Model.Id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
                {
                    skipped.Add(new CleanupSkip(hit.Item.Label, ex.Message));
                }
            }

            if (old is not null && wanted.Contains(old.Item.Id))
            {
                matched.Add(old.Item.Id);
                try
                {
                    var left = await RemoveOldCopiesCoreAsync(ct).ConfigureAwait(false);
                    var gone = old.Files.Where(f => !File.Exists(f.Path)).Sum(f => f.Size);
                    freed += gone;
                    if (gone > 0)
                    {
                        removed.Add(old.Item.Label);
                    }

                    foreach (var path in left)
                    {
                        skipped.Add(new CleanupSkip(Path.GetFileName(path), "Left in place: its new copy could not be confirmed, or the file is in use."));
                    }
                }
                catch (StoreLockedException)
                {
                    skipped.Add(new CleanupSkip(old.Item.Label, "Another program has the old models folder open."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    skipped.Add(new CleanupSkip(old.Item.Label, ex.Message));
                }
            }

            foreach (var id in wanted.Where(i => !matched.Contains(i) && !i.StartsWith("damaged|", StringComparison.Ordinal)))
            {
                skipped.Add(new CleanupSkip(LabelOf(id), "It changed or came into use since it was listed, so it was left alone."));
            }

            return new CleanupOutcome(freed, removed, skipped, removedIds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string LabelOf(string id)
    {
        var p = id.Split('|');
        return p.Length > 1 && p[0] == "unf" ? p[1] : "Old copies";
    }

    // ---- damaged models --------------------------------------------------------------------------

    /// <summary>The on-disk state of a complete catalogue model's files, or null when the model is not removable this way.</summary>
    private (CleanupCandidate Item, ManifestModel Model, List<(ManifestFile File, long Length, string? Id)> Present)? DescribeDamagedLocked(string modelId)
    {
        var m = Find(modelId);
        if (m is not { Origin: ModelOrigin.Catalogue } || IsDownloading(modelId) || m.Files.Count == 0
            || m.Files.Any(f => f.State != StoredFileState.Verified) || !HasUniqueSafePaths(m))
        {
            return null;
        }

        try
        {
            var present = new List<(ManifestFile File, long Length, string? Id)>();
            var parts = new List<string>();
            foreach (var f in m.Files)
            {
                var info = new FileInfo(_folder.Resolve(f.Path));
                if (Directory.Exists(info.FullName) || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                {
                    return null;
                }

                if (!info.Exists)
                {
                    parts.Add($"{f.Path}:missing");
                    continue;
                }

                var (links, id) = _folder.GetIdentity(f.Path);
                // Only the very file the host stored is ever removed: a file the user put at that name has another ID (or none is recorded).
                if (links != 1 || id is null || f.FileId is null || !string.Equals(id, f.FileId, StringComparison.Ordinal))
                {
                    return null;
                }

                present.Add((f, info.Length, id));
                parts.Add($"{f.Path}:{info.Length}:{id}");
            }

            var item = new CleanupCandidate($"damaged|{m.Id}|{string.Join(";", parts)}", CleanupKind.DamagedModel, m.DisplayName,
                present.Sum(p => p.Length), present.Count, "A check found this model damaged");
            return (item, m, present);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<CleanupCandidate?> DescribeDamagedAsync(string modelId, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            return DescribeDamagedLocked(modelId)?.Item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CleanupOutcome> CleanDamagedAsync(string modelId, string expectedId, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DescribeDamagedLocked(modelId);
            if (now is not { } d || !string.Equals(d.Item.Id, expectedId, StringComparison.Ordinal))
            {
                return new CleanupOutcome(0, [], [new CleanupSkip(now?.Item.Label ?? modelId, "It changed or came into use since it was listed, so it was left alone.")], []);
            }

            try
            {
                foreach (var (f, len, id) in d.Present)
                {
                    _folder.AssertDeletable(f.Path, len, id);
                }

                foreach (var (f, len, id) in d.Present)
                {
                    _folder.DeleteOwnedFile(f.Path, len, id);
                }

                _manifest.Models.Remove(d.Model);
                SaveManifest();
                try
                {
                    _folder.TryRemoveEmptyFolder(d.Model.Folder!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                return new CleanupOutcome(d.Item.Bytes, [d.Item.Label], [], [modelId]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CleanupOutcome(0, [], [new CleanupSkip(d.Item.Label, ex.Message)], []);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- unfinished models -----------------------------------------------------------------------

    /// <summary>
    /// Every file path of the model sits directly in its own folder and is unique in the whole manifest, and no recorded
    /// file ID is shared (a tampered store.json could otherwise point at another model's file, store.json or the marker).
    /// </summary>
    private bool HasUniqueSafePaths(ManifestModel m)
    {
        foreach (var f in m.Files)
        {
            if (!DirectlyInFolder(f.Path, m))
            {
                return false;
            }

            if (f.PartPath is { } part
                && (!part.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !DirectlyInFolder(part, m) || !DirectlyInFolder(part + ".json", m)))
            {
                return false;
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string path) => counts[NormRel(path)] = counts.GetValueOrDefault(NormRel(path)) + 1;
        void CountId(string? id)
        {
            if (id is not null)
            {
                idCounts[id] = idCounts.GetValueOrDefault(id) + 1;
            }
        }

        foreach (var f in _manifest.Models.SelectMany(x => x.Files))
        {
            Count(f.Path);
            if (f.PartPath is { } p)
            {
                Count(p);
                Count(p + ".json");
            }

            CountId(f.FileId); // a committed file keeps the ID of the part it was renamed from: counted once per file
            if (f.PartFileId != f.FileId)
            {
                CountId(f.PartFileId);
            }
        }

        foreach (var f in m.Files)
        {
            if (counts[NormRel(f.Path)] != 1
                || (f.PartPath is { } p && (counts[NormRel(p)] != 1 || counts[NormRel(p + ".json")] != 1))
                || (f.FileId is not null && idCounts[f.FileId] != 1)
                || (f.PartFileId is not null && idCounts[f.PartFileId] != 1))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Models with an unfinished download that nothing is resuming: every partial file is a .part the host created
    /// (file ID matches), nothing is downloading or in use, and each is older than the age limit. The whole model is
    /// one item, so a half model is never left behind.
    /// </summary>
    private List<UnfinishedHit> FindUnfinished(TimeSpan minAge)
    {
        var hits = new List<UnfinishedHit>();
        var now = DateTime.UtcNow;
        foreach (var m in _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue).ToList())
        {
            if (IsDownloading(m.Id) || m.Files.Count == 0 || m.Files.Any(f => f.State == StoredFileState.InUse)
                || !m.Files.Any(f => f.State == StoredFileState.Partial) || !HasUniqueSafePaths(m))
            {
                continue;
            }

            try
            {
                var parts = new List<PartInfo>();
                var segs = new List<string>();
                long bytes = 0;
                var ok = true;
                var youngestDays = double.MaxValue;
                foreach (var f in m.Files)
                {
                    if (f.State == StoredFileState.Partial)
                    {
                        if (f.PartPath is null || f.PartFileId is null)
                        {
                            ok = false;
                            break;
                        }

                        var partRel = f.PartPath;
                        var info = new FileInfo(_folder.Resolve(partRel));
                        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            ok = false;
                            break;
                        }

                        var (links, id) = _folder.GetIdentity(partRel);
                        if (links != 1 || !string.Equals(id, f.PartFileId, StringComparison.Ordinal))
                        {
                            ok = false; // not the file the host created
                            break;
                        }

                        var newest = info.LastWriteTimeUtc;
                        long sidecarLen = -1;
                        var sidecar = new FileInfo(_folder.Resolve(partRel + ".json"));
                        if (sidecar.Exists)
                        {
                            if (sidecar.Attributes.HasFlag(FileAttributes.ReparsePoint) || _folder.GetIdentity(partRel + ".json").Links != 1)
                            {
                                ok = false;
                                break;
                            }

                            sidecarLen = sidecar.Length;
                            newest = newest > sidecar.LastWriteTimeUtc ? newest : sidecar.LastWriteTimeUtc;
                        }

                        if (now - newest < minAge)
                        {
                            ok = false; // recent enough to resume: kept
                            break;
                        }

                        youngestDays = Math.Min(youngestDays, (now - newest).TotalDays);
                        parts.Add(new PartInfo(partRel, id!, info.Length, sidecarLen));
                        segs.Add($"P:{partRel}:{id}:{info.Length}:{sidecarLen}:{info.LastWriteTimeUtc.Ticks}");
                        bytes += info.Length + Math.Max(0, sidecarLen);
                    }
                    else
                    {
                        // A finished file of the same unfinished model (a split model's completed part).
                        var info = new FileInfo(_folder.Resolve(f.Path));
                        if (!info.Exists)
                        {
                            segs.Add($"V:{f.Path}:missing");
                            continue;
                        }

                        var (links, id) = _folder.GetIdentity(f.Path);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || links != 1 || info.Length != f.Size
                            || f.FileId is null || !string.Equals(id, f.FileId, StringComparison.Ordinal))
                        {
                            ok = false;
                            break;
                        }

                        segs.Add($"V:{f.Path}:{info.Length}:{id}");
                        bytes += info.Length;
                    }
                }

                if (!ok || parts.Count == 0)
                {
                    continue;
                }

                hits.Add(new UnfinishedHit(
                    new CleanupCandidate($"unf|{m.Id}|{string.Join(";", segs)}", CleanupKind.PartialDownload, m.DisplayName, bytes, segs.Count,
                        $"Unfinished download, untouched for {(int)youngestDays} days"),
                    m, parts));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A model that cannot be checked is never a candidate.
            }
        }

        return hits;
    }


    /// <summary>
    /// The previous store's files that the old store.json lists and that the current store holds as verified with the
    /// same size and SHA-256. Reads only; the hash of the new copy is checked again by <see cref="RemoveOldCopiesCoreAsync"/>.
    /// </summary>
    private OldHit? FindOldCopies()
    {
        if (_manifest.PreviousStore is not { } prev || prev.StoreId == _folder.StoreId
            || string.Equals(OwnedFolder.Normalize(prev.Path), _folder.Root, PathComparison))
        {
            return null;
        }

        try
        {
            if (OwnedFolder.ReadMarker(prev.Path, _options) != prev.StoreId)
            {
                return null;
            }

            var old = new OwnedFolder(prev.Path, prev.StoreId, _options);
            if (!File.Exists(old.ManifestPath))
            {
                return null;
            }

            var oldManifest = JsonSerializer.Deserialize(File.ReadAllBytes(old.ManifestPath), StoreJsonContext.Default.StoreManifest);
            if (oldManifest is null || oldManifest.StoreId != prev.StoreId)
            {
                return null;
            }

            var files = new List<(string Path, long Size)>();
            foreach (var m in oldManifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue))
            {
                foreach (var f in m.Files.Where(f => f.State != StoredFileState.Partial && f.Sha256 is not null))
                {
                    try
                    {
                        var current = Find(m.Id)?.Files.FirstOrDefault(c => c.Name == f.Name);
                        if (current is null || current.State == StoredFileState.Partial || current.Size != f.Size
                            || !string.Equals(current.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (new FileInfo(_folder.Resolve(current.Path)) is not { Exists: true } copy || copy.Length != current.Size)
                        {
                            continue; // the new copy is missing or the wrong size: the old one is the only good one
                        }

                        var path = old.Resolve(f.Path);
                        var info = new FileInfo(path);
                        if (info.Exists && info.Length == f.Size && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            files.Add((path, f.Size));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            if (files.Count == 0)
            {
                return null;
            }

            var bytes = files.Sum(f => f.Size);
            var item = new CleanupCandidate($"old|{prev.StoreId}|{files.Count}|{bytes}", CleanupKind.OldCopies,
                "Old copies of your models", bytes, files.Count, "Left in the earlier folder after a move; the new copies are recorded as checked");
            return new OldHit(item, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
