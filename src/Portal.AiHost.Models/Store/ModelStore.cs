using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>Shortens a recorded .part file to <paramref name="length"/> (never lengthens it).</summary>
    Task TruncatePartAsync(string modelId, string partPath, long length, CancellationToken ct);

    /// <summary>
    /// Opens a recorded .part file for writing after the ownership, link-count and file-ID checks. Called before
    /// every write session, so a link planted during hours of retries is refused.
    /// </summary>
    Task<FileStream> OpenPartForWriteAsync(string modelId, string partPath, int bufferSize, CancellationToken ct);

    /// <summary>
    /// If a file already sits at the model file's final name (for example after a crash between the rename and the
    /// save of store.json) and has the expected size and SHA-256, records it as verified and returns it.
    /// </summary>
    Task<StoredFile?> TryAdoptExistingAsync(string modelId, string fileName, string sha256, long size, CancellationToken ct);

    /// <summary>Forgets a catalogue model that has no files left and removes its empty folder.</summary>
    Task ForgetIfEmptyAsync(string modelId, CancellationToken ct);

    /// <summary>Marks a model as being downloaded; a second download of the same model is refused.</summary>
    IDisposable AcquireDownload(string modelId);

    Task<string?> ReadResumeStateAsync(string modelId, string partPath, CancellationToken ct);

    Task WriteResumeStateAsync(string modelId, string partPath, string json, CancellationToken ct);
}

/// <summary>The move side of the store: what a move needs, and the removal of the old copies once the new ones verified.</summary>
public interface IModelStoreMover : IModelStore
{
    /// <summary>Free space a move to another drive needs: the models' size plus 2 % plus 2 GB headroom.</summary>
    long MoveBytesNeeded { get; }

    /// <summary>Folder of the previous store after a move, until its copies are removed; otherwise null.</summary>
    string? PreviousLocation { get; }

    /// <summary>Deletes the old copies of the last move that the old store lists and the new store holds verified. Returns what was left.</summary>
    Task<IReadOnlyList<string>> RemoveOldCopiesAsync(CancellationToken ct);

    /// <summary>The plain-language reason a folder cannot receive the models, or null when it can. Reads only.</summary>
    string? CheckMoveTarget(string newParentFolder);

    /// <summary>
    /// Forgets the pointer to a previous store whose folder is gone, unmarked, empty of listed files or without a usable
    /// store.json (so a new move is not blocked forever). Deletes nothing. False when there is nothing stale.
    /// </summary>
    Task<bool> ClearStalePreviousStoreAsync(CancellationToken ct);
}

/// <summary>Removing a model a check found damaged, after the user confirmed.</summary>
public interface IModelStoreRepair : IModelStore
{
    /// <summary>
    /// Like <see cref="IModelStore.RemoveAsync"/> for a model whose file changed on disk (a truncated or altered file):
    /// the size and file-ID checks are skipped; the marker, the strictly-inside, the no-links and the
    /// listed-by-the-store checks stay, and nothing is deleted recursively.
    /// </summary>
    Task RemoveDamagedAsync(string modelId, CancellationToken ct);
}

/// <summary>A failed or cancelled move could not remove everything it had copied; the leftovers are listed in the new folder.</summary>
public sealed class StoreCleanupIncompleteException(string message, string leftoverFolder, Exception inner) : IOException(message, inner)
{
    public string LeftoverFolder { get; } = leftoverFolder;
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

    /// <summary>
    /// True for a model file the user brought in ("Add your own model"): the host holds its own copy in the store.
    /// Additive field: older versions ignore it and treat the model as any stored model.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserAdded { get; set; }

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

    /// <summary>Volume + file index of the .part file when the host created it.</summary>
    public string? PartFileId { get; set; }

    /// <summary>
    /// Volume + file index of the finished file, recorded when it was committed. Remove refuses a file whose ID
    /// changed (replaced by another file of the same size). Null in older inventories: then only size is checked.
    /// </summary>
    public string? FileId { get; set; }

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
public sealed partial class ModelStore : IModelStoreWriter, IModelStoreMover, IModelStoreRepair, IDisposable
{
    public const string UserFolderName = StoreOptions.DefaultFolderName;

