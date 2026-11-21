using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Download;

/// <summary>Keeps the PC awake while a download runs (Windows: a system-required power request).</summary>
public interface ISleepBlocker
{
    IDisposable Acquire(string reason);
}

public sealed class DownloadOptions
{
    /// <summary>Sent on every request as "PortalAIHost/&lt;version&gt;". No tokens are ever sent.</summary>
    public string UserAgent { get; init; } = "PortalAIHost/" + HostVersion();

    /// <summary>Waits between network retries; after the last one, <see cref="RetryEvery"/> repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Network attempts per part before giving up; null retries until cancelled.</summary>
    public int? MaxAttempts { get; init; }

    public int ParallelParts { get; init; } = 2;
    public long DiskCheckIntervalBytes { get; init; } = 1L << 30;
    public long FixedHeadroomBytes { get; init; } = 2L << 30;
    public double HeadroomFraction { get; init; } = 0.02;
    public int BufferBytes { get; init; } = 1 << 20;
    public bool PreventSleep { get; init; } = true;

    /// <summary>Delay function (replaced in tests).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    private static string HostVersion()
    {
        var v = typeof(DownloadOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DownloadOptions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? v[..plus] : v;
    }
}

/// <summary>Progress of one model (all parts). State: queued, downloading, verifying, done, paused, failed.</summary>
public sealed record DownloadProgress(string ModelId, string State, long BytesDone, long BytesTotal, double? EtaSeconds, string? Message);

public class DownloadException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class NotDownloadableException(string message) : DownloadException(message);

public sealed class InsufficientDiskSpaceException(long neededBytes, long freeBytes)
    : DownloadException($"This download needs {neededBytes / 1e9:0.0} GB free; the drive has {freeBytes / 1e9:0.0} GB.")
{
    public long NeededBytes { get; } = neededBytes;
    public long FreeBytes { get; } = freeBytes;
}

/// <summary>The file failed its hash twice (once after a restart from zero): "file damaged at source".</summary>
public sealed class ModelFileDamagedException(string fileName)
    : DownloadException($"'{fileName}' is damaged at the source (hash mismatch twice).");

internal sealed class ResumeState
{
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public string? Etag { get; init; }
    public long BytesDone { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ResumeState))]
internal sealed partial class DownloadJsonContext : JsonSerializerContext;

