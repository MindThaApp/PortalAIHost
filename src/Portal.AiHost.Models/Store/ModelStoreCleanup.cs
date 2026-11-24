using System.Text.Json;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

public enum CleanupKind
{
    /// <summary>A recorded, unfinished download (.part file and its resume sidecar) that nothing is resuming.</summary>
    PartialDownload,

    /// <summary>The old store's copies of the models after a move whose new copies are recorded as verified.</summary>
    OldCopies,
}

/// <summary>
/// One thing the host created and could remove. <see cref="Id"/> is opaque: it carries the identity (file ID, size,
/// time stamp) seen by the scan, and a clean only acts on an item whose id a fresh scan still produces unchanged.
/// </summary>
public sealed record CleanupCandidate(string Id, CleanupKind Kind, string Label, long Bytes, int FileCount, string Detail);

public sealed record CleanupSkip(string Label, string Reason);

public sealed record CleanupOutcome(long FreedBytes, IReadOnlyList<string> Removed, IReadOnlyList<CleanupSkip> Skipped);

/// <summary>"Free up space": reads what the host itself left behind, and removes the items the caller confirmed.</summary>
public interface IModelStoreCleaner : IModelStore
{
    /// <summary>Reads only. Partial downloads count only when older than <paramref name="minPartialAge"/>.</summary>
    Task<IReadOnlyList<CleanupCandidate>> ScanLeftoversAsync(TimeSpan minPartialAge, CancellationToken ct);

    /// <summary>
    /// Re-scans, then removes only the items in <paramref name="ids"/> that the fresh scan still produces with the same
    /// id (so a file that changed, was replaced or came into use since the first scan is skipped), file by file.
    /// </summary>
    Task<CleanupOutcome> CleanLeftoversAsync(IReadOnlyCollection<string> ids, TimeSpan minPartialAge, CancellationToken ct);
}

public sealed partial class ModelStore : IModelStoreCleaner
{
    private sealed record PartHit(CleanupCandidate Item, ManifestModel Model, ManifestFile File, long PartLength, long SidecarLength, string FileId);

    private sealed record OldHit(CleanupCandidate Item, List<(string Path, long Size)> Files);

    public async Task<IReadOnlyList<CleanupCandidate>> ScanLeftoversAsync(TimeSpan minPartialAge, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            var list = FindPartials(minPartialAge).Select(h => h.Item).ToList();
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
            var skipped = new List<CleanupSkip>();
            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);

            var parts = FindPartials(minPartialAge);
            var old = FindOldCopies();
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var hit in parts.Where(h => wanted.Contains(h.Item.Id)))
            {
                matched.Add(hit.Item.Id);
                ct.ThrowIfCancellationRequested();
                try
                {
                    var partRel = hit.File.PartPath!;
                    _folder.AssertDeletable(partRel, hit.PartLength, hit.FileId);
                    _folder.AssertDeletable(partRel + ".json", hit.SidecarLength > 0 ? hit.SidecarLength : null);
                    _folder.DeleteOwnedFile(partRel, hit.PartLength, hit.FileId);
                    _folder.DeleteOwnedFile(partRel + ".json", hit.SidecarLength > 0 ? hit.SidecarLength : null);
                    hit.Model.Files.Remove(hit.File);
                    if (hit.Model.Files.Count == 0 && hit.Model.Origin == ModelOrigin.Catalogue)
                    {
                        _manifest.Models.Remove(hit.Model);
                        _folder.TryRemoveEmptyFolder(hit.Model.Folder!);
                    }

                    SaveManifest();
                    freed += hit.Item.Bytes;
                    removed.Add(hit.Item.Label);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

            return new CleanupOutcome(freed, removed, skipped);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string LabelOf(string id)
    {
        var p = id.Split('|');
        return p.Length > 2 && p[0] == "part" ? Path.GetFileName(p[2]) : "Old copies";
    }

    /// <summary>Recorded .part files the host created (file ID matches), not being downloaded, and older than the age limit.</summary>
    private List<PartHit> FindPartials(TimeSpan minAge)
    {
        var hits = new List<PartHit>();
        var now = DateTime.UtcNow;
        foreach (var m in _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue))
        {
            if (IsDownloading(m.Id))
            {
                continue;
            }

            foreach (var f in m.Files.Where(f => f.State == StoredFileState.Partial && f.PartPath is not null && f.PartFileId is not null))
            {
                try
                {
                    var partRel = f.PartPath!;
                    var full = _folder.Resolve(partRel);
                    var info = new FileInfo(full);
                    if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var (links, id) = _folder.GetIdentity(partRel);
                    if (links != 1 || !string.Equals(id, f.PartFileId, StringComparison.Ordinal))
                    {
                        continue; // not the file the host created
                    }

                    var newest = info.LastWriteTimeUtc;
                    long sidecarLen = 0;
                    var sidecar = new FileInfo(_folder.Resolve(partRel + ".json"));
                    if (sidecar.Exists)
                    {
                        if (sidecar.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        sidecarLen = sidecar.Length;
                        newest = newest > sidecar.LastWriteTimeUtc ? newest : sidecar.LastWriteTimeUtc;
                    }

                    if (now - newest < minAge)
                    {
                        continue; // recent enough to resume: kept
                    }

                    var itemId = $"part|{m.Id}|{partRel}|{id}|{info.Length}|{sidecarLen}|{info.LastWriteTimeUtc.Ticks}";
                    var label = f.Name;
                    var days = (int)(now - newest).TotalDays;
                    hits.Add(new PartHit(
                        new CleanupCandidate(itemId, CleanupKind.PartialDownload, label, info.Length + sidecarLen, 1,
                            $"Unfinished download, untouched for {days} days"),
                        m, f, info.Length, sidecarLen, id!));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file that cannot be checked is never a candidate.
                }
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
