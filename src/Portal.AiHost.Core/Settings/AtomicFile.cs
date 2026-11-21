namespace Portal.AiHost.Core.Settings;

/// <summary>
/// Crash-safe file writes for the host's own state files (settings, client registry, host key).
/// The new content goes to a unique temp file next to the target, is flushed to disk, and only then
/// replaces the target in one step. The existing file is never opened for writing or truncated, so a
/// crash or a failed write leaves the previous version intact.
/// </summary>
public static class AtomicFile
{
    /// <summary>Suffix of the temp files this class creates. Only files with it are ever deleted.</summary>
    public const string TempSuffix = ".pah-tmp";

    /// <summary>Writes the bytes to <paramref name="path"/> atomically.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        Write(path, stream => stream.Write(copy));
    }

    /// <summary>
    /// Writes to <paramref name="path"/> atomically. <paramref name="write"/> fills a fresh temp file; if it
    /// throws, the temp file is removed and the target is left as it was.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? throw new ArgumentException("Path has no directory.", nameof(path));
        Directory.CreateDirectory(dir);

        // Unique name + CreateNew: we never open (let alone truncate) a file we did not just create.
        var temp = full + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        var created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                created = true;
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                // ReplaceFile: swaps the content in one step; no backup copy is kept.
                File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full, overwrite: false);
            }

            created = false;
        }
        finally
        {
            if (created)
            {
                TryDeleteOwnTemp(temp);
            }
        }
    }

    /// <summary>
    /// Returns a path in the same folder that does not exist yet: "name.ext", then "name (2).ext", "name (3).ext"...
    /// Used for backups so that an existing file is never overwritten.
    /// </summary>
    public static string UniquePath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(directory, fileName);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(directory, $"{stem} ({n}){ext}");
        }

        return candidate;
    }

    /// <summary>Copies a file to a new unique backup path (never overwrites) and returns that path.</summary>
    public static string CopyToUniqueBackup(string source, string backupFileName)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var target = UniquePath(dir, backupFileName);
        File.Copy(source, target, overwrite: false);
        return target;
    }

    private static void TryDeleteOwnTemp(string temp)
    {
        // Only the exact temp file created above, identified by its unique name and suffix.
        if (!temp.EndsWith(TempSuffix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            File.Delete(temp);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
