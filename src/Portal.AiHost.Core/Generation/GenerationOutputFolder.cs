using System.Text.RegularExpressions;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Generation;

/// <summary>
/// The host-owned folder for generated pictures and clips. Layout:
/// <c>&lt;root&gt;\images\</c> and <c>&lt;root&gt;\videos\</c> hold finished files under unique names (never overwritten);
/// <c>&lt;root&gt;\.work\&lt;job id&gt;\</c> holds one running job's partial output, preview, start picture and state
/// marker; <c>&lt;root&gt;\recovered\</c> holds leftovers of a crash the host could not judge.
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

    /// <summary>The job's state marker in its work folder: one of the <c>State*</c> words.</summary>
    public const string StateName = "job.state";
    public const string RecoveredFolderName = "recovered";

    /// <summary>The engine is running: an output here is partial.</summary>
    public const string StateRunning = "running";

    /// <summary>The engine finished and wrote its output; only the move to the finished folder is left.</summary>
    public const string StateFinished = "finished";

    /// <summary>The job was cancelled: an output here is partial.</summary>
    public const string StateCancelled = "cancelled";

    /// <summary>The engine failed: an output here may or may not be usable, so a sweep keeps it.</summary>
    public const string StateFailed = "failed";

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

    /// <summary>Records the job's state in its work folder (atomic replace of the host's own marker file).</summary>
    public void MarkState(string workDir, string state)
    {
        RequireWorkDir(workDir);
        AtomicFile.WriteAllBytes(Path.Combine(workDir, StateName), System.Text.Encoding.UTF8.GetBytes(state));
    }

    /// <summary>The state marker of a work folder, or null when it is missing or unreadable.</summary>
    public static string? ReadState(string workDir)
    {
        try
        {
            var path = Path.Combine(workDir, StateName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

        // The state marker goes only once no output is left, so a later sweep can still judge a leftover.
        if (!OwnedExtensions.Any(ext => File.Exists(PartialPath(workDir, ext))))
        {
            TryDelete(Path.Combine(workDir, StateName));
        }

        TryDeleteEmptyFolder(workDir);
    }

    /// <summary>What a sweep did with the leftovers it found.</summary>
    public sealed record SweepResult(int Folders, IReadOnlyList<string> Published, IReadOnlyList<string> Recovered, int DeletedPartials);

    /// <summary>
    /// At start, before any job runs: work folders left by a crash. Each leftover output is judged by the job's state
    /// marker. Finished and not empty: moved to the finished folder under a unique name. Running or cancelled: clearly
    /// partial, deleted. Anything else (no marker, failed, empty, unreadable): kept, moved to <c>recovered\</c> under a
    /// unique name. Then the preview, start picture, marker and empty folder go. Folders whose name is not a job id are
    /// left alone.
    /// </summary>
    public SweepResult SweepStaleWork()
    {
        var published = new List<string>();
        var recovered = new List<string>();
        var deleted = 0;
        var folders = 0;
        if (!Directory.Exists(WorkRoot))
        {
            return new SweepResult(0, published, recovered, 0);
        }

        foreach (var dir in Directory.EnumerateDirectories(WorkRoot))
        {
            var jobId = Path.GetFileName(dir);
            if (!JobIdPattern().IsMatch(jobId))
            {
                continue;
            }

            folders++;
            var state = ReadState(dir);
            foreach (var ext in OwnedExtensions)
            {
                var partial = PartialPath(dir, ext);
                FileInfo info;
                try
                {
                    info = new FileInfo(partial);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                try
                {
                    if (state == StateFinished && info.Length > 0)
                    {
                        published.Add(Publish(partial, ext == ".avi" ? "video" : "image", jobId, info.LastWriteTimeUtc));
                    }
                    else if (state is StateRunning or StateCancelled)
                    {
                        TryDelete(partial); // clearly partial
                        deleted++;
                    }
                    else
                    {
                        recovered.Add(MoveToRecovered(partial, jobId));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked: left in place for the next start.
                }
            }

            CleanWorkFolder(dir);
        }

        return new SweepResult(folders, published, recovered, deleted);
    }

    /// <summary>Moves a leftover the host cannot judge to <c>recovered\</c> under a new unique name (never replacing).</summary>
    private string MoveToRecovered(string partialPath, string jobId)
    {
        RequireOwnedPartial(partialPath);
        var folder = Path.Combine(Root, RecoveredFolderName);
        Directory.CreateDirectory(folder);
        var target = AtomicFile.UniquePath(folder, jobId + Path.GetExtension(partialPath));
        File.Move(partialPath, target, overwrite: false);
        return target;
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
