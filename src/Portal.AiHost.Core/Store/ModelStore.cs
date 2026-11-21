using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

/// <summary>Writer side of the store used by the downloader. All writes stay inside the host's own files.</summary>
public interface IModelStoreWriter : IModelStore
{
    /// <summary>Creates or updates the model record before its files are downloaded.</summary>
    Task EnsureModelAsync(string modelId, string displayName, string kind, string engine, CancellationToken ct);

    /// <summary>Deletes one recorded .part file and its resume sidecar, and forgets the record.</summary>
    Task DiscardPartAsync(string modelId, string partPath, CancellationToken ct);

    /// <summary>Truncates one recorded .part file to zero (restart from scratch) and drops its sidecar.</summary>
    Task ResetPartAsync(string modelId, string partPath, CancellationToken ct);

    Task<string?> ReadResumeStateAsync(string modelId, string partPath, CancellationToken ct);

    Task WriteResumeStateAsync(string modelId, string partPath, string json, CancellationToken ct);
}

internal sealed class StoreManifest
{
    public int Version { get; init; } = 1;
    public Guid StoreId { get; init; }
    public List<ManifestModel> Models { get; init; } = new();

    /// <summary>Set after a move: the old store, whose copies the user may remove later.</summary>
    public PreviousStore? PreviousStore { get; set; }
}

internal sealed class PreviousStore
{
    public string Path { get; init; } = "";
    public Guid StoreId { get; init; }
}

internal sealed class ManifestModel
{
    public string Id { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Engine { get; set; } = "";
    public ModelOrigin Origin { get; init; }

    /// <summary>Store-relative folder the host created for this model (catalogue models only).</summary>
    public string? Folder { get; init; }

    public List<ManifestFile> Files { get; init; } = new();
}

internal sealed class ManifestFile
{
    /// <summary>Catalogue file name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Final path: store-relative for catalogue files, absolute for external files.</summary>
    public string Path { get; set; } = "";

    /// <summary>Store-relative .part path while downloading; its sidecar is the same path plus ".json".</summary>
    public string? PartPath { get; set; }

    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public StoredFileState State { get; set; }
}

/// <summary>
/// The host's model inventory (store.json inside a folder marked with <c>.portal-ai-host-store</c>).
/// Destructive rules: a file is only deleted, truncated or replaced when it is listed in store.json and lives
/// inside the marked folder whose GUID matches (see <see cref="OwnedFolder"/>). External models are never
/// touched. Moves copy, verify by re-hashing the copy, switch, and leave the old copies until
/// <see cref="RemoveOldCopiesAsync"/> is called.
/// </summary>
public sealed partial class ModelStore : IModelStoreWriter
{
    public const string UserFolderName = "Portal AI Host Models";

    private const long Fat32MaxFile = 4L * 1024 * 1024 * 1024 - 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDiskInfo _disk;
    private OwnedFolder _folder;
    private StoreManifest _manifest;

    private ModelStore(OwnedFolder folder, StoreManifest manifest, IDiskInfo disk)
    {
        _folder = folder;
        _manifest = manifest;
        _disk = disk;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Opens the default store (LocalState\models): creates and marks it when missing, opens it when it carries
    /// a marker, and refuses an existing unmarked folder.
    /// </summary>
    public static ModelStore OpenOrCreate(string storeFolder, IDiskInfo? disk = null)
    {
        disk ??= SystemDiskInfo.Instance;
        var root = OwnedFolder.Normalize(storeFolder);
        if (!Directory.Exists(root))
        {
            return CreateFresh(root, disk);
        }

        return Open(root, disk);
    }

    /// <summary>Opens an existing marked store. An unmarked folder is refused.</summary>
    public static ModelStore Open(string storeFolder, IDiskInfo? disk = null)
    {
        disk ??= SystemDiskInfo.Instance;
        var root = OwnedFolder.Normalize(storeFolder);
        var id = OwnedFolder.ReadMarker(root)
            ?? throw new StoreRefusedException($"'{root}' is not a Portal AI Host store (no marker); it was not used.");
        var folder = new OwnedFolder(root, id);
        StoreManifest manifest;
        if (File.Exists(folder.ManifestPath))
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllBytes(folder.ManifestPath), StoreJsonContext.Default.StoreManifest)
                ?? throw new StoreRefusedException("store.json is empty.");
            if (manifest.StoreId != id)
            {
                throw new StoreRefusedException("store.json belongs to a different store than the marker.");
            }
        }
        else
        {
            manifest = new StoreManifest { StoreId = id };
        }

