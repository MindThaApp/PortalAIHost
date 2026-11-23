using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.AiHost.Core.Store;

/// <summary>Thrown when a store operation would touch something the host cannot prove it created.</summary>
public sealed class StoreSafetyException(string message) : IOException(message);

/// <summary>Thrown when a folder cannot be used as a store (no marker, foreign marker, FAT32 ...).</summary>
public sealed class StoreRefusedException(string message) : IOException(message);

/// <summary>Free space and file system of the volume that holds a path. Faked in tests.</summary>
public interface IDiskInfo
{
    long GetFreeBytes(string path);

    string GetFileSystem(string path);
}

public sealed class SystemDiskInfo : IDiskInfo
{
    public static SystemDiskInfo Instance { get; } = new();

    public long GetFreeBytes(string path) => Drive(path)?.AvailableFreeSpace ?? 0;

    public string GetFileSystem(string path)
    {
        try
        {
            return Drive(path)?.DriveFormat ?? "unknown";
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static DriveInfo? Drive(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        return string.IsNullOrEmpty(root) ? null : new DriveInfo(root);
    }
}

internal sealed class StoreMarker
{
    public string Kind { get; set; } = StoreOptions.DefaultMarkerKind;
    public Guid StoreId { get; init; }
    public DateTimeOffset Created { get; init; }
}

/// <summary>
/// Names that identify a store: the folder name used inside a chosen parent folder, the marker file name and the
/// marker kind written inside it. Two stores with different options never adopt each other's folders. The defaults
/// are Portal AI Host's own values. Another app must use <see cref="ForApp"/>, which derives all three together, so
/// it can never open the host's store (or the other way round). Options that differ from the defaults but keep the
/// default marker file name or kind are refused.
/// </summary>
public sealed partial record StoreOptions
{
    public const string DefaultFolderName = Portal.AiHost.Contracts.HostFolders.ModelsSubfolder;
    public const string DefaultMarkerFileName = ".portal-ai-host-store";
    public const string DefaultMarkerKind = "portal-ai-host-store";

    public static StoreOptions Default { get; } = new();

    /// <summary>Subfolder created inside the user's chosen parent folder.</summary>
    public string FolderName { get; init; } = DefaultFolderName;

    /// <summary>Marker file at the store root; a folder without it is never adopted.</summary>
    public string MarkerFileName { get; init; } = DefaultMarkerFileName;

    /// <summary>Kind string stored inside the marker; a marker with another kind is not this store's.</summary>
    public string MarkerKind { get; init; } = DefaultMarkerKind;

    /// <summary>
    /// Options for another app's own store. <paramref name="appKey"/> is lower-case letters, digits and hyphens
    /// (for example "voice-portal"); the folder is "&lt;appKey&gt; Models", the marker file ".&lt;appKey&gt;-store" and the kind
    /// "&lt;appKey&gt;-store". The host's own key ("portal-ai-host") is refused.
    /// </summary>
    public static StoreOptions ForApp(string appKey)
    {
        if (appKey is null || !AppKeyPattern().IsMatch(appKey) || appKey.EndsWith('-') || appKey.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("The app key must be lower-case letters, digits and single hyphens (at most 48 characters).", nameof(appKey));
        }

        var options = new StoreOptions
        {
            FolderName = appKey + " Models",
            MarkerFileName = "." + appKey + "-store",
            MarkerKind = appKey + "-store",
        };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (FolderName is null || MarkerFileName is null || MarkerKind is null)
        {
            throw new ArgumentException("Store names must not be empty.");
        }

        if (!FolderPattern().IsMatch(FolderName) || FolderName.EndsWith('.') || FolderName.EndsWith(' ')
            || FolderName is "." or ".." || OwnedFolder.IsReservedDeviceName(FolderName) || IsInternalName(FolderName))
        {
            throw new ArgumentException("The store folder name must be a plain, safe folder name.");
        }

        if (!MarkerPattern().IsMatch(MarkerFileName) || IsInternalName(MarkerFileName))
        {
            throw new ArgumentException("The store marker name must be a plain dotted name such as '.my-app-store'.");
        }

        if (!KindPattern().IsMatch(MarkerKind))
        {
            throw new ArgumentException("The store marker kind must be lower-case letters, digits and hyphens.");
        }

        if (!Equals(Default)
            && (string.Equals(MarkerFileName, DefaultMarkerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(MarkerKind, DefaultMarkerKind, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Custom store options must not reuse the default marker name or kind. Use StoreOptions.ForApp(appKey).");
        }
    }

    private static bool IsInternalName(string name) =>
        name.Equals("store.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("store.json.tmp", StringComparison.OrdinalIgnoreCase)
        || name.Equals(OwnedFolder.LockFileName, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial System.Text.RegularExpressions.Regex AppKeyPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,99}$")]
    private static partial System.Text.RegularExpressions.Regex FolderPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[.][a-z0-9][a-z0-9-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex MarkerPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex KindPattern();
}

/// <summary>
/// One store folder the host created, identified by its marker file. Every delete, truncate and replace in the
/// store goes through this class, which checks before each operation that:
/// the marker is present and carries the expected store GUID; the target is strictly inside the folder;
/// no folder between the store root and the target (root included) is a junction or symbolic link;
/// the target itself is not a link. Nothing here deletes recursively.
/// </summary>
internal sealed class OwnedFolder
{
    public const string ManifestFileName = "store.json";
    public const string LockFileName = ".store-lock";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public OwnedFolder(string root, Guid storeId, StoreOptions? options = null)
    {
        Root = Normalize(root);
        StoreId = storeId;
        Options = options ?? StoreOptions.Default;
    }

    public StoreOptions Options { get; }
    public string MarkerFileName => Options.MarkerFileName;

    public string Root { get; }
    public Guid StoreId { get; }

    public string MarkerPath => Path.Combine(Root, MarkerFileName);
    public string ManifestPath => Path.Combine(Root, ManifestFileName);

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Reads the marker GUID of a folder, or null when the folder carries no valid marker.</summary>
    public static Guid? ReadMarker(string root, StoreOptions? options = null)
    {
        options ??= StoreOptions.Default;
        var marker = Path.Combine(Normalize(root), options.MarkerFileName);
        var info = new FileInfo(marker);
        if (!info.Exists || info.LinkTarget is not null || IsLinkedDirectory(new DirectoryInfo(Normalize(root))))
        {
            return null;
        }

        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllBytes(marker), StoreJsonContext.Default.StoreMarker);
            return m is { } && m.Kind == options.MarkerKind && m.StoreId != Guid.Empty ? m.StoreId : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Creates a brand-new store folder with a fresh marker. Fails if anything already exists there.</summary>
    public static OwnedFolder CreateNew(string root, StoreOptions? options = null)
    {
        root = Normalize(root);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new StoreRefusedException($"'{root}' already exists; the host never adopts an existing folder.");
        }

        Directory.CreateDirectory(root);
        var folder = new OwnedFolder(root, Guid.NewGuid(), options);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new StoreMarker { Kind = folder.Options.MarkerKind, StoreId = folder.StoreId, Created = DateTimeOffset.UtcNow }, StoreJsonContext.Default.StoreMarker);
        using (var fs = new FileStream(folder.MarkerPath, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(bytes);
        }

        return folder;
    }

    /// <summary>
    /// Takes an exclusive lock for as long as the returned stream is open, so two processes (or two store objects)
    /// never work in one store at the same time. The lock file is created by this store inside its own root and
    /// disappears when the lock is released. Refused when someone else holds it.
    /// </summary>
    public FileStream AcquireLock()
    {
        AssertMarker();
        var path = Path.Combine(Root, LockFileName);
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new StoreSafetyException("The store lock is a link; the store was not opened.");
        }

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new StoreRefusedException("This model store is already open in another window or process, so it was not opened again. " + ex.Message);
        }
    }

    public void AssertMarker()
    {
        if (ReadMarker(Root, Options) != StoreId)
        {
            throw new StoreSafetyException($"Store marker missing or changed in '{Root}'; nothing was touched.");
        }
    }

    /// <summary>Resolves a store-relative path, refusing traversal, rooted paths and link escapes.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new StoreSafetyException($"'{relativePath}' is not a store-relative path.");
        }

        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!IsStrictlyInside(full))
        {
            throw new StoreSafetyException($"'{relativePath}' resolves outside the store.");
        }

        AssertNoLinksBetweenRootAnd(full);
        return full;
    }

    public bool IsStrictlyInside(string fullPath) =>
        Normalize(fullPath).StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);

