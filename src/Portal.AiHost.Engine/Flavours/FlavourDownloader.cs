using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Engine.Flavours;

public sealed class FlavourDownloadOptions
{
    public string UserAgent { get; set; } = "PortalAIHost";

    /// <summary>Tries per URL before the next pinned URL is used.</summary>
    public int AttemptsPerUrl { get; set; } = 3;
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>Extra free space required on top of the zip and the unpacked files.</summary>
    public double HeadroomFraction { get; set; } = 0.10;
    public int BufferBytes { get; set; } = 1 << 20;

    /// <summary>Waits between tries; replaced in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
}

/// <summary>States: checking, downloading, verifying, extracting, installed.</summary>
public sealed record FlavourProgress(string FlavourId, string State, long BytesDone, long BytesTotal, string? Message = null);

/// <summary>
/// Why an engine install stopped. Codes: placeholder, invalid, disk_space, size_mismatch, hash_mismatch,
/// bad_archive, unsafe, download_failed.
/// </summary>
public sealed class FlavourInstallException(string code, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Installs a pinned engine flavour: resumable download to the host's own .part file from the pinned URLs only
/// (second URL when the first fails), size and SHA-256 check before anything is extracted, extraction into a new
/// unique staging folder with every file checked against the pinned list, then one rename into place. Runs on the
/// thread pool; never touches anything outside the flavour store.
/// </summary>
public sealed class FlavourDownloader
{
    private readonly HttpClient _http;
    private readonly FlavourStore _store;
    private readonly IDiskInfo _disk;
    private readonly FlavourDownloadOptions _o;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public FlavourDownloader(HttpClient http, FlavourStore store, IDiskInfo? disk = null, FlavourDownloadOptions? options = null)
    {
        _http = http;
        _store = store;
        _disk = disk ?? SystemDiskInfo.Instance;
        _o = options ?? new FlavourDownloadOptions();
    }

    /// <summary>Space the install needs on the store's volume: the rest of the zip, the files, and the headroom.</summary>
    public long NeededBytes(FlavourEntry e, long alreadyDownloaded = 0) =>
        Math.Max(0, e.Size - alreadyDownloaded) + e.UnpackedSize + (long)((e.Size + e.UnpackedSize) * _o.HeadroomFraction);

    /// <summary>Installs the entry (not its requirements) and returns the verified version folder.</summary>
    public Task<string> InstallAsync(FlavourEntry e, IProgress<FlavourProgress>? progress, CancellationToken ct) =>
        Task.Run(() => InstallCoreAsync(e, progress, ct), ct);

    /// <summary>Drops the host's own partial download of the entry (after the user cancels for good).</summary>
    public bool DiscardDownload(FlavourEntry e) => _store.DeletePart(e);

    private async Task<string> InstallCoreAsync(FlavourEntry e, IProgress<FlavourProgress>? progress, CancellationToken ct)
    {
        if (FlavourManifest.Problem(e) is { } problem)
        {
            throw new FlavourInstallException(problem == "placeholder" ? "placeholder" : "invalid",
                $"Engine flavour '{e.Id}' is not installable ({problem}).");
        }

        var gate = _gates.GetOrAdd(e.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new FlavourProgress(e.Id, "checking", 0, e.Size));
            if (_store.TryGetVerified(e, ct) is { } existing)
            {
                progress?.Report(new FlavourProgress(e.Id, "installed", e.Size, e.Size));
                return existing;
            }

            _store.EnsureFlavourFolder(e.Id);
            _store.CleanStaging(e.Id);
            var part = _store.PartPath(e);
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            var needed = NeededBytes(e, Math.Min(have, e.Size));
            var free = _disk.GetFreeBytes(_store.Root);
            if (free < needed)
            {
                throw new FlavourInstallException("disk_space", $"Not enough free space for '{e.Id}': {needed} bytes needed, {free} free.");
            }

            await DownloadAsync(e, part, progress, ct).ConfigureAwait(false);
            progress?.Report(new FlavourProgress(e.Id, "verifying", e.Size, e.Size));
            var folder = ExtractVerified(e, part, progress, ct);
            _store.DeletePart(e);
            progress?.Report(new FlavourProgress(e.Id, "installed", e.Size, e.Size));
            return folder;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAsync(FlavourEntry e, string part, IProgress<FlavourProgress>? progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in e.Urls)
        {
            for (var attempt = 1; attempt <= Math.Max(1, _o.AttemptsPerUrl); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadFromAsync(e, new Uri(url), part, progress, ct).ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
                {
                    last = ex;
                    break; // this address no longer has the file: try the next pinned address
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
                {
                    last = ex;
                    if (attempt < _o.AttemptsPerUrl)
                    {
                        var delays = _o.RetryDelays;
                        var wait = delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(attempt - 1, delays.Count - 1)];
                        await _o.Delay(wait, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        throw new FlavourInstallException("download_failed", $"Engine flavour '{e.Id}' could not be downloaded.", last);
    }

    private static bool IsRetryable(Exception ex) => ex switch
    {
        FlavourInstallException => false,
        FlavourSafetyException => false,
        HttpRequestException => true,
        TaskCanceledException => true,
        IOException io => !IsDiskFull(io),
        _ => false,
    };

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27;

    private async Task DownloadFromAsync(FlavourEntry e, Uri url, string part, IProgress<FlavourProgress>? progress, CancellationToken ct)
    {
        try
        {
            await DownloadFromCoreAsync(e, url, part, progress, ct).ConfigureAwait(false);
        }
        catch (FlavourInstallException ex) when (ex.Code == "size_mismatch")
        {
            _store.DeletePart(e); // the .part is closed again here
            throw;
        }
    }

    private async Task DownloadFromCoreAsync(FlavourEntry e, Uri url, string part, IProgress<FlavourProgress>? progress, CancellationToken ct)
    {
        var info = new FileInfo(part);
        if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new FlavourSafetyException($"'{part}' is a link; nothing was downloaded.");
        }

        if (info.Exists && info.Length > e.Size)
        {
            _store.DeletePart(e);
        }

        // The .part file is the host's own; it is opened without sharing so a second process cannot write to it.
        await using var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var have = fs.Length;
        if (have == e.Size)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_o.UserAgent);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            fs.SetLength(0); // own .part: start over
            throw new IOException("The server refused to resume; starting again.");
        }

        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != have)
            {
                fs.SetLength(0);
                throw new IOException("The server resumed at the wrong place; starting again.");
            }

            if (range.Length is { } total && total != e.Size)
            {
                throw SizeMismatch(e, total);
            }
        }
        else
        {
            have = 0;
            fs.SetLength(0); // the server sent the whole file: the own .part starts over
            if (response.Content.Headers.ContentLength is { } length && length != e.Size)
            {
                throw SizeMismatch(e, length);
            }
        }

        fs.Seek(have, SeekOrigin.Begin);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Max(4096, _o.BufferBytes)];
        var done = have;
        var lastReport = 0L;
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (done + n > e.Size)
            {
                throw SizeMismatch(e, done + n);
            }

            await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (done - lastReport >= 4 << 20 || done == e.Size)
            {
                lastReport = done;
                progress?.Report(new FlavourProgress(e.Id, "downloading", done, e.Size));
            }
        }

        await fs.FlushAsync(ct).ConfigureAwait(false);
        if (done != e.Size)
        {
            throw new IOException($"The download of '{e.Id}' ended early ({done} of {e.Size} bytes).");
        }
    }

    private static FlavourInstallException SizeMismatch(FlavourEntry e, long served)
    {
        return new FlavourInstallException("size_mismatch", $"The server offered {served} bytes for '{e.Id}', the pin says {e.Size}; nothing was installed.");
    }

    /// <summary>Checks the zip, extracts it into a new staging folder, checks every file, then renames it into place.</summary>
    private string ExtractVerified(FlavourEntry e, string part, IProgress<FlavourProgress>? progress, CancellationToken ct)
    {
        // Held open without write sharing from the hash check to the end of extraction: what was checked is what is unpacked.
        using var zipFile = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (zipFile.Length != e.Size)
        {
            var length = zipFile.Length;
            zipFile.Dispose();
            _store.DeletePart(e);
            throw SizeMismatch(e, length);
        }

        if (!string.Equals(FlavourStore.HashStream(zipFile, ct), e.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            zipFile.Dispose();
            _store.DeletePart(e);
            throw new FlavourInstallException("hash_mismatch", $"The download of '{e.Id}' does not match its SHA-256; it was deleted and nothing was installed.");
        }

        zipFile.Seek(0, SeekOrigin.Begin);
        progress?.Report(new FlavourProgress(e.Id, "extracting", e.Size, e.Size));
        var staging = _store.CreateStaging(e);
        try
        {
            ExtractInto(e, zipFile, staging, ct);
            var check = _store.Verify(staging, e, ct);
            if (!check.Ok)
            {
                throw new FlavourInstallException("bad_archive", $"'{e.Id}' failed its file check: {check.Problem}.");
            }

            var final = _store.VersionFolder(e);
            if (Directory.Exists(final))
            {
                if (_store.Verify(final, e, ct).Ok)
                {
                    _store.DeleteOwnedFolder(staging, e.Id);
                    return final;
                }

                // A damaged earlier install of this same pin: removed only when it carries the host's marker.
                if (!_store.IsOwnedFolder(final, e.Id, out var reason))
                {
                    throw new FlavourInstallException("unsafe", $"'{final}' exists and is not the host's own ({reason}); it was left in place.");
                }

                _store.DeleteOwnedFolder(final, e.Id);
            }
            else if (File.Exists(final))
            {
                throw new FlavourInstallException("unsafe", $"'{final}' exists as a file; it was left in place.");
            }

            Directory.Move(staging, final); // one rename within the flavour folder
            return final;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    _store.DeleteOwnedFolder(staging, e.Id);
                }
                catch (IOException)
                {
                    // Left for CleanStaging on the next install; it keeps its marker.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

    private static void ExtractInto(FlavourEntry e, Stream zipStream, string staging, CancellationToken ct)
    {
        var expected = e.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':'))
            {
                throw new FlavourInstallException("unsafe", $"Archive entry '{name}' has an unsafe name.");
            }

            if (name.EndsWith('/'))
            {
                if (!FlavourManifest.IsSafeRelativePath(name.TrimEnd('/')))
                {
                    throw new FlavourInstallException("unsafe", $"Archive folder '{name}' has an unsafe name.");
                }

                continue; // folders are created for the files they hold
            }

            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new FlavourInstallException("unsafe", $"Archive entry '{name}' is a link.");
            }

            if (!FlavourManifest.IsSafeRelativePath(name))
            {
                throw new FlavourInstallException("unsafe", $"Archive entry '{name}' has an unsafe path.");
            }

            if (!expected.TryGetValue(name, out var file) || !seen.Add(name))
            {
                throw new FlavourInstallException("bad_archive", $"Archive entry '{name}' is not in the pinned file list.");
            }

            if (entry.Length != file.Size)
            {
                throw new FlavourInstallException("bad_archive", $"Archive entry '{name}' has the wrong size.");
            }

            total += file.Size;
            if (total > e.UnpackedSize)
            {
                throw new FlavourInstallException("bad_archive", $"'{e.Id}' unpacks to more than its pinned size.");
            }

            var target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(FlavourStore.Normalize(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlavourInstallException("unsafe", $"Archive entry '{name}' points outside its folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            using (var src = entry.Open())
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = src.Read(buffer)) > 0)
                {
                    written += n;
                    if (written > file.Size)
                    {
                        throw new FlavourInstallException("bad_archive", $"Archive entry '{name}' is larger than pinned.");
                    }

                    sha.AppendData(buffer, 0, n);
                    dst.Write(buffer, 0, n);
                }
            }

            if (written != file.Size || !string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlavourInstallException("bad_archive", $"Archive entry '{name}' does not match its pinned SHA-256.");
            }
        }

        if (seen.Count != expected.Count)
        {
            throw new FlavourInstallException("bad_archive", $"'{e.Id}' is missing pinned files.");
        }
    }
}