        return new ModelStore(folder, manifest, disk);
    }

    /// <summary>
    /// Creates a new store in a user-chosen parent folder: always a new subfolder "Portal AI Host Models"
    /// (or "Portal AI Host Models (2)" ...), never an existing folder. FAT32 is refused.
    /// </summary>
    public static ModelStore CreateInParent(string parentFolder, IDiskInfo? disk = null)
    {
        disk ??= SystemDiskInfo.Instance;
        var root = PickNewStoreFolder(parentFolder, disk);
        return CreateFresh(root, disk);
    }

    private static string PickNewStoreFolder(string parentFolder, IDiskInfo disk)
    {
        var parent = OwnedFolder.Normalize(parentFolder);
        if (!Directory.Exists(parent))
        {
            throw new StoreRefusedException($"'{parent}' does not exist.");
        }

        var fs = disk.GetFileSystem(parent);
        if (fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) && !fs.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreRefusedException($"'{parent}' is {fs}, which cannot hold files over 4 GB. Use an NTFS or exFAT drive.");
        }

        return UniquePath(Path.Combine(parent, UserFolderName));
    }

    private static ModelStore CreateFresh(string root, IDiskInfo disk)
    {
        var folder = OwnedFolder.CreateNew(root);
        var store = new ModelStore(folder, new StoreManifest { StoreId = folder.StoreId }, disk);
        store.SaveManifest();
        return store;
    }

    public StoreLocation Location =>
        new(_folder.Root, _folder.StoreId, _disk.GetFreeBytes(_folder.Root), _disk.GetFileSystem(_folder.Root));

    /// <summary>Folder of the previous store after a move, until its copies are removed.</summary>
    public string? PreviousLocation => _manifest.PreviousStore?.Path;

    public async Task<IReadOnlyList<StoredModel>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _manifest.Models.Select(ToStored).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredModel?> GetAsync(string modelId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var m = Find(modelId);
            return m is null ? null : ToStored(m);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task EnsureModelAsync(string modelId, string displayName, string kind, string engine, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var m = GetOrCreateCatalogueModel(modelId);
            m.DisplayName = displayName;
            m.Kind = kind;
            m.Engine = engine;
            SaveManifest();
        });

    public Task<string> BeginFileAsync(string modelId, string fileName, CancellationToken ct) =>
        Locked(ct, () =>
        {
            if (!SafeFileName().IsMatch(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            {
                throw new StoreSafetyException($"'{fileName}' is not a safe model file name.");
            }

            var m = GetOrCreateCatalogueModel(modelId);
            var existing = m.Files.FirstOrDefault(f => f.Name == fileName);
            if (existing is { State: StoredFileState.Partial, PartPath: { } partRel })
            {
                // Resume: hand back the host's own .part file (re-created empty if it vanished).
                var partFull = _folder.Resolve(partRel);
                if (!File.Exists(partFull))
                {
                    CreateEmptyFile(partFull);
                }

                return partFull;
            }

            if (existing is not null)
            {
                throw new InvalidOperationException($"'{fileName}' is already stored for '{modelId}'.");
            }

            _folder.AssertMarker();
            var folderFull = _folder.Resolve(m.Folder!);
            Directory.CreateDirectory(folderFull);
            var finalFull = UniqueFilePath(folderFull, fileName);
            var partFullNew = finalFull + ".part";
            CreateEmptyFile(partFullNew);
            m.Files.Add(new ManifestFile
            {
                Name = fileName,
                Path = _folder.ToRelative(finalFull),
                PartPath = _folder.ToRelative(partFullNew),
                State = StoredFileState.Partial,
            });
            SaveManifest();
            return partFullNew;
        });

    public Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            _folder.AssertMarker();
            var partFull = _folder.Resolve(f.PartPath!);
            var finalFull = _folder.Resolve(f.Path);
            if (File.Exists(finalFull) || Directory.Exists(finalFull))
            {
                // Something appeared at the final name: never replace it, pick a new name instead.
                finalFull = UniqueFilePath(Path.GetDirectoryName(finalFull)!, Path.GetFileName(finalFull));
                f.Path = _folder.ToRelative(finalFull);
            }

            File.Move(partFull, finalFull, overwrite: false);
            _folder.DeleteOwnedFile(f.PartPath! + ".json");
            f.PartPath = null;
            f.Size = new FileInfo(finalFull).Length;
            f.Sha256 = sha256.ToLowerInvariant();
            f.State = StoredFileState.Verified;
            SaveManifest();
            return ToStoredFile(f, ModelOrigin.Catalogue);
        });

    public Task DiscardPartAsync(string modelId, string partPath, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (m, f) = FindPart(modelId, partPath);
            _folder.DeleteOwnedFile(f.PartPath!);
            _folder.DeleteOwnedFile(f.PartPath! + ".json");
            m.Files.Remove(f);
            if (m.Files.Count == 0)
            {
                _manifest.Models.Remove(m);
                _folder.TryRemoveEmptyFolder(m.Folder!);
            }

            SaveManifest();
        });

    public Task ResetPartAsync(string modelId, string partPath, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            _folder.AssertMarker();
            var full = _folder.Resolve(f.PartPath!);
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(0);
            }

            _folder.DeleteOwnedFile(f.PartPath! + ".json");
        });

    public Task<string?> ReadResumeStateAsync(string modelId, string partPath, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            var sidecar = _folder.Resolve(f.PartPath! + ".json");
            return File.Exists(sidecar) ? File.ReadAllText(sidecar) : null;
        });

    public Task WriteResumeStateAsync(string modelId, string partPath, string json, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            _folder.ReplaceOwnedFile(f.PartPath! + ".json", System.Text.Encoding.UTF8.GetBytes(json));
        });

    public Task SetInUseAsync(string modelId, bool inUse, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var m = Find(modelId) ?? throw new KeyNotFoundException(modelId);
            if (inUse && m.Files.Any(f => f.State == StoredFileState.Partial))
            {
                throw new InvalidOperationException($"'{modelId}' is not completely downloaded.");
            }

            foreach (var f in m.Files)
            {
                f.State = inUse ? StoredFileState.InUse : StoredFileState.Verified;
            }

            SaveManifest();
        });

    public Task<StoredModel> AddExternalAsync(string filePath, string kind, string engine, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var full = Path.GetFullPath(filePath);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Model file not found.", full);
            }

            if (_folder.IsStrictlyInside(full) || string.Equals(OwnedFolder.Normalize(full), _folder.Root, PathComparison))
            {
                throw new StoreSafetyException("Files inside the host's own store cannot be added as external models.");
            }

            var existing = _manifest.Models.FirstOrDefault(m =>
                m.Origin == ModelOrigin.External && m.Files.Any(f => string.Equals(f.Path, full, PathComparison)));
            if (existing is not null)
            {
                return ToStored(existing);
            }

            var stem = SafeIdChars().Replace(Path.GetFileNameWithoutExtension(full).ToLowerInvariant(), "-").Trim('-');
            var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..8];
            var model = new ManifestModel
            {
                Id = $"external-{(stem.Length > 40 ? stem[..40] : stem)}-{hash}",
                DisplayName = Path.GetFileNameWithoutExtension(full),
                Kind = kind,
                Engine = engine,
                Origin = ModelOrigin.External,
                Files = { new ManifestFile { Name = info.Name, Path = full, Size = info.Length, State = StoredFileState.Verified } },
            };
            _manifest.Models.Add(model);
            SaveManifest();
            return ToStored(model);
        });

    public Task RemoveAsync(string modelId, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var m = Find(modelId) ?? throw new KeyNotFoundException(modelId);
            if (m.Files.Any(f => f.State == StoredFileState.InUse))
            {
                throw new InvalidOperationException($"'{modelId}' is in use; stop the engine first.");
            }

            if (m.Origin == ModelOrigin.External)
            {
                // Used in place: removing only forgets it. The user's file is never touched.
                _manifest.Models.Remove(m);
                SaveManifest();
                return;
            }

            // Check every file first, so a refusal leaves the whole model untouched.
            foreach (var f in m.Files)
            {
                _folder.AssertDeletable(f.Path, f.State == StoredFileState.Partial ? null : f.Size);
                if (f.PartPath is { } part)
                {
                    _folder.AssertDeletable(part);
                    _folder.AssertDeletable(part + ".json");
                }
            }

            foreach (var f in m.Files.ToList())
            {
                if (f.State != StoredFileState.Partial)
                {
                    _folder.DeleteOwnedFile(f.Path, f.Size);
                }

                if (f.PartPath is { } part)
                {
                    _folder.DeleteOwnedFile(part);
                    _folder.DeleteOwnedFile(part + ".json");
                }

                m.Files.Remove(f);
                SaveManifest();
            }

            _manifest.Models.Remove(m);
            _folder.TryRemoveEmptyFolder(m.Folder!);
            SaveManifest();
        });

    public async Task MoveStoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MoveCoreAsync(newParentFolder, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MoveCoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct)
    {
        if (_manifest.PreviousStore is not null)
        {
            throw new InvalidOperationException("Remove or keep the copies of the previous move first.");
        }

        var files = _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue).SelectMany(m => m.Files).ToList();
        if (files.Any(f => f.State != StoredFileState.Verified))
        {
            throw new InvalidOperationException("Pause downloads and stop the engine before moving the store.");
        }

        var total = files.Sum(f => f.Size);
        var root = PickNewStoreFolder(newParentFolder, _disk);
        var needed = total + (long)(total * 0.02) + (2L << 30);
        var free = _disk.GetFreeBytes(Path.GetDirectoryName(root)!);
        if (free < needed)
        {
            throw new IOException($"Not enough space: the move needs {needed / 1e9:0.0} GB, the drive has {free / 1e9:0.0} GB free.");
        }

        if (files.Any(f => f.Size > Fat32MaxFile) && _disk.GetFileSystem(root).StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
            && !_disk.GetFileSystem(root).Equals("exFAT", StringComparison.OrdinalIgnoreCase))
        {
            throw new StoreRefusedException("That drive cannot hold files over 4 GB.");
        }

        var target = OwnedFolder.CreateNew(root);
        var newManifest = new StoreManifest
        {
            StoreId = target.StoreId,
            PreviousStore = new PreviousStore { Path = _folder.Root, StoreId = _folder.StoreId },
        };
        var created = new List<string>(); // store-relative paths created in the target, for cleanup on failure
        var createdFolders = new List<string>();
        try
        {
            long done = 0;
            foreach (var m in _manifest.Models)
            {
                var copy = new ManifestModel
                {
                    Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine, Origin = m.Origin, Folder = m.Folder,
                };
                newManifest.Models.Add(copy);
                if (m.Origin == ModelOrigin.External)
                {
                    copy.Files.AddRange(m.Files.Select(f => new ManifestFile
                    {
                        Name = f.Name, Path = f.Path, Size = f.Size, Sha256 = f.Sha256, State = f.State,
                    }));
                    continue;
                }

                Directory.CreateDirectory(target.Resolve(m.Folder!));
                createdFolders.Add(m.Folder!);
                foreach (var f in m.Files)
                {
                    _folder.AssertMarker();
                    var source = _folder.Resolve(f.Path);
                    var dest = target.Resolve(f.Path);
                    var partRel = f.Path + ".part";
                    var part = target.Resolve(partRel);
                    created.Add(partRel);
                    var sourceHash = await CopyNewAsync(source, part, ct).ConfigureAwait(false);
                    var copyHash = await HashFileAsync(part, ct).ConfigureAwait(false);
                    var expected = f.Sha256 ?? sourceHash;
                    if (!string.Equals(copyHash, expected, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(sourceHash, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"'{f.Name}' did not verify after copying; the move was undone.");
                    }

                    File.Move(part, dest, overwrite: false);
                    created.Remove(partRel);
                    created.Add(f.Path);
                    copy.Files.Add(new ManifestFile
                    {
                        Name = f.Name, Path = f.Path, Size = f.Size, Sha256 = expected, State = StoredFileState.Verified,
                    });
                    done += f.Size;
                    progress?.Report(total == 0 ? 1 : (double)done / total);
                }
            }

            target.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(newManifest, StoreJsonContext.Default.StoreManifest));
        }
        catch
        {
            // Undo only what this move created inside the new, freshly marked folder.
            foreach (var rel in created)
            {
                target.DeleteOwnedFile(rel);
            }

            foreach (var rel in createdFolders)
            {
                target.TryRemoveEmptyFolder(rel);
            }

            target.TryRetireEmptyStore();
            throw;
        }

        _folder = target;
        _manifest = newManifest;
        progress?.Report(1);
    }

    /// <summary>
    /// After a move, deletes the old copies file by file. A file is deleted only when it is listed in the old
    /// store.json, lives in the old marked folder, still has its recorded size, and its copy in the current store is
    /// verified with the same hash. Anything else is left. Returns the paths left in place.
    /// </summary>
    public Task<IReadOnlyList<string>> RemoveOldCopiesAsync(CancellationToken ct) =>
        Locked<IReadOnlyList<string>>(ct, () =>
        {
            var prev = _manifest.PreviousStore ?? throw new InvalidOperationException("There is no previous store.");
            var kept = new List<string>();
            if (OwnedFolder.ReadMarker(prev.Path) != prev.StoreId)
            {
                throw new StoreSafetyException($"The old store '{prev.Path}' no longer carries its marker; nothing was removed.");
            }

            var old = new OwnedFolder(prev.Path, prev.StoreId);
            var oldManifest = File.Exists(old.ManifestPath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(old.ManifestPath), StoreJsonContext.Default.StoreManifest)
                : null;
            if (oldManifest is null || oldManifest.StoreId != prev.StoreId)
            {
                throw new StoreSafetyException("The old store.json is missing or belongs to another store; nothing was removed.");
            }

            foreach (var m in oldManifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue))
            {
                foreach (var f in m.Files.ToList())
                {
                    var current = Find(m.Id)?.Files.FirstOrDefault(c => c.Name == f.Name);
                    var copyOk = current is { State: not StoredFileState.Partial }
                        && f.State != StoredFileState.Partial
                        && string.Equals(current.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase)
                        && current.Size == f.Size
                        && new FileInfo(_folder.Resolve(current.Path)) is { Exists: true } ci && ci.Length == current.Size;
                    if (!copyOk)
                    {
                        kept.Add(Path.Combine(old.Root, f.Path));
                        continue;
                    }

                    try
                    {
                        old.DeleteOwnedFile(f.Path, f.Size);
                        m.Files.Remove(f);
                    }
                    catch (StoreSafetyException)
                    {
                        kept.Add(Path.Combine(old.Root, f.Path));
                    }
                }

                if (m.Files.Count == 0 && m.Folder is not null)
                {
                    old.TryRemoveEmptyFolder(m.Folder);
                }
            }

            oldManifest.Models.RemoveAll(m => m.Origin == ModelOrigin.External || m.Files.Count == 0);
            if (oldManifest.Models.Count == 0)
            {
                old.TryRetireEmptyStore();
            }
            else
            {
                old.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(oldManifest, StoreJsonContext.Default.StoreManifest));
            }

            if (kept.Count == 0)
            {
                _manifest.PreviousStore = null;
                SaveManifest();
            }

            return kept;
        });

    private static async Task<string> CopyNewAsync(string source, string dest, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    internal static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }

    private ManifestModel GetOrCreateCatalogueModel(string modelId)
    {
        if (!SafeFileName().IsMatch(modelId + ".gguf") || modelId.Contains("..", StringComparison.Ordinal))
        {
            throw new StoreSafetyException($"'{modelId}' is not a safe model id.");
        }

        var m = Find(modelId);
        if (m is not null)
        {
            if (m.Origin != ModelOrigin.Catalogue)
            {
                throw new InvalidOperationException($"'{modelId}' is an external model.");
            }

            return m;
        }

        _folder.AssertMarker();
        var folderFull = UniquePath(Path.Combine(_folder.Root, modelId));
        m = new ManifestModel
        {
            Id = modelId, DisplayName = modelId, Origin = ModelOrigin.Catalogue, Folder = _folder.ToRelative(folderFull),
        };
        Directory.CreateDirectory(_folder.Resolve(m.Folder));
        _manifest.Models.Add(m);
        SaveManifest();
        return m;
    }

    private (ManifestModel Model, ManifestFile File) FindPart(string modelId, string partPath)
    {
        var m = Find(modelId) ?? throw new KeyNotFoundException(modelId);
        var rel = _folder.ToRelative(partPath);
        var f = m.Files.FirstOrDefault(x => x.State == StoredFileState.Partial && x.PartPath is not null
            && string.Equals(x.PartPath, rel, PathComparison))
            ?? throw new StoreSafetyException($"'{partPath}' is not a download the host started for '{modelId}'.");
        return (m, f);
    }

    private ManifestModel? Find(string id) => _manifest.Models.FirstOrDefault(m => m.Id == id);

    private void SaveManifest() =>
        _folder.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(_manifest, StoreJsonContext.Default.StoreManifest));

    private static void CreateEmptyFile(string full)
    {
        using var _ = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
    }

    /// <summary>"name.ext", or "name (2).ext" ... when the name, its .part or its sidecar already exists.</summary>
    private static string UniqueFilePath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, i == 1 ? fileName : $"{stem} ({i}){ext}");
            if (!Exists(candidate) && !Exists(candidate + ".part") && !Exists(candidate + ".part.json"))
            {
                return candidate;
            }
        }
    }

    private static string UniquePath(string path)
    {
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? path : $"{path} ({i})";
            if (!Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private StoredModel ToStored(ManifestModel m) =>
        new(m.Id, m.DisplayName, m.Kind, m.Engine, m.Origin, m.Files.Select(f => ToStoredFile(f, m.Origin)).ToList());

    private StoredFile ToStoredFile(ManifestFile f, ModelOrigin origin)
    {
        var path = origin == ModelOrigin.External
            ? f.Path
            : Path.Combine(_folder.Root, f.State == StoredFileState.Partial && f.PartPath is not null ? f.PartPath : f.Path);
        var size = f.State == StoredFileState.Partial && File.Exists(path) ? new FileInfo(path).Length : f.Size;
        return new StoredFile(f.Name, path, size, f.Sha256, f.State);
    }

    private async Task<T> Locked<T>(CancellationToken ct, Func<T> action)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task Locked(CancellationToken ct, Action action)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}\\.(gguf|bin)$")]
    private static partial Regex SafeFileName();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SafeIdChars();
}