    public string ToRelative(string fullPath)
    {
        var full = Normalize(fullPath);
        if (!IsStrictlyInside(full))
        {
            throw new StoreSafetyException($"'{fullPath}' is outside the store.");
        }

        return full[(Root.Length + 1)..];
    }

    private void AssertNoLinksBetweenRootAnd(string full)
    {
        if (IsLinkedDirectory(new DirectoryInfo(Root)))
        {
            throw new StoreSafetyException($"The store root '{Root}' is a link.");
        }

        var target = new FileInfo(full);
        if (target.Exists && target.LinkTarget is not null)
        {
            throw new StoreSafetyException($"'{full}' is a link; links are never touched.");
        }

        if (Directory.Exists(full) && IsLinkedDirectory(new DirectoryInfo(full)))
        {
            throw new StoreSafetyException($"'{full}' is a junction or link.");
        }

        for (var dir = target.Directory; dir is not null && IsStrictlyInside(dir.FullName); dir = dir.Parent)
        {
            if (IsLinkedDirectory(dir))
            {
                throw new StoreSafetyException($"'{dir.FullName}' is a junction or link; refusing to follow it.");
            }
        }
    }

    private static bool IsLinkedDirectory(DirectoryInfo dir) =>
        dir.Exists && (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget is not null);

