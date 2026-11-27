using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Generation;

public sealed class UploadServiceOptions
{
    /// <summary>The host-owned folder (&lt;data&gt;\uploads). Created, with a marker file, when the service starts.</summary>
    public required string Folder { get; init; }

    /// <summary>ffmpeg tools, used to check that a video upload is readable. Null or not available: video uploads are refused.</summary>
    public IMediaTools? Media { get; init; }

    public long MaxUploadBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public int MaxOpenPerApp { get; init; } = 4;

    /// <summary>An upload goes this long after it was last used.</summary>
    public TimeSpan Expiry { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How often expired uploads are swept. Zero: no timer (a test calls <see cref="UploadService.SweepExpired"/>).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(30);

    public Action<string>? Log { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Uploads of pictures and clips that are too big for a JSON body (docs/api-v1.md, "Uploads"). The body is streamed to a
/// new <c>upl_&lt;id&gt;.part</c> file of the host's own uploads folder, checked, and renamed to <c>upl_&lt;id&gt;.&lt;ext&gt;</c>.
/// Local apps only in this version. An upload belongs to the app and device that made it; another caller sees not_found.
/// The host deletes only files of that folder whose name matches its own id pattern, never a stray file.
/// </summary>
public sealed partial class UploadService : IDisposable
{
    public const string MarkerName = ".portal-ai-host-uploads";

    private static readonly Dictionary<string, (string Ext, bool Video)> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = (".png", false),
        ["image/jpeg"] = (".jpg", false),
        ["image/webp"] = (".webp", false),
        ["video/mp4"] = (".mp4", true),
        ["video/quicktime"] = (".mov", true),
        ["video/webm"] = (".webm", true),
        ["video/x-matroska"] = (".mkv", true),
    };

    [GeneratedRegex("^upl_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^upl_[0-9a-f]{32}\.(png|jpg|webp|mp4|mov|webm|mkv|part)$", RegexOptions.CultureInvariant)]
    private static partial Regex FilePattern();

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string AppId { get; init; }
        public required string DeviceId { get; init; }
        public required string ContentType { get; init; }
        public required string Path { get; init; }
        public required bool Video { get; init; }
        public long Bytes { get; set; }
        public bool Done { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }

    private readonly UploadServiceOptions _o;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _timer;

    public UploadService(UploadServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        Folder = Path.GetFullPath(options.Folder);
        Directory.CreateDirectory(Folder);
        var marker = Path.Combine(Folder, MarkerName);
        if (!File.Exists(marker))
        {
            AtomicFile.WriteAllBytes(marker, "portal-ai-host-uploads"u8);
        }

        SweepAtStart();
        if (options.SweepInterval > TimeSpan.Zero)
        {
            _timer = Task.Run(SweepLoopAsync);
        }
    }

    public string Folder { get; }

    public long MaxUploadBytes => _o.MaxUploadBytes;

    public static bool IsUploadId(string text) => IdPattern().IsMatch(text);

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _timer?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    // ---- routes ------------------------------------------------------------------------------------

    /// <summary>Adds the upload routes. They need a token; the POST streams its raw body and works for apps on this PC only.</summary>
    public void MapRoutes(ApiPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var json = AiHostJsonContext.Default;
        pipeline.Map("POST", Routes.Uploads, async c =>
        {
            var owner = Owner(c);
            if (owner.DeviceId != ClientPrincipal.LocalDevice || c.Request.BodyStream is not { } stream)
            {
                throw new ApiException(ErrorCodes.FeatureDisabled, "Uploads work only for apps on this PC in this version.");
            }

            c.Request.Headers.TryGetValue("Content-Type", out var contentType);
            long? declared = c.Request.Headers.TryGetValue("Content-Length", out var len) &&
                long.TryParse(len, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
            var info = await CreateAsync(owner, contentType, declared, stream, c.Aborted).ConfigureAwait(false);
            c.ResponseHeaders["Location"] = Routes.Uploads + "/" + info.Id;
            await c.WriteJsonAsync(201, info, json.UploadInfo).ConfigureAwait(false);
        }, rawBody: true);
        pipeline.Map("GET", Routes.Uploads + "/{id}", c =>
            c.WriteJsonAsync(200, Get(Owner(c), c.RouteValues["id"]), json.UploadInfo));
        pipeline.Map("DELETE", Routes.Uploads + "/{id}", async c =>
        {
            Delete(Owner(c), c.RouteValues["id"]);
            await c.WriteEmptyAsync(204).ConfigureAwait(false);
        });
    }

    private static (string AppId, string DeviceId) Owner(ApiContext c) =>
        c.Principal is { } p ? (p.AppId, p.DeviceId) : throw new ApiException(ErrorCodes.AuthRequired, "Authorization: Bearer <token> is required.");

    // ---- create ------------------------------------------------------------------------------------

    /// <summary>Streams the body to a new file, checks it and records it. A failed or cancelled upload leaves nothing behind.</summary>
    public async Task<UploadInfo> CreateAsync((string AppId, string DeviceId) owner, string? contentType, long? declaredBytes,
        Stream body, CancellationToken ct)
    {
        var mediaType = (contentType ?? "").Split(';')[0].Trim();
        if (!Types.TryGetValue(mediaType, out var type))
        {
            throw new ApiException(ErrorCodes.UnsupportedMedia,
                "Content-Type must be image/png, image/jpeg, image/webp, video/mp4, video/quicktime, video/webm or video/x-matroska.",
                "Content-Type");
        }

        if (declaredBytes is { } d && d > _o.MaxUploadBytes)
        {
            throw TooLarge();
        }

        if (type.Video && _o.Media is not { Available: true })
        {
            throw new ApiException(ErrorCodes.EngineMissing, "Checking a video needs ffmpeg, which is not installed on this PC.");
        }

        var now = _o.Time.GetUtcNow();
        var id = "upl_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var entry = new Entry
        {
            Id = id,
            AppId = owner.AppId,
            DeviceId = owner.DeviceId,
            ContentType = mediaType.ToLowerInvariant(),
            Path = Path.Combine(Folder, id + type.Ext),
            Video = type.Video,
            LastUsed = now,
        };
        lock (_gate)
        {
            if (_entries.Values.Count(e => Mine(e, owner)) >= _o.MaxOpenPerApp)
            {
                throw new ApiException(ErrorCodes.QueueFull, $"This app has {_o.MaxOpenPerApp} uploads open. Delete one first.",
                    retryAfterMs: 10_000);
            }

            _entries[id] = entry;
        }

        var part = Path.Combine(Folder, id + ".part");
        var keep = false;
        try
        {
            entry.Bytes = await CopyAsync(body, part, ct).ConfigureAwait(false);
            if (entry.Bytes == 0)
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "The upload is empty.");
            }

            if (type.Video)
            {
                var size = await _o.Media!.ProbeSizeAsync(part, ct).ConfigureAwait(false);
                if (size is null)
                {
                    throw new ApiException(ErrorCodes.UnsupportedMedia, "The upload is not a video this host can read.");
                }
            }
            else if (!LooksLikePicture(part, mediaType))
            {
                throw new ApiException(ErrorCodes.UnsupportedMedia, "The upload is not a " + mediaType + " picture.");
            }

            File.Move(part, entry.Path, overwrite: false); // A new unique name: never replaces a file.
            entry.Done = true;
            entry.LastUsed = _o.Time.GetUtcNow();
            keep = true;
            return Info(entry);
        }
        finally
        {
            if (!keep)
            {
                _entries.TryRemove(id, out _);
                DeleteOwn(part);
            }
        }
    }

    private async Task<long> CopyAsync(Stream body, string partPath, CancellationToken ct)
    {
        long total = 0;
        try
        {
            // CreateNew: the name is new and random, an existing file is never opened for writing.
            await using var file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > _o.MaxUploadBytes)
                {
                    throw TooLarge();
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            throw e is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) }
                ? new ApiException(ErrorCodes.InternalError, "There is not enough free space for the upload.")
                : new ApiException(ErrorCodes.InvalidRequest, "The upload body could not be read.");
        }
    }

    private static ApiException TooLarge() =>
        new(ErrorCodes.RequestTooLarge, "The upload is larger than this host takes (limits.max_upload_bytes in capabilities).");

    private static bool LooksLikePicture(string path, string mediaType)
    {
        Span<byte> head = stackalloc byte[12];
        int n;
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            n = f.Read(head);
        }
        catch (IOException)
        {
            return false;
        }

        var h = head[..n];
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => h.StartsWith("\x89PNG"u8.ToArray()) || (n >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47),
            "image/jpeg" => n >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
            "image/webp" => n >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    // ---- read, delete, use -------------------------------------------------------------------------

    private static bool Mine(Entry e, (string AppId, string DeviceId) owner) => e.AppId == owner.AppId && e.DeviceId == owner.DeviceId;

    private Entry Find((string AppId, string DeviceId) owner, string id) =>
        _entries.TryGetValue(id, out var e) && e.Done && Mine(e, owner) ? e : throw new ApiException(ErrorCodes.NotFound, "No such upload.");

    private UploadInfo Info(Entry e) => new()
    {
        Id = e.Id,
        Bytes = e.Bytes,
        ContentType = e.ContentType,
        ExpiresAt = e.LastUsed + _o.Expiry,
    };

    public UploadInfo Get((string AppId, string DeviceId) owner, string id) => Info(Find(owner, id));

    /// <summary>Deletes the caller's upload (the host's copy only). Another app's id is not_found.</summary>
    public void Delete((string AppId, string DeviceId) owner, string id)
    {
        var e = Find(owner, id);
        if (!DeleteOwn(e.Path) && File.Exists(e.Path))
        {
            throw new ApiException(ErrorCodes.InternalError, "The upload is in use and could not be removed now.");
        }

        _entries.TryRemove(id, out _);
    }

    /// <summary>
    /// The file of one of the caller's finished video uploads, for a join clip. Marks the upload used (its expiry moves
    /// forward). Another app's upload, a picture, or an expired one is refused.
    /// </summary>
    public string? ResolveVideo((string AppId, string DeviceId) owner, string id)
    {
        if (!_entries.TryGetValue(id, out var e) || !e.Done || !Mine(e, owner) || !e.Video || !File.Exists(e.Path))
        {
            return null;
        }

        e.LastUsed = _o.Time.GetUtcNow();
        return e.Path;
    }

    // ---- expiry ------------------------------------------------------------------------------------

    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(_o.SweepInterval, _o.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                SweepExpired();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// At start no upload is live: every leftover <c>.part</c> goes, and every finished file older than the expiry (by its
    /// last write). Only files whose name matches the host's own id pattern are touched.
    /// </summary>
    private void SweepAtStart()
    {
        var cutoff = _o.Time.GetUtcNow() - _o.Expiry;
        foreach (var file in EnumerateOwnFiles())
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".part", StringComparison.Ordinal) || File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
            {
                DeleteOwn(file);
            }
        }
    }

    /// <summary>
    /// Removes the uploads nobody used for the expiry time, and any finished file of the host's own pattern that no
    /// record owns and that is older than the expiry. Returns how many files went.
    /// </summary>
    public int SweepExpired()
    {
        var now = _o.Time.GetUtcNow();
        var removed = 0;
        foreach (var e in _entries.Values.Where(e => e.Done && now - e.LastUsed >= _o.Expiry))
        {
            if (DeleteOwn(e.Path) || !File.Exists(e.Path))
            {
                _entries.TryRemove(e.Id, out _);
                removed++;
            }
        }

        foreach (var file in EnumerateOwnFiles())
        {
            var name = Path.GetFileName(file);
            var id = name.Length >= 36 ? name[..36] : name;
            if (!name.EndsWith(".part", StringComparison.Ordinal) && !_entries.ContainsKey(id) &&
                File.GetLastWriteTimeUtc(file) < (now - _o.Expiry).UtcDateTime && DeleteOwn(file))
            {
                removed++;
            }
        }

        return removed;
    }

    private IEnumerable<string> EnumerateOwnFiles()
    {
        try
        {
            return [.. Directory.EnumerateFiles(Folder).Where(f => FilePattern().IsMatch(Path.GetFileName(f)))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes one file of the uploads folder, only when its name matches the host's own pattern. False when it did not go.</summary>
    private bool DeleteOwn(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), Folder, StringComparison.OrdinalIgnoreCase) ||
                !FilePattern().IsMatch(Path.GetFileName(full)))
            {
                return false;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _o.Log?.Invoke("An upload file could not be removed now: " + e.GetType().Name);
            return false;
        }
    }
}