    private const long Fat32MaxFile = 4L * 1024 * 1024 * 1024 - 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _downloading = new(StringComparer.Ordinal);
    private readonly IDiskInfo _disk;
    private OwnedFolder _folder;
    private StoreManifest _manifest;

    private readonly StoreOptions _options;

    private FileStream _lock;
    private volatile bool _disposed;

    private ModelStore(OwnedFolder folder, StoreManifest manifest, IDiskInfo disk, FileStream storeLock)
    {
        _lock = storeLock;
        _options = folder.Options;
        _folder = folder;
        _manifest = manifest;
        _disk = disk;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Releases the store's exclusive lock so it can be opened again (by this or another process).</summary>
    public void Dispose()
    {
        _disposed = true;
        _lock.Dispose();
    }

    private async Task EnterGateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(ModelStore));
        }
    }

    /// <summary>
    /// Opens the default store (LocalState\models): creates and marks it when missing, opens it when it carries
    /// a marker, and refuses an existing unmarked folder.
    /// </summary>
    public static ModelStore OpenOrCreate(string storeFolder, IDiskInfo? disk = null, StoreOptions? options = null)
    {
        disk ??= SystemDiskInfo.Instance;
        options = Checked(options);
        var root = OwnedFolder.Normalize(storeFolder);
        if (!Directory.Exists(root))
        {
            return CreateFresh(root, disk, options);
        }

        return Open(root, disk, options);
    }

    /// <summary>Opens an existing marked store. An unmarked folder is refused.</summary>
    public static ModelStore Open(string storeFolder, IDiskInfo? disk = null, StoreOptions? options = null)
    {
        disk ??= SystemDiskInfo.Instance;
        options = Checked(options);
        var root = OwnedFolder.Normalize(storeFolder);
        var id = OwnedFolder.ReadMarker(root, options)
            ?? throw new StoreRefusedException($"'{root}' is not a model store of this app (no matching marker); it was not used.");
        var folder = new OwnedFolder(root, id, options);
        var storeLock = folder.AcquireLock(); // before store.json is read, so nobody changes it meanwhile
        StoreManifest manifest;
        try
        {
            if (File.Exists(folder.ManifestPath))
            {
                try
                {
                    manifest = JsonSerializer.Deserialize(File.ReadAllBytes(folder.ManifestPath), StoreJsonContext.Default.StoreManifest)
                        ?? throw new StoreRefusedException("store.json is empty.");
                }
                catch (JsonException ex)
                {
                    // Never repaired or deleted automatically: the inventory is the only proof of what the host created.
                    throw new StoreRefusedException($"store.json in '{root}' is damaged ({ex.Message}); it was left in place.");
                }
                if (manifest.StoreId != id)
                {
                    throw new StoreRefusedException("store.json belongs to a different store than the marker.");
                }
            }
            else
            {
                manifest = new StoreManifest { StoreId = id };
            }
        }
        catch
        {
            storeLock.Dispose();
            throw;
        }

        return new ModelStore(folder, manifest, disk, storeLock);
    }

    /// <summary>
    /// Creates a new store in a user-chosen parent folder: always a new subfolder "Portal AI Host Models"
    /// (or "Portal AI Host Models (2)" ...), never an existing folder. FAT32 is refused.
    /// </summary>
    public static ModelStore CreateInParent(string parentFolder, IDiskInfo? disk = null, StoreOptions? options = null)
    {
        disk ??= SystemDiskInfo.Instance;
        options = Checked(options);
        var root = PickNewStoreFolder(parentFolder, disk, options);
        return CreateFresh(root, disk, options);
    }

    /// <summary>
    /// The models folder chosen in settings: opens parent\"Portal AI Host Models" when it exists (it must carry the
    /// marker), otherwise creates it with the same checks as <see cref="CreateInParent"/>. Missing parents are
    /// never created, and an existing unmarked folder is refused.
    /// </summary>
    public static ModelStore OpenOrCreateInParent(string parentFolder, IDiskInfo? disk = null, StoreOptions? options = null)
    {
        disk ??= SystemDiskInfo.Instance;
        options = Checked(options);
        var parent = CheckedParent(parentFolder, disk);
        var root = StoreFolderIn(parent, options);
        return Directory.Exists(root) ? Open(root, disk, options) : CreateFresh(root, disk, options);
    }

    /// <summary>The store folder the host uses inside a chosen models folder.</summary>
    public static string StoreFolderIn(string parentFolder, StoreOptions? options = null) =>
        Path.Combine(OwnedFolder.Normalize(parentFolder), (options ?? StoreOptions.Default).FolderName);

    private static StoreOptions Checked(StoreOptions? options)
    {
        options ??= StoreOptions.Default;
        options.Validate();
        return options;
    }

    /// <summary>Plain-language reason a folder cannot hold a new store (missing, FAT32), or null when it can. Reads only.</summary>
    public static string? CheckParentFolder(string parentFolder, IDiskInfo? disk = null)
    {
        try
        {
            CheckedParent(parentFolder, disk ?? SystemDiskInfo.Instance);
            return null;
        }
        catch (StoreRefusedException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"'{parentFolder}' cannot be used: {ex.Message}";
        }
    }

    private static string CheckedParent(string parentFolder, IDiskInfo disk)
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

        return parent;
    }

    private static string PickNewStoreFolder(string parentFolder, IDiskInfo disk, StoreOptions options) =>
        UniquePath(Path.Combine(CheckedParent(parentFolder, disk), options.FolderName));

    private static ModelStore CreateFresh(string root, IDiskInfo disk, StoreOptions options)
    {
        var folder = OwnedFolder.CreateNew(root, options);
        var store = new ModelStore(folder, new StoreManifest { StoreId = folder.StoreId }, disk, folder.AcquireLock());
        try
        {
            store.SaveManifest();
        }
        catch
        {
            store.Dispose();
            throw;
        }

        return store;
    }

    public StoreLocation Location =>
        new(_folder.Root, _folder.StoreId, _disk.GetFreeBytes(_folder.Root), _disk.GetFileSystem(_folder.Root));

    /// <summary>Folder of the previous store after a move, until its copies are removed.</summary>
    public string? PreviousLocation => _manifest.PreviousStore?.Path;

    public async Task<IReadOnlyList<StoredModel>> ListAsync(CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
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
        await EnterGateAsync(ct).ConfigureAwait(false);
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
            AssertSafeFileName(fileName);
            var m = GetOrCreateCatalogueModel(modelId);
            var existing = m.Files.FirstOrDefault(f => f.Name == fileName);
            if (existing is { State: StoredFileState.Partial, PartPath: { } partRel })
            {
                // Resume: hand back the host's own .part file (re-created empty if it vanished).
                var partFull = _folder.Resolve(partRel);
                if (!File.Exists(partFull))
                {
                    if (Exists(_folder.Resolve(existing.Path)))
                    {
                        throw new StoreSafetyException(
                            $"'{existing.Path}' already exists; check it with TryAdoptExistingAsync before downloading again.");
                    }

                    CreateEmptyFile(partFull);
                    existing.PartFileId = _folder.GetIdentity(partRel).Id;
                    SaveManifest();
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
            var finalFull = Path.Combine(folderFull, fileName);
            if (Exists(finalFull))
            {
                // The engine loads model files (and split parts) by name, so no "(2)" names: stop instead.
                throw new StoreSafetyException(
                    $"'{finalFull}' already exists and is not a file the host recorded. Move it away and try again.");
            }

            var partFullNew = UniquePartPath(finalFull);
            CreateEmptyFile(partFullNew);
            var partRelNew = _folder.ToRelative(partFullNew);
            m.Files.Add(new ManifestFile
            {
                Name = fileName,
                Path = _folder.ToRelative(finalFull),
                PartPath = partRelNew,
                PartFileId = _folder.GetIdentity(partRelNew).Id,
                State = StoredFileState.Partial,
            });
            SaveManifest();
            return partFullNew;
        });

    public Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, CancellationToken ct) =>
        CommitFileAsync(modelId, partPath, sha256, null, ct);

    /// <summary>Commits a finished part. When <paramref name="expectedSize"/> is given, a part of another size is refused and kept.</summary>
    public Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, long? expectedSize, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            _folder.AssertMarker();
            var partFull = _folder.Resolve(f.PartPath!);
            if (expectedSize is { } want && new FileInfo(partFull).Length != want)
            {
                throw new StoreSafetyException($"'{Path.GetFileName(partFull)}' is not the expected size; it was not committed.");
            }

            var finalFull = _folder.Resolve(f.Path);
            if (Exists(finalFull))
            {
                // Something appeared at the final name: never replace it and never rename (the engine loads by name).
                throw new StoreSafetyException(
                    $"'{finalFull}' appeared while downloading and is not the host's. The download was kept as "
                    + $"'{Path.GetFileName(partFull)}'; move the other file away and try again.");
            }

            File.Move(partFull, finalFull, overwrite: false);
            _folder.DeleteOwnedFile(f.PartPath! + ".json");
            f.PartPath = null;
            f.Size = new FileInfo(finalFull).Length;
            f.Sha256 = sha256.ToLowerInvariant();
            f.State = StoredFileState.Verified;
            f.FileId = TryFileId(f.Path);
            SaveManifest();
            return ToStoredFile(f, ModelOrigin.Catalogue);
        });

    public Task DiscardPartAsync(string modelId, string partPath, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (m, f) = FindPart(modelId, partPath);
            _folder.DeleteOwnedFile(f.PartPath!);
            _folder.DeleteOwnedFile(f.PartPath! + ".json");
            m.Files.Remove(f); // the model record (kind, engine) stays for a retry; see ForgetIfEmptyAsync
            SaveManifest();
        });

    public Task ForgetIfEmptyAsync(string modelId, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var m = Find(modelId);
            if (m is { Origin: ModelOrigin.Catalogue, Files.Count: 0 })
            {
                _manifest.Models.Remove(m);
                _folder.TryRemoveEmptyFolder(m.Folder!);
                SaveManifest();
            }
        });

    /// <summary>True when a store file or part has this file id (a hard link into the store would match).</summary>
    internal Task<bool> HasFileIdAsync(string fileId, CancellationToken ct) =>
        Locked(ct, () => _manifest.Models.SelectMany(m => m.Files)
            .Any(f => string.Equals(f.FileId, fileId, StringComparison.Ordinal) || string.Equals(f.PartFileId, fileId, StringComparison.Ordinal)));

    public IDisposable AcquireDownload(string modelId)
    {
        lock (_downloading)
        {
            if (!_downloading.Add(modelId))
            {
                throw new InvalidOperationException($"'{modelId}' is already being downloaded.");
            }
        }

        return new Releaser(() =>
        {
            lock (_downloading)
            {
                _downloading.Remove(modelId);
            }
        });
    }

    private bool IsDownloading(string modelId)
    {
        lock (_downloading)
        {
            return _downloading.Contains(modelId);
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public Task<FileStream> OpenPartForWriteAsync(string modelId, string partPath, int bufferSize, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            return OpenOwnedPart(f, bufferSize, useAsync: true);
        });

    public Task TruncatePartAsync(string modelId, string partPath, long length, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            using (var fs = OpenOwnedPart(f, 4096, useAsync: false))
            {
                if (length < fs.Length)
                {
                    fs.SetLength(Math.Max(0, length));
                }
            }

            if (length <= 0)
            {
                _folder.DeleteOwnedFile(f.PartPath! + ".json");
            }
        });

    private FileStream OpenOwnedPart(ManifestFile f, int bufferSize, bool useAsync)
    {
        var fs = _folder.OpenOwnedFileForWrite(f.PartPath!, f.PartFileId, out var id, bufferSize, useAsync);
        if (f.PartFileId is null && id is not null)
        {
            f.PartFileId = id;
            SaveManifest();
        }

        return fs;
    }

    public async Task<StoredFile?> TryAdoptExistingAsync(string modelId, string fileName, string sha256, long size, CancellationToken ct)
    {
        AssertSafeFileName(fileName);
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            var m = Find(modelId);
            if (m is null || m.Origin != ModelOrigin.Catalogue)
            {
                return null;
            }

            var rec = m.Files.FirstOrDefault(f => f.Name == fileName);
            if (rec is { State: not StoredFileState.Partial })
            {
                return ToStoredFile(rec, ModelOrigin.Catalogue);
            }

            _folder.AssertMarker();
            var finalRel = rec?.Path ?? Path.Combine(m.Folder!, fileName);
            var finalFull = _folder.Resolve(finalRel);
            var info = new FileInfo(finalFull);
            if (!info.Exists || info.Length != size || _folder.GetIdentity(finalRel).Links != 1)
            {
                return null;
            }

            var hash = await HashFileAsync(finalFull, ct).ConfigureAwait(false);
            if (!string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (rec is null)
            {
                rec = new ManifestFile { Name = fileName, Path = finalRel };
                m.Files.Add(rec);
            }
            else if (rec.PartPath is { } part)
            {
                _folder.DeleteOwnedFile(part);
                _folder.DeleteOwnedFile(part + ".json");
            }

            rec.PartPath = null;
            rec.PartFileId = null;
            rec.Size = size;
            rec.Sha256 = hash;
            rec.State = StoredFileState.Verified;
            rec.FileId = TryFileId(finalRel);
            SaveManifest();
            return ToStoredFile(rec, ModelOrigin.Catalogue);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Files inside the host's model folders that store.json does not list (for example an orphan .part left by a
    /// crash). Report only: they are never deleted or overwritten.
    /// </summary>
    public Task<IReadOnlyList<string>> ListUnrecordedFilesAsync(CancellationToken ct) =>
        Locked<IReadOnlyList<string>>(ct, () =>
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue).SelectMany(m => m.Files))
            {
                known.Add(f.Path);
                if (f.PartPath is { } p)
                {
                    known.Add(p);
                    known.Add(p + ".json");
                }
            }

            var result = new List<string>();
            foreach (var m in _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue && m.Folder is not null))
            {
                var dir = _folder.Resolve(m.Folder!);
                if (Directory.Exists(dir))
                {
                    result.AddRange(Directory.EnumerateFiles(dir).Where(f => !known.Contains(_folder.ToRelative(f))));
                }
            }

            return result;
        });


    public Task ResetPartAsync(string modelId, string partPath, CancellationToken ct) =>
        Locked(ct, () =>
        {
            var (_, f) = FindPart(modelId, partPath);
            using (var fs = OpenOwnedPart(f, 4096, useAsync: false))
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

    public Task RemoveAsync(string modelId, CancellationToken ct) => RemoveCore(modelId, skipIdentity: false, ct);

    public Task RemoveDamagedAsync(string modelId, CancellationToken ct) => RemoveCore(modelId, skipIdentity: true, ct);

    private Task RemoveCore(string modelId, bool skipIdentity, CancellationToken ct) =>
        Locked(ct, () => RemoveCoreLocked(modelId, skipIdentity));

    /// <summary>The removal of a whole model; the caller holds the store gate.</summary>
    private void RemoveCoreLocked(string modelId, bool skipIdentity)
        {
            var m = Find(modelId) ?? throw new KeyNotFoundException(modelId);
            if (m.Files.Any(f => f.State == StoredFileState.InUse))
            {
                throw new InvalidOperationException($"'{modelId}' is in use; stop the engine first.");
            }

            if (IsDownloading(modelId))
            {
                throw new InvalidOperationException($"'{modelId}' is downloading; pause it first.");
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
                _folder.AssertDeletable(f.Path, f.State == StoredFileState.Partial || skipIdentity ? null : f.Size,
                    f.State == StoredFileState.Partial || skipIdentity ? null : f.FileId);
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
                    if (skipIdentity)
                    {
                        _folder.DeleteOwnedFile(f.Path);
                    }
                    else
                    {
                        _folder.DeleteOwnedFile(f.Path, f.Size, f.FileId);
                    }
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
            SaveManifest();
            try
            {
                _folder.TryRemoveEmptyFolder(m.Folder!); // the files are gone and recorded as gone; an empty folder left behind is harmless
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
    }

    public Task<bool> ClearStalePreviousStoreAsync(CancellationToken ct) => Locked(ct, ClearStaleCore);

    private bool ClearStaleCore()
    {
        if (_manifest.PreviousStore is not { } prev || !IsStalePrevious(prev))
        {
            return false;
        }

        _manifest.PreviousStore = null;
        SaveManifest();
        return true;
    }

    /// <summary>Reads only. An unplugged drive is not "stale": the old copies may still be there.</summary>
    private bool IsStalePrevious(PreviousStore prev)
    {
        try
        {
            var driveRoot = Path.GetPathRoot(prev.Path);
            if (driveRoot is null || !Directory.Exists(driveRoot))
            {
                return false;
            }

            if (!Directory.Exists(prev.Path) || OwnedFolder.ReadMarker(prev.Path, _options) != prev.StoreId)
            {
                return true;
            }

            var manifestPath = Path.Combine(prev.Path, OwnedFolder.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return true;
            }

            StoreManifest? old;
            try
            {
                old = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), StoreJsonContext.Default.StoreManifest);
            }
            catch (JsonException)
            {
                return true;
            }

            return old is null || old.StoreId != prev.StoreId
                || !old.Models.Where(m => m.Origin == ModelOrigin.Catalogue).SelectMany(m => m.Files)
                    .Any(f => File.Exists(Path.Combine(prev.Path, f.Path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public long MoveBytesNeeded
    {
        get
        {
            var total = _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue).SelectMany(m => m.Files).Sum(f => f.Size);
            return total + (long)(total * 0.02) + (2L << 30);
        }
    }

    public string? CheckMoveTarget(string newParentFolder)
    {
        try
        {
            var parent = CheckedParent(newParentFolder, _disk);
            if (new DirectoryInfo(parent).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return "That folder is a link to another place. Pick the real folder.";
            }

            var canonicalParent = FinalPath.Resolve(parent) ?? parent;
            var canonicalRoot = FinalPath.Resolve(_folder.Root) ?? _folder.Root;
            if (string.Equals(canonicalParent, canonicalRoot, PathComparison)
                || canonicalParent.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, PathComparison))
            {
                return "That folder is inside the current models folder. Pick a folder outside it.";
            }

            return Directory.Exists(StoreFolderIn(parent, _options))
                ? "That folder already has a Portal AI Host Models folder. Pick another folder."
                : null;
        }
        catch (StoreRefusedException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"'{newParentFolder}' cannot be used: {ex.Message}";
        }
    }

    public async Task MoveStoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
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
        if (_manifest.PreviousStore is { } pending && !ClearStaleCore())
        {
            throw new InvalidOperationException(
                $"Your last move left old copies in '{pending.Path}'. Open Models and remove them under the earlier folder, or delete that folder yourself, then try again.");
        }

        if (CheckMoveTarget(newParentFolder) is { } refusedTarget)
        {
            throw new StoreRefusedException(refusedTarget);
        }

        var files = _manifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue).SelectMany(m => m.Files).ToList();
        if (files.Any(f => f.State != StoredFileState.Verified))
        {
            throw new InvalidOperationException("Pause downloads and stop the engine before moving the store.");
        }

        var total = files.Sum(f => f.Size);
        var root = PickNewStoreFolder(newParentFolder, _disk, _options);
        var needed = MoveBytesNeeded;
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

        var target = OwnedFolder.CreateNew(root, _options);
        var targetLock = target.AcquireLock();
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
                    Id = m.Id, DisplayName = m.DisplayName, Kind = m.Kind, Engine = m.Engine, Origin = m.Origin, Folder = m.Folder, UserAdded = m.UserAdded,
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
                    // Listed in the new store.json before the first byte is copied, so a crash or a lost drive leaves a
                    // folder whose leftovers the store itself can remove later.
                    var entry = new ManifestFile { Name = f.Name, Path = f.Path, PartPath = partRel, State = StoredFileState.Partial };
                    copy.Files.Add(entry);
                    target.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(newManifest, StoreJsonContext.Default.StoreManifest));
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
                    entry.PartPath = null;
                    entry.Size = f.Size;
                    entry.Sha256 = expected;
                    entry.State = StoredFileState.Verified;
                    done += f.Size;
                    progress?.Report(total == 0 ? 1 : (double)done / total);
                }
            }

            target.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(newManifest, StoreJsonContext.Default.StoreManifest));
        }
        catch (Exception original)
        {
            targetLock.Dispose();
            var cleanupFailed = false;

            // Undo only what this move created inside the new, freshly marked folder. A cleanup problem never hides
            // the original error; anything left over stays in the new marked folder for the user to see.
            try
            {
                foreach (var rel in created)
                {
                    target.DeleteOwnedFile(rel);
                }

                foreach (var rel in createdFolders)
                {
                    target.TryRemoveEmptyFolder(rel);
                }

                target.TryRetireEmptyStore();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupFailed = true;
            }

            if (cleanupFailed)
            {
                throw new StoreCleanupIncompleteException(
                    "The move did not finish and could only be partly cleaned up. What is left is listed under earlier folders at '"
                    + target.Root + "' and can be removed there. Your models are where they were. " + original.Message, target.Root, original);
            }

            throw;
        }

        var oldLock = _lock;
        _lock = targetLock;
        oldLock.Dispose();
        _folder = target;
        _manifest = newManifest;
        progress?.Report(1);
    }

    /// <summary>
    /// After a move, deletes the old copies file by file. A file is deleted only when it is listed in the old
    /// store.json, lives in the old marked folder, still has its recorded size, and its copy in the current store is
    /// verified with the same hash. Anything else is left. Returns the paths left in place.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveOldCopiesAsync(CancellationToken ct)
    {
        await EnterGateAsync(ct).ConfigureAwait(false);
        try
        {
            return await RemoveOldCopiesCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> RemoveOldCopiesCoreAsync(CancellationToken ct)
    {
        var prev = _manifest.PreviousStore ?? throw new InvalidOperationException("There is no previous store.");
        if (prev.StoreId == _folder.StoreId
            || string.Equals(OwnedFolder.Normalize(prev.Path), _folder.Root, PathComparison))
        {
            throw new StoreSafetyException("The previous store is the current store; nothing was removed.");
        }

        if (OwnedFolder.ReadMarker(prev.Path, _options) != prev.StoreId)
        {
            throw new StoreSafetyException($"The old store '{prev.Path}' no longer carries its marker; nothing was removed.");
        }

        var old = new OwnedFolder(prev.Path, prev.StoreId, _options);
        var oldLock = old.AcquireLock(); // refused (StoreLockedException) while another store object or process has it open
        try
        {
            return await RemoveOldCopiesLockedAsync(old, prev, oldLock, ct).ConfigureAwait(false);
        }
        finally
        {
            oldLock.Dispose();
            try
            {
                old.TryRemoveEmptyRootAfterRetire();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<IReadOnlyList<string>> RemoveOldCopiesLockedAsync(OwnedFolder old, PreviousStore prev, FileStream oldLock, CancellationToken ct)
    {
        StoreManifest? oldManifest;
        try
        {
            oldManifest = File.Exists(old.ManifestPath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(old.ManifestPath), StoreJsonContext.Default.StoreManifest)
                : null;
        }
        catch (JsonException)
        {
            oldManifest = null;
        }

        if (oldManifest is null || oldManifest.StoreId != prev.StoreId)
        {
            throw new StoreSafetyException("The old store.json is missing, damaged or belongs to another store; nothing was removed.");
        }

        var kept = new List<string>();
        var retired = false;
        try
        {
            foreach (var m in oldManifest.Models.Where(m => m.Origin == ModelOrigin.Catalogue))
            {
                foreach (var f in m.Files.ToList())
                {
                    try
                    {
                        // The new copy must exist, be recorded as verified, and hash to the old copy's recorded SHA-256.
                        var current = Find(m.Id)?.Files.FirstOrDefault(c => c.Name == f.Name);
                        var copyOk = false;
                        if (current is { State: not StoredFileState.Partial } && f.State != StoredFileState.Partial
                            && f.Sha256 is not null && current.Size == f.Size
                            && string.Equals(current.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            var copyPath = _folder.Resolve(current.Path);
                            copyOk = new FileInfo(copyPath) is { Exists: true } ci && ci.Length == current.Size
                                && string.Equals(await HashFileAsync(copyPath, ct).ConfigureAwait(false), f.Sha256, StringComparison.OrdinalIgnoreCase);
                        }

                        if (!copyOk)
                        {
                            kept.Add(Path.Combine(old.Root, f.Path));
                            continue;
                        }

                        old.DeleteOwnedFile(f.Path, f.Size, f.FileId);
                        m.Files.Remove(f);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        kept.Add(Path.Combine(old.Root, f.Path)); // one locked or vanished file never stops the others
                    }
                }

                if (m.Files.Count == 0 && m.Folder is not null)
                {
                    try
                    {
                        old.TryRemoveEmptyFolder(m.Folder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            oldManifest.Models.RemoveAll(m => m.Origin == ModelOrigin.External || m.Files.Count == 0);
            if (oldManifest.Models.Count == 0)
            {
                try
                {
                    retired = old.TryRetireEmptyStore(ownLockHeld: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            if (!retired)
            {
                // Always record what is really left (also after an error or a cancel), so no model is listed whose files are gone.
                try
                {
                    old.ReplaceOwnedFile(OwnedFolder.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(oldManifest, StoreJsonContext.Default.StoreManifest));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        if (kept.Count == 0)
        {
            _manifest.PreviousStore = null;
            SaveManifest();
        }

        return kept;
    }

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
        if (!SafeFileName().IsMatch(modelId + ".gguf") || modelId.Contains("..", StringComparison.Ordinal)
            || OwnedFolder.IsReservedDeviceName(modelId))
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

    /// <summary>"name.gguf.part", or "name.gguf.2.part" ... when an unrecorded .part (or its sidecar) is in the way.</summary>
    private static string UniquePartPath(string finalFull)
    {
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? finalFull + ".part" : $"{finalFull}.{i}.part";
            if (!Exists(candidate) && !Exists(candidate + ".json") && !Exists(candidate + ".json.tmp"))
            {
                return candidate;
            }
        }
    }

    private static void AssertSafeFileName(string fileName)
    {
        if (!SafeFileName().IsMatch(fileName) || fileName.Contains("..", StringComparison.Ordinal)
            || OwnedFolder.IsReservedDeviceName(fileName))
        {
            throw new StoreSafetyException($"'{fileName}' is not a safe model file name.");
        }
    }

    private string? TryFileId(string relativePath)
    {
        try
        {
            return _folder.GetIdentity(relativePath).Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // the size check still applies
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
        new(m.Id, m.DisplayName, m.Kind, m.Engine, m.Origin, m.Files.Select(f => ToStoredFile(f, m.Origin)).ToList(), m.UserAdded);

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
        await EnterGateAsync(ct).ConfigureAwait(false);
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
        await EnterGateAsync(ct).ConfigureAwait(false);
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
