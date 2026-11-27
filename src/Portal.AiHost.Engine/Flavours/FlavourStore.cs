using System.Security.Cryptography;
using System.Text.Json;

namespace Portal.AiHost.Engine.Flavours;

/// <summary>Thrown when an engine folder operation would touch something the host cannot prove it created.</summary>
public sealed class FlavourSafetyException(string message) : IOException(message);

/// <summary>Result of checking an installed flavour folder against its pinned file list.</summary>
public sealed record FlavourCheck(bool Ok, string? Problem)
{
    public static FlavourCheck Good { get; } = new(true, null);

    public static FlavourCheck Bad(string problem) => new(false, problem);
}

/// <summary>
/// Downloaded engine builds under the host's data folder: engines\&lt;flavour&gt;\&lt;tag&gt;-&lt;sha8&gt;\. Every version folder
/// carries a <see cref="MarkerFileName"/> marker that names its flavour and pin. Folders are only ever removed
/// when they carry that marker, sit directly in their flavour folder inside this store, and contain no junctions
/// or links; anything else is left in place. Nothing here follows a link.
/// </summary>
public sealed class FlavourStore
{
    public const string MarkerFileName = ".portal-engine";
    public const string MarkerKind = "portal-engine";
    public const string StagingSuffix = ".staging";
    public const string PartSuffix = ".zip.part";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FlavourStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Normalize(root);
    }

    /// <summary>The store folder: engines\ inside the host's data folder (packaged LocalState).</summary>
    public static string DefaultRoot(string dataRoot) => Path.Combine(dataRoot, "engines");

    public string Root { get; }

    public string FlavourFolder(string flavourId) => Path.Combine(Root, CheckedId(flavourId));

    public string VersionFolder(FlavourEntry e) => Path.Combine(FlavourFolder(e.Id), e.VersionName);

    internal string PartPath(FlavourEntry e) => Path.Combine(FlavourFolder(e.Id), e.VersionName + PartSuffix);

    /// <summary>Creates the store and flavour folders when missing; refuses when either is a junction or link.</summary>
    internal string EnsureFlavourFolder(string flavourId)
    {
        Directory.CreateDirectory(Root);
        AssertNotLinked(Root);
        var folder = FlavourFolder(flavourId);
        Directory.CreateDirectory(folder);
        AssertNotLinked(folder);
        return folder;
    }

    /// <summary>The verified folder of the pinned version, or null when it is missing or does not match the pin.</summary>
    public string? TryGetVerified(FlavourEntry e, CancellationToken ct = default)
    {
        var folder = VersionFolder(e);
        return Directory.Exists(folder) && Verify(folder, e, ct).Ok ? folder : null;
    }

    /// <summary>
    /// Checks a folder against the pin: the marker matches, the folder holds exactly the listed files (no extra
    /// files, which could be loaded by the engine), each with the listed size and SHA-256, and nothing is a link.
    /// </summary>
    public FlavourCheck Verify(string folder, FlavourEntry e, CancellationToken ct = default)
    {
        folder = Normalize(folder);
        try
        {
            AssertNotLinked(Root);
            AssertNotLinked(folder);
            var marker = ReadMarker(folder);
            if (marker is null || marker.Flavour != e.Id || !string.Equals(marker.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return FlavourCheck.Bad("marker missing or for another build");
            }

            var expected = e.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var found = 0;
            foreach (var (info, relative) in Walk(folder))
            {
                ct.ThrowIfCancellationRequested();
                if (info is DirectoryInfo)
                {
                    continue;
                }

                if (relative.Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!expected.TryGetValue(relative, out var file))
                {
                    return FlavourCheck.Bad($"unexpected file '{relative}'");
                }

                var fi = (FileInfo)info;
                if (fi.Length != file.Size)
                {
                    return FlavourCheck.Bad($"'{relative}' has the wrong size");
                }

                if (!string.Equals(HashFile(fi.FullName, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return FlavourCheck.Bad($"'{relative}' does not match its SHA-256");
                }

                found++;
            }

            return found == expected.Count ? FlavourCheck.Good : FlavourCheck.Bad("files are missing");
        }
        catch (FlavourSafetyException ex)
        {
            return FlavourCheck.Bad(ex.Message);
        }
        catch (IOException ex)
        {
            return FlavourCheck.Bad(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FlavourCheck.Bad(ex.Message);
        }
    }

    /// <summary>Creates a new, uniquely named staging folder with the marker written first.</summary>
    internal string CreateStaging(FlavourEntry e)
    {
        var parent = EnsureFlavourFolder(e.Id);
        var staging = Path.Combine(parent, e.VersionName + "." + Guid.NewGuid().ToString("N")[..8] + StagingSuffix);
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            throw new FlavourSafetyException($"'{staging}' already exists; nothing was touched.");
        }

        Directory.CreateDirectory(staging);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new FlavourMarker { Flavour = e.Id, Tag = e.Tag, Sha256 = e.Sha256.ToLowerInvariant(), Created = DateTimeOffset.UtcNow },
            FlavourJsonContext.Default.FlavourMarker);
        using (var fs = new FileStream(Path.Combine(staging, MarkerFileName), FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(bytes);
        }

        return staging;
    }

    /// <summary>
    /// True when <paramref name="folder"/> is a version or staging folder of <paramref name="flavourId"/> that this
    /// host created: directly inside engines\&lt;flavour&gt;\ of this store, not a link, and carrying the marker.
    /// </summary>
    public bool IsOwnedFolder(string folder, string flavourId, out string? reason)
    {
        try
        {
            AssertOwnedFolder(Normalize(folder), flavourId);
            reason = null;
            return true;
        }
        catch (FlavourSafetyException ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes a folder the host created (see <see cref="IsOwnedFolder"/>). The whole tree is listed first; when any
    /// entry in it is a junction or link nothing is deleted. Files go first, then folders bottom-up (never recursive),
    /// then the marker, so an interrupted delete keeps its marker and can be finished later.
    /// </summary>
    public void DeleteOwnedFolder(string folder, string flavourId)
    {
        folder = Normalize(folder);
        AssertOwnedFolder(folder, flavourId);
        var entries = Walk(folder).ToList();
        var marker = Path.Combine(folder, MarkerFileName);
        foreach (var (info, _) in entries)
        {
            if (info is FileInfo f && !string.Equals(f.FullName, marker, PathComparison))
            {
                File.Delete(f.FullName);
            }
        }

        foreach (var (info, _) in entries.Where(x => x.Info is DirectoryInfo).OrderByDescending(x => x.Info.FullName.Length))
        {
            Directory.Delete(info.FullName, recursive: false);
        }

        AssertOwnedFolder(folder, flavourId);
        File.Delete(marker);
        Directory.Delete(folder, recursive: false);
    }

    /// <summary>Removes the host's own leftover staging folders of a flavour; unmarked folders are kept.</summary>
    public int CleanStaging(string flavourId)
    {
        var parent = FlavourFolder(flavourId);
        if (!Directory.Exists(parent))
        {
            return 0;
        }

        AssertNotLinked(Root);
        AssertNotLinked(parent);
        var removed = 0;
        foreach (var dir in new DirectoryInfo(parent).EnumerateDirectories("*" + StagingSuffix))
        {
            if (IsOwnedFolder(dir.FullName, flavourId, out _))
            {
                DeleteOwnedFolder(dir.FullName, flavourId);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// After the pinned version verified: removes the other version folders of the flavour that the host created
    /// and that <paramref name="inUse"/> does not claim. Folders without the marker are kept. Returns the removed paths.
    /// </summary>
    public IReadOnlyList<string> RemoveOldVersions(FlavourEntry current, Func<string, bool> inUse, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        var keep = TryGetVerified(current, ct);
        if (keep is null)
        {
            return [];
        }

        var removed = new List<string>();
        foreach (var dir in new DirectoryInfo(FlavourFolder(current.Id)).EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var full = Normalize(dir.FullName);
            if (string.Equals(full, keep, PathComparison) || full.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase) || inUse(full))
            {
                continue;
            }

            if (IsOwnedFolder(full, current.Id, out _))
            {
                DeleteOwnedFolder(full, current.Id);
                removed.Add(full);
            }
        }

        return removed;
    }

    /// <summary>Deletes the host's own partial download of a pin (a plain file, never a link).</summary>
    public bool DeletePart(FlavourEntry e)
    {
        var part = PartPath(e);
        var info = new FileInfo(part);
        if (!info.Exists)
        {
            return false;
        }

        AssertNotLinked(Root);
        AssertNotLinked(FlavourFolder(e.Id));
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new FlavourSafetyException($"'{part}' is a link; it was left in place.");
        }

        File.Delete(part);
        return true;
    }

    internal static string HashFile(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return HashStream(fs, ct);
    }

    internal static string HashStream(Stream s, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = s.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal bool IsStrictlyInside(string fullPath, string parent) =>
        Normalize(fullPath).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, PathComparison);

    private void AssertOwnedFolder(string folder, string flavourId)
    {
        var parent = FlavourFolder(flavourId);
        if (!IsStrictlyInside(folder, Root) || !string.Equals(Path.GetDirectoryName(folder), parent, PathComparison))
        {
            throw new FlavourSafetyException($"'{folder}' is not a version folder inside the engine store; nothing was touched.");
        }

        if (!Directory.Exists(folder))
        {
            throw new FlavourSafetyException($"'{folder}' does not exist.");
        }

        AssertNotLinked(Root);
        AssertNotLinked(parent);
        AssertNotLinked(folder);
        var marker = ReadMarker(folder);
        if (marker is null || marker.Flavour != flavourId)
        {
            throw new FlavourSafetyException($"'{folder}' has no engine marker for '{flavourId}'; nothing was touched.");
        }
    }

    private static FlavourMarker? ReadMarker(string folder)
    {
        var path = Path.Combine(folder, MarkerFileName);
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 4096)
        {
            return null;
        }

        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllBytes(path), FlavourJsonContext.Default.FlavourMarker);
            return m is { Kind: MarkerKind } && m.Flavour.Length > 0 ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lists a folder tree without following links; throws when any entry is a junction or link.</summary>
    private static IEnumerable<(FileSystemInfo Info, string Relative)> Walk(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
                {
                    throw new FlavourSafetyException($"'{entry.FullName}' is a junction or link; nothing was touched.");
                }

                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                yield return (entry, relative);
                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                }
            }
        }
    }

    private static void AssertNotLinked(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists && (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget is not null))
        {
            throw new FlavourSafetyException($"'{path}' is a junction or link; nothing was touched.");
        }
    }

    private static string CheckedId(string flavourId)
    {
        if (flavourId is null || flavourId.Length is 0 or > 48 || !flavourId.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') || flavourId[0] == '-')
        {
            throw new FlavourSafetyException($"'{flavourId}' is not a valid engine flavour id.");
        }

        return flavourId;
    }
}