/// <summary>
/// Resumable downloader for 60-250 GB models. Each part of a split GGUF is its own resumable unit, written to a
/// host-created ".part" file (via the store) with a ".part.json" sidecar, hashed incrementally, and committed into
/// place only when its SHA-256 matches the catalogue. Cancelling the token pauses (the part stays for resume);
/// <see cref="CancelAsync"/> discards an unfinished download.
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly IModelStoreWriter _store;
    private readonly IDiskInfo _disk;
    private readonly DownloadOptions _options;
    private readonly ISleepBlocker? _sleep;

    public ModelDownloader(HttpClient http, IModelStoreWriter store, IDiskInfo? disk = null, DownloadOptions? options = null, ISleepBlocker? sleep = null)
    {
        _http = http;
        _store = store;
        _disk = disk ?? SystemDiskInfo.Instance;
        _options = options ?? new DownloadOptions();
        _sleep = sleep;
    }

    public async Task<StoredModel> DownloadAsync(CatalogueEntry entry, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!entry.IsDownloadable)
        {
            throw new NotDownloadableException($"'{entry.Id}' has no pinned commit or verified hashes in the catalogue.");
        }

        await _store.EnsureModelAsync(entry.Id, entry.DisplayName, entry.Kind, entry.Engine, ct).ConfigureAwait(false);
        var stored = await _store.GetAsync(entry.Id, ct).ConfigureAwait(false);
        var pending = entry.Files
            .Where(f => stored?.Files.FirstOrDefault(s => s.Name == f.Name) is not { State: not StoredFileState.Partial })
            .ToList();

        var total = entry.TotalBytes;
        long alreadyThere = total - pending.Sum(f => f.Size);
        foreach (var f in pending)
        {
            var partial = stored?.Files.FirstOrDefault(s => s.Name == f.Name);
            alreadyThere += Math.Min(partial?.SizeBytes ?? 0, f.Size);
        }

        var tracker = new ProgressTracker(entry.Id, total, alreadyThere, progress);
        CheckDisk(total - alreadyThere);

        using var awake = _options.PreventSleep ? _sleep?.Acquire($"Downloading {entry.DisplayName}") : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var parallel = new SemaphoreSlim(Math.Max(1, _options.ParallelParts));
        tracker.Report("downloading");
        try
        {
            var tasks = pending.Select(async f =>
            {
                await parallel.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await DownloadPartAsync(entry, f, tracker, linked.Token).ConfigureAwait(false);
                }
                catch
                {
                    await linked.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    parallel.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            tracker.Report("paused");
            throw;
        }
        catch (Exception ex)
        {
            tracker.Report("failed", ex.Message);
            throw;
        }

        tracker.Report("done");
        return (await _store.GetAsync(entry.Id, ct).ConfigureAwait(false))!;
    }

    /// <summary>Discards an unfinished download (its .part files and finished parts). Finished models are refused.</summary>
    public async Task CancelAsync(string modelId, CancellationToken ct)
    {
        var model = await _store.GetAsync(modelId, ct).ConfigureAwait(false);
        if (model is null)
        {
            return;
        }

        if (model.IsComplete || model.Origin != ModelOrigin.Catalogue)
        {
            throw new InvalidOperationException($"'{modelId}' is not an unfinished download; use Remove.");
        }

        await _store.RemoveAsync(modelId, ct).ConfigureAwait(false);
    }

    private void CheckDisk(long remaining)
    {
        var needed = remaining + (long)(remaining * _options.HeadroomFraction) + _options.FixedHeadroomBytes;
        var free = _disk.GetFreeBytes(_store.Location.Path);
        if (free < needed)
        {
            throw new InsufficientDiskSpaceException(needed, free);
        }
    }

    private async Task DownloadPartAsync(CatalogueEntry entry, CatalogueFile file, ProgressTracker tracker, CancellationToken ct)
    {
        var restartedFromZero = false;
        while (true)
        {
            var partPath = await _store.BeginFileAsync(entry.Id, file.Name, ct).ConfigureAwait(false);
            var hash = await TransferWithRetriesAsync(entry.Id, file, partPath, tracker, ct).ConfigureAwait(false);
            if (string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await _store.CommitFileAsync(entry.Id, partPath, hash, ct).ConfigureAwait(false);
                return;
            }

            // Hash mismatch: delete our own .part, retry once from zero, then stop.
            tracker.Add(-file.Size);
            await _store.DiscardPartAsync(entry.Id, partPath, ct).ConfigureAwait(false);
            if (restartedFromZero)
            {
                throw new ModelFileDamagedException(file.Name);
            }

            restartedFromZero = true;
        }
    }

    private async Task<string> TransferWithRetriesAsync(
        string modelId, CatalogueFile file, string partPath, ProgressTracker tracker, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TransferOnceAsync(modelId, file, partPath, tracker, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                if (_options.MaxAttempts is { } max && attempt >= max)
                {
                    throw new DownloadException($"Download of '{file.Name}' kept failing: {ex.Message}", ex);
                }

                var delay = attempt <= _options.RetryDelays.Count ? _options.RetryDelays[attempt - 1] : _options.RetryEvery;
                tracker.Report("downloading", $"Connection problem, retrying in {delay.TotalSeconds:0} s");
                await _options.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        DownloadException => false,
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int?)h.StatusCode >= 500,
        TaskCanceledException => true, // HttpClient timeout (the caller's token is checked separately)
        IOException => true,
        _ => false,
    };

    private async Task<string> TransferOnceAsync(
        string modelId, CatalogueFile file, string partPath, ProgressTracker tracker, CancellationToken ct)
    {
        var url = file.Urls[0];
        var state = await ReadStateAsync(modelId, partPath, ct).ConfigureAwait(false);
        var length = new FileInfo(partPath).Length;
        if (state is not null && (state.Size != file.Size || !string.Equals(state.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            state = null;
            await ResetAsync(modelId, partPath, length, tracker, ct).ConfigureAwait(false);
            length = 0;
        }

        if (length > file.Size)
        {
            await ResetAsync(modelId, partPath, length, tracker, ct).ConfigureAwait(false);
            length = 0;
        }

        // Re-hash the existing prefix so the final hash covers every byte on disk.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (length > 0)
        {
            await HashPrefixAsync(partPath, length, sha, ct).ConfigureAwait(false);
        }

        if (length == file.Size)
        {
            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        if (length > 0)
        {
            request.Headers.Range = new RangeHeaderValue(length, null);
            if (state?.Etag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            }
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            await ResetAsync(modelId, partPath, length, tracker, ct).ConfigureAwait(false);
            throw new IOException("Server refused the resume range; restarting the part.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            if (code is 408 or 429 || code >= 500)
            {
                throw new HttpRequestException($"Server returned {code}.", null, response.StatusCode);
            }

            throw new DownloadException($"Server returned {code} for '{file.Name}'.");
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != length || (range.Length is { } len && len != file.Size))
            {
                throw new DownloadException($"Server sent an unexpected range for '{file.Name}'.");
            }
        }
        else
        {
            // 200: full body (resource changed or range ignored). Start this part again from zero.
            if (length > 0)
            {
                await ResetAsync(modelId, partPath, length, tracker, ct).ConfigureAwait(false);
                sha.GetHashAndReset();
                length = 0;
            }

            if (response.Content.Headers.ContentLength is { } cl && cl != file.Size)
            {
                throw new DownloadException($"'{file.Name}' is {cl} bytes at the source; the catalogue says {file.Size}.");
            }
        }

        var etagOut = response.Headers.ETag?.ToString();
        await WriteStateAsync(modelId, partPath, url, file, etagOut, length, ct).ConfigureAwait(false);

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.Read, _options.BufferBytes, useAsync: true);
        output.Seek(length, SeekOrigin.Begin);
        var buffer = new byte[_options.BufferBytes];
        var sinceCheck = 0L;
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (length + n > file.Size)
            {
                throw new DownloadException($"'{file.Name}' is larger at the source than the catalogue says.");
            }

            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            sha.AppendData(buffer, 0, n);
            length += n;
            sinceCheck += n;
            tracker.Add(n);
            if (sinceCheck >= _options.DiskCheckIntervalBytes)
            {
                sinceCheck = 0;
                await output.FlushAsync(ct).ConfigureAwait(false);
                await WriteStateAsync(modelId, partPath, url, file, etagOut, length, ct).ConfigureAwait(false);
                CheckDisk(tracker.Total - tracker.Done);
            }
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (length != file.Size)
        {
            await WriteStateAsync(modelId, partPath, url, file, etagOut, length, ct).ConfigureAwait(false);
            throw new IOException($"Connection closed after {length} of {file.Size} bytes.");
        }

        tracker.Report("verifying");
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private async Task ResetAsync(string modelId, string partPath, long length, ProgressTracker tracker, CancellationToken ct)
    {
        tracker.Add(-length);
        await _store.ResetPartAsync(modelId, partPath, ct).ConfigureAwait(false);
    }

    private static async Task HashPrefixAsync(string path, long length, IncrementalHash sha, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        var left = length;
        while (left > 0)
        {
            var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new IOException("Partial file shrank while resuming.");
            }

            sha.AppendData(buffer, 0, n);
            left -= n;
        }
    }

    private async Task<ResumeState?> ReadStateAsync(string modelId, string partPath, CancellationToken ct)
    {
        var json = await _store.ReadResumeStateAsync(modelId, partPath, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, DownloadJsonContext.Default.ResumeState);
        }
        catch (JsonException)
        {
            return null; // a damaged sidecar only costs the ETag; the prefix is re-hashed anyway
        }
    }

    private Task WriteStateAsync(string modelId, string partPath, string url, CatalogueFile file, string? etag, long done, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new ResumeState { Url = url, Size = file.Size, Sha256 = file.Sha256, Etag = etag, BytesDone = done },
            DownloadJsonContext.Default.ResumeState);
        return _store.WriteResumeStateAsync(modelId, partPath, json, ct);
    }

    private sealed class ProgressTracker(string modelId, long total, long initial, IProgress<DownloadProgress>? progress)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _startDone = initial;
        private long _done = initial;
        private long _lastReportTicks;

        public long Total => total;
        public long Done => Interlocked.Read(ref _done);

        public void Add(long bytes)
        {
            Interlocked.Add(ref _done, bytes);
            var now = _clock.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref _lastReportTicks) >= 250)
            {
                Interlocked.Exchange(ref _lastReportTicks, now);
                Report("downloading");
            }
        }

        public void Report(string state, string? message = null)
        {
            var done = Done;
            var seconds = _clock.Elapsed.TotalSeconds;
            var rate = seconds > 1 ? (done - _startDone) / seconds : 0;
            double? eta = rate > 0 ? (total - done) / rate : null;
            progress?.Report(new DownloadProgress(modelId, state, done, total, eta, message));
        }
    }
}
