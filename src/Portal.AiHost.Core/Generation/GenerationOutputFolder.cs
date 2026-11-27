using System.Text.RegularExpressions;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Generation;

/// <summary>
/// The host-owned folder for generated pictures and clips. Layout:
/// <c>&lt;root&gt;\images\</c> and <c>&lt;root&gt;\videos\</c> hold finished files under unique names (never overwritten);
/// <c>&lt;root&gt;\.work\&lt;job id&gt;\</c> holds one running job's partial output, preview and start picture.
/// Only the fixed file names this class creates inside a job's work folder are ever deleted, plus that work folder
/// itself once empty. Finished files are never deleted or replaced by the host, and nothing outside the root is touched.
/// </summary>
public sealed partial class GenerationOutputFolder
{
    public const string MarkerName = ".portal-ai-host-generated";
    public const string WorkFolderName = ".work";
    public const string PartialStem = "output";
    public const string PreviewName = "preview.png";
    public const string InitStem = "init";

    private static readonly string[] OwnedExtensions = [".png", ".avi", ".jpg"];

    [GeneratedRegex("^gen_[0-9a-f]{16,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();

    public GenerationOutputFolder(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        var marker = Path.Combine(Root, MarkerName);
        if (!File.Exists(marker))
        {
            AtomicFile.WriteAllBytes(marker, "portal-ai-host-generated"u8);
        }
    }

    public string Root { get; }

    public string WorkRoot => Path.Combine(Root, WorkFolderName);

    /// <summary>Folder of finished files of a kind (images or videos).</summary>
    public string FinishedFolder(string kind) => Path.Combine(Root, kind == "video" ? "videos" : "images");

    /// <summary>Creates the job's own new work folder. Fails if it exists already (a job id is never reused).</summary>
    public string CreateWorkFolder(string jobId)
    {
        RequireJobId(jobId);
        var dir = Path.Combine(WorkRoot, jobId);
        if (Directory.Exists(dir) || File.Exists(dir))
        {
            throw new IOException("The work folder of this job exists already.");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string PartialPath(string workDir, string extension) => Path.Combine(workDir, PartialStem + extension);

    public static string PreviewPath(string workDir) => Path.Combine(workDir, PreviewName);

    /// <summary>Writes the start picture into the job's work folder (atomic, new file) and returns its path.</summary>
    public string WriteInitImage(string workDir, ReadOnlySpan<byte> bytes, string extension)
    {
        RequireWorkDir(workDir);
        var path = Path.Combine(workDir, InitStem + extension);
        if (File.Exists(path))
        {
            throw new IOException("The start picture of this job exists already.");
        }

        AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Moves the finished partial output to a new unique name in the finished folder and returns that path. The
    /// move stays on one volume (a rename) and never replaces a file: an existing name gets " (2)", " (3)" and so on.
    /// </summary>
    public string Publish(string partialPath, string kind, string jobId, DateTimeOffset createdAt)
    {
        RequireOwnedPartial(partialPath);
        RequireJobId(jobId);
        var folder = FinishedFolder(kind);
        Directory.CreateDirectory(folder);
        var name = $"{createdAt.ToLocalTime():yyyyMMdd-HHmmss}-{jobId}{Path.GetExtension(partialPath)}";
        for (var attempt = 0; ; attempt++)
        {
            var target = AtomicFile.UniquePath(folder, name);
            try
            {
                File.Move(partialPath, target, overwrite: false);
                return target;
            }
            catch (IOException) when (attempt < 5 && File.Exists(target) && File.Exists(partialPath))
            {
                // Another file took the name between the check and the move: try the next free name.
            }
        }
    }

    /// <summary>Deletes the partial output of a job (cancel or failure). Only the exact owned file is removed.</summary>
    public void DeletePartial(string partialPath)
    {
        RequireOwnedPartial(partialPath);
        TryDelete(partialPath);
    }

    /// <summary>
    /// Removes the job's preview and start picture and then the work folder, only if it is empty afterwards. A
    /// partial output that is still there (for example a failed publish) is left and named by the next sweep.
    /// </summary>
    public void CleanWorkFolder(string workDir)
    {
        RequireWorkDir(workDir);
        TryDelete(PreviewPath(workDir));
        foreach (var ext in OwnedExtensions)
        {
            TryDelete(Path.Combine(workDir, InitStem + ext));
        }

        TryDeleteEmptyFolder(workDir);
    }

    /// <summary>
    /// At start: work folders left by a crash. Their partial outputs, previews and start pictures (the fixed names
    /// above) are removed, then each folder if it is empty. Folders whose name is not a job id are left alone.
    /// </summary>
    public int SweepStaleWork()
    {
        if (!Directory.Exists(WorkRoot))
        {
            return 0;
        }

        var swept = 0;
        foreach (var dir in Directory.EnumerateDirectories(WorkRoot))
        {
            if (!JobIdPattern().IsMatch(Path.GetFileName(dir)))
            {
                continue;
            }

            foreach (var ext in OwnedExtensions)
            {
                TryDelete(PartialPath(dir, ext));
            }

            CleanWorkFolder(dir);
            swept++;
        }

        return swept;
    }

    private void RequireWorkDir(string workDir)
    {
        var full = Path.GetFullPath(workDir);
        if (!string.Equals(Path.GetDirectoryName(full), WorkRoot, StringComparison.OrdinalIgnoreCase) ||
            !JobIdPattern().IsMatch(Path.GetFileName(full)))
        {
            throw new InvalidOperationException("Not a work folder of this output folder.");
        }
    }

    private void RequireOwnedPartial(string partialPath)
    {
        var full = Path.GetFullPath(partialPath);
        RequireWorkDir(Path.GetDirectoryName(full)!);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (Path.GetFileNameWithoutExtension(full) != PartialStem || !OwnedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("Not a partial output of this output folder.");
        }
    }

    private static void RequireJobId(string jobId)
    {
        if (!JobIdPattern().IsMatch(jobId))
        {
            throw new ArgumentException("Not a job id.", nameof(jobId));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Locked (for example by a virus scanner): the next start's sweep tries again.
        }
    }

    private static void TryDeleteEmptyFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