    /// <summary>
    /// Deletes one file the caller has found in the manifest. Refuses (throws) unless the marker matches, the path
    /// is inside the store with no links on the way, and, when given, the size still matches what the host
    /// recorded, and, when given, the file ID still matches (the same file, not a replacement). Missing files are
    /// fine (already gone).
    /// </summary>
    public void DeleteOwnedFile(string relativePath, long? expectedSize = null, string? expectedFileId = null)
    {
        AssertMarker();
        var full = Resolve(relativePath);
        if (Directory.Exists(full))
        {
            throw new StoreSafetyException($"'{full}' is a folder, not a file the host created.");
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            return;
        }

        if (expectedSize is { } size && info.Length != size)
        {
            throw new StoreSafetyException($"'{full}' changed since the host stored it; left in place.");
        }

        if (expectedFileId is not null && GetIdentity(relativePath).Id is { } id && !string.Equals(id, expectedFileId, StringComparison.Ordinal))
        {
            throw new StoreSafetyException($"'{full}' was replaced since the host stored it; left in place.");
        }

        File.Delete(full);
    }

    /// <summary>
    /// Opens a host-created file for writing (never creating or truncating it) after the marker and link checks,
    /// then checks the open handle: exactly one hard link, and the same file ID the host recorded when it created
    /// the file. Returns the identity so the caller can record it on first use.
    /// </summary>
    public FileStream OpenOwnedFileForWrite(string relativePath, string? expectedFileId, out string? fileId, int bufferSize = 4096, bool useAsync = false)
    {
        AssertMarker();
        var full = Resolve(relativePath);
        var fs = new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.Read, bufferSize, useAsync);
        try
        {
            var (links, id) = FileIdentity.Read(fs.SafeFileHandle);
            if (links != 1)
            {
                throw new StoreSafetyException($"'{full}' has {links} hard links; the host never writes to a linked file.");
            }

            if (expectedFileId is not null && id is not null && !string.Equals(id, expectedFileId, StringComparison.Ordinal))
            {
                throw new StoreSafetyException($"'{full}' was replaced since the host created it; it was not written.");
            }

            fileId = id;
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>File ID and hard-link count of a file inside the store (read-only).</summary>
    public (uint Links, string? Id) GetIdentity(string relativePath)
    {
        var full = Resolve(relativePath);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return FileIdentity.Read(fs.SafeFileHandle);
    }

    /// <summary>Windows reserved device names (con, prn, aux, nul, com1-9, lpt1-9), with any extension.</summary>
    public static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToLowerInvariant();
        return stem is "con" or "prn" or "aux" or "nul"
            || (stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9');
    }

    /// <summary>Checks that <see cref="DeleteOwnedFile"/> would succeed, without deleting.</summary>
    public void AssertDeletable(string relativePath, long? expectedSize = null, string? expectedFileId = null)
    {
        AssertMarker();
        var full = Resolve(relativePath);
        if (Directory.Exists(full))
        {
            throw new StoreSafetyException($"'{full}' is a folder, not a file the host created.");
        }

        var info = new FileInfo(full);
        if (info.Exists && expectedSize is { } size && info.Length != size)
        {
            throw new StoreSafetyException($"'{full}' changed since the host stored it; nothing was removed.");
        }

        if (info.Exists && expectedFileId is not null && GetIdentity(relativePath).Id is { } id
            && !string.Equals(id, expectedFileId, StringComparison.Ordinal))
        {
            throw new StoreSafetyException($"'{full}' was replaced since the host stored it; nothing was removed.");
        }
    }

    /// <summary>Removes a folder the host created, only when it is empty (never recursive).</summary>
    public bool TryRemoveEmptyFolder(string relativePath)
    {
        AssertMarker();
        var full = Resolve(relativePath);
        if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
        {
            return false;
        }

        Directory.Delete(full, recursive: false);
        return true;
    }

    /// <summary>Atomically replaces a host-owned file (store.json, a resume sidecar) via a temp file.</summary>
    public void ReplaceOwnedFile(string relativePath, ReadOnlySpan<byte> content)
    {
        AssertMarker();
        var full = Resolve(relativePath);
        var tmpRel = relativePath + ".tmp";
        DeleteOwnedFile(tmpRel);
        var tmp = Resolve(tmpRel);
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>
    /// Final step of removing a whole store folder the host created: deletes store.json and the marker, then the
    /// folder, and only when nothing else is left in it.
    /// </summary>
    public bool TryRetireEmptyStore()
    {
        AssertMarker();
        var leftovers = Directory.EnumerateFileSystemEntries(Root)
            .Select(Path.GetFileName)
            .Where(n => n != MarkerFileName && n != ManifestFileName)
            .ToList();
        if (leftovers.Count > 0)
        {
            return false;
        }

        DeleteOwnedFile(ManifestFileName);
        AssertMarker();
        File.Delete(MarkerPath);
        if (!Directory.EnumerateFileSystemEntries(Root).Any())
        {
            Directory.Delete(Root, recursive: false);
        }

        return true;
    }
}

/// <summary>Volume serial + file index and hard-link count of an open file (Windows; elsewhere unknown).</summary>
internal static partial class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
#pragma warning disable SYSLIB1054 // blittable signature, no marshalling needed
    private static extern int GetFileInformationByHandle(nint file, out ByHandleFileInformation info);
#pragma warning restore SYSLIB1054

    public static (uint Links, string? Id) Read(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (1, null);
        }

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (GetFileInformationByHandle(handle.DangerousGetHandle(), out var info) == 0)
            {
                throw new StoreSafetyException("Could not read the identity of a store file; it was not touched.");
            }

            return (info.NumberOfLinks, $"{info.VolumeSerialNumber:x8}-{info.FileIndexHigh:x8}{info.FileIndexLow:x8}");
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(StoreMarker))]
[JsonSerializable(typeof(StoreManifest))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
