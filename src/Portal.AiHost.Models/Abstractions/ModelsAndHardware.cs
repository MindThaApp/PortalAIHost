using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Abstractions;

/// <summary>Who put a model file into the store.</summary>
public enum ModelOrigin
{
    /// <summary>Downloaded by the host into its own store folder.</summary>
    Catalogue,

    /// <summary>Added by the user and used in place. Removing only forgets it; the file is never touched.</summary>
    External,
}

/// <summary>State of one stored file (store.json: partial, verified, in_use).</summary>
public enum StoredFileState
{
    Partial,
    Verified,
    InUse,
}

public sealed record StoredFile(
    string Name, string Path, long SizeBytes, string? Sha256, StoredFileState State);

public sealed record StoredModel(
    string Id,
    string DisplayName,
    string Kind,
    string Engine,
    ModelOrigin Origin,
    IReadOnlyList<StoredFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.SizeBytes);
    public bool IsComplete => Files.Count > 0 && Files.All(f => f.State != StoredFileState.Partial);
}

public sealed record StoreLocation(string Path, Guid StoreId, long FreeBytes, string FileSystem);

/// <summary>
/// Inventory of model files the host created or was told about. Obeys the destructive-code
/// rules: never overwrite or delete a file the host did not create, never delete recursively
/// outside its own marked folder, moves are copy then verify then delete.
/// </summary>
public interface IModelStore
{
    StoreLocation Location { get; }

    Task<IReadOnlyList<StoredModel>> ListAsync(CancellationToken ct);

    Task<StoredModel?> GetAsync(string modelId, CancellationToken ct);

    /// <summary>Opens a new file for writing as name.part and returns its path; name clashes get "name (2).ext".</summary>
    Task<string> BeginFileAsync(string modelId, string fileName, CancellationToken ct);

    /// <summary>Renames a finished .part file into place (never replacing), records size and hash.</summary>
    Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, CancellationToken ct);

    /// <summary>Marks the files of a model as in use by a running engine, or releases them.</summary>
    Task SetInUseAsync(string modelId, bool inUse, CancellationToken ct);

    /// <summary>Registers a user-supplied file to be used in place.</summary>
    Task<StoredModel> AddExternalAsync(string filePath, string kind, string engine, CancellationToken ct);

    /// <summary>
    /// Removes a model. Catalogue files the host created are deleted one by one (never recursively);
    /// external models are only forgotten. Refuses while in use.
    /// </summary>
    Task RemoveAsync(string modelId, CancellationToken ct);

    /// <summary>Copies to a new location, verifies hashes, switches, and leaves old copies for the user to remove.</summary>
    Task MoveStoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct);
}

public sealed record GpuProfile(
    string Name, string Vendor, string LuidOrId, string Driver, long VramBytes, long VramBudgetBytes);

public sealed record DriveProfile(string Root, string FileSystem, long FreeBytes);

public sealed record MemorySnapshot(long TotalRamBytes, long AvailableRamBytes, long[] FreeVramBytesPerGpu);

/// <summary>Result of the hardware check. Fingerprint changes when RAM, GPU set, VRAM or driver change.</summary>
public sealed record HardwareProfile(
    string Os,
    string Arch,
    int CpuCores,
    long RamBytes,
    IReadOnlyList<GpuProfile> Gpus,
    IReadOnlyList<DriveProfile> Drives,
    string Fingerprint);

public interface IHardwareProbe
{
    Task<HardwareProfile> ProbeAsync(CancellationToken ct);

    /// <summary>Live free memory, read at load time by the planner.</summary>
    MemorySnapshot GetMemory();

    /// <summary>True on battery power (idle unload uses a shorter timeout).</summary>
    bool IsOnBattery { get; }
}

/// <summary>
/// Cheap checks for the ease-off rules. Each call must be quick (no busy loops): the host asks every few seconds.
/// </summary>
public interface IEaseOffSensors
{
    /// <summary>A full-screen program (game, video, presentation) is in front.</summary>
    bool IsFullscreen();

    /// <summary>Another program is using the graphics card heavily.</summary>
    bool IsGpuBusy();

    /// <summary>The PC runs from its battery.</summary>
    bool IsOnBattery();
}
