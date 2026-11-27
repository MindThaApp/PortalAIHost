using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>ffmpeg stand-in for the upload check: reads every file unless told it cannot.</summary>
internal sealed class UploadProbeTools : IMediaTools
{
    public bool Present { get; set; } = true;
    public bool Unreadable { get; set; }
    public List<string> Probed { get; } = [];

    public bool Available => Present;

    public Task ConvertToMp4Async(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

    public Task<byte[]> ExtractLastFramePngAsync(string inputPath, CancellationToken ct) => Task.FromResult<byte[]>([1]);

    public async Task JoinAsync(MediaJoinPlan plan, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        await File.WriteAllTextAsync(outputPath, "joined " + plan.Clips.Count, ct);

    public Task<double?> ProbeDurationAsync(string path, CancellationToken ct) => Task.FromResult<double?>(2);

    public Task<(int Width, int Height)?> ProbeSizeAsync(string path, CancellationToken ct)
    {
        Probed.Add(path);
        return Task.FromResult<(int Width, int Height)?>(Unreadable ? null : (640, 360));
    }
}

/// <summary>A body that sends some bytes and then waits, so a test can drop the connection mid-upload.</summary>
internal sealed class StallingContent(int bytes) : HttpContent
{
    public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[bytes], cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Sent.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class HandClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>POST /host/uploads and joins that take an upload id (docs/api-v1.md, "Uploads"), over the real loopback server.</summary>
public sealed class UploadTests : IAsyncLifetime
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private TestHost _host = null!;
    private UploadProbeTools _media = null!;
    private GenerationService _generation = null!;
    private UploadService _uploads = null!;
    private string Folder => Path.Combine(_host.RootPath, "uploads");

    public async ValueTask InitializeAsync() => await StartAsync(new UploadServiceOptions { Folder = "x" });

    private async Task StartAsync(UploadServiceOptions options)
    {
        _host = await TestHost.StartAsync(withModels: false);
        _media = new UploadProbeTools();
        _uploads = new UploadService(new UploadServiceOptions
        {
            Folder = Path.Combine(_host.RootPath, "uploads"),
            Media = _media,
            MaxUploadBytes = options.MaxUploadBytes,
            MaxOpenPerApp = options.MaxOpenPerApp,
            Expiry = options.Expiry,
            Time = options.Time,
            SweepInterval = TimeSpan.Zero,
        });
        _uploads.MapRoutes(_host.Runtime.Pipeline);
        _generation = new GenerationService(new GenerationServiceOptions
        {
            Runtime = _host.Runtime,
            Scheduler = _host.Scheduler,
            Engine = new FakeGenerationEngine(),
            Models = new FakeGenerationModels(Path.Combine(_host.RootPath, "models")),
            Output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "generated")),
            Media = _media,
            Uploads = _uploads,
            CancelWait = TimeSpan.FromSeconds(5),
        });
        _generation.MapRoutes(_host.Runtime.Pipeline);
    }

    public async ValueTask DisposeAsync()
    {
        await _generation.DisposeAsync();
        _uploads.Dispose();
        await _host.DisposeAsync();
    }

    private async Task RestartAsync(UploadServiceOptions options)
    {
        await DisposeAsync();
        await StartAsync(options);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string token, string? contentType, byte[] bytes)
    {
        var msg = _host.Authed(HttpMethod.Post, Routes.Uploads, token);
        msg.Content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return await SendAsync(msg);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpRequestMessage msg)
    {
        using var response = await _host.Http.SendAsync(msg);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string token) =>
        SendAsync(_host.Authed(method, path, token));

    private static void AssertError(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, status);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private string[] Files() => [.. Directory.GetFiles(Folder).Select(f => Path.GetFileName(f)).Where(n => !n.StartsWith('.'))];

    [Fact]
    public async Task AnUploadIsStoredReadAndDeleted()
    {
        var token = await _host.GetTokenAsync();
        var (status, info) = await PostAsync(token, "image/png", Png);
        Assert.Equal(HttpStatusCode.Created, status);
        var id = info.GetProperty("id").GetString()!;
        Assert.Matches("^upl_[0-9a-f]{32}$", id);
        Assert.Equal("upload", info.GetProperty("object").GetString());
        Assert.Equal(Png.Length, info.GetProperty("bytes").GetInt64());
        Assert.Equal("image/png", info.GetProperty("content_type").GetString());
        Assert.True(info.TryGetProperty("expires_at", out _));
        Assert.Equal([id + ".png"], Files());
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(Folder, id + ".png")));

        var (getStatus, got) = await SendAsync(HttpMethod.Get, Routes.Uploads + "/" + id, token);
        Assert.Equal(HttpStatusCode.OK, getStatus);
        Assert.Equal(id, got.GetProperty("id").GetString());

        var (delStatus, _) = await SendAsync(HttpMethod.Delete, Routes.Uploads + "/" + id, token);
        Assert.Equal(HttpStatusCode.NoContent, delStatus);
        Assert.Empty(Files());
        var (again, body) = await SendAsync(HttpMethod.Get, Routes.Uploads + "/" + id, token);
        AssertError(again, body, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task AWrongContentTypeOrWrongBytesAreRefusedAndNothingStays()
    {
        var token = await _host.GetTokenAsync();
        foreach (var type in new[] { "text/plain", "application/octet-stream", "image/gif", null })
        {
            var (s, b) = await PostAsync(token, type, Png);
            AssertError(s, b, HttpStatusCode.BadRequest, ErrorCodes.UnsupportedMedia);
        }

        var (s2, b2) = await PostAsync(token, "image/jpeg", Png); // PNG bytes under a JPEG label
        AssertError(s2, b2, HttpStatusCode.BadRequest, ErrorCodes.UnsupportedMedia);
        var (s3, b3) = await PostAsync(token, "image/png", []);
        AssertError(s3, b3, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task TheSizeLimitIsEnforcedForDeclaredAndStreamedBodies()
    {
        await RestartAsync(new UploadServiceOptions { Folder = "x", MaxUploadBytes = 1000 });
        var token = await _host.GetTokenAsync();
        var (s, b) = await PostAsync(token, "image/png", [.. Png, .. new byte[2000]]); // declared length
        AssertError(s, b, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.RequestTooLarge);

        var msg = _host.Authed(HttpMethod.Post, Routes.Uploads, token); // chunked: no declared length
        msg.Content = new StreamContent(new MemoryStream([.. Png, .. new byte[2000]]));
        msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        msg.Headers.TransferEncodingChunked = true;
        var (s2, b2) = await SendAsync(msg);
        AssertError(s2, b2, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.RequestTooLarge);
        Assert.Empty(Files());

        var (ok, _) = await PostAsync(token, "image/png", [.. Png, .. new byte[900]]);
        Assert.Equal(HttpStatusCode.Created, ok);
    }

    [Fact]
    public async Task ABodyOverTheJsonCapStillUploads()
    {
        var token = await _host.GetTokenAsync();
        var big = new byte[ApiLimits.MaxRequestBytes + 1024 * 1024];
        Png.CopyTo(big, 0);
        var (s, info) = await PostAsync(token, "image/png", big);
        Assert.Equal(HttpStatusCode.Created, s);
        Assert.Equal(big.Length, info.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task AtMostFourUploadsStayOpenPerApp()
    {
        var token = await _host.GetTokenAsync();
        string? first = null;
        for (var i = 0; i < 4; i++)
        {
            var (s, info) = await PostAsync(token, "image/png", Png);
            Assert.Equal(HttpStatusCode.Created, s);
            first ??= info.GetProperty("id").GetString();
        }

        var (s5, b5) = await PostAsync(token, "image/png", Png);
        AssertError(s5, b5, HttpStatusCode.TooManyRequests, ErrorCodes.QueueFull);
        await SendAsync(HttpMethod.Delete, Routes.Uploads + "/" + first, token);
        var (s6, _) = await PostAsync(token, "image/png", Png);
        Assert.Equal(HttpStatusCode.Created, s6);
    }

    [Fact]
    public async Task AVideoThatFfprobeCannotReadIsRefusedAndRemoved()
    {
        var token = await _host.GetTokenAsync();
        _media.Unreadable = true;
        var (s, b) = await PostAsync(token, "video/mp4", [1, 2, 3, 4]);
        AssertError(s, b, HttpStatusCode.BadRequest, ErrorCodes.UnsupportedMedia);
        Assert.Single(_media.Probed);
        Assert.Empty(Files());

        _media.Unreadable = false;
        var (ok, info) = await PostAsync(token, "video/mp4", [1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.Created, ok);
        Assert.Equal([info.GetProperty("id").GetString() + ".mp4"], Files());

        _media.Present = false;
        var (s3, b3) = await PostAsync(token, "video/webm", [1]);
        AssertError(s3, b3, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);
    }

    [Fact]
    public async Task AnotherAppsUploadIsNotFoundAndItsFileStays()
    {
        var mine = await _host.GetTokenAsync("com.example.mine");
        var theirs = await _host.GetTokenAsync("com.example.theirs");
        var (_, info) = await PostAsync(mine, "video/mp4", [1, 2, 3]);
        var id = info.GetProperty("id").GetString()!;

        var (s1, b1) = await SendAsync(HttpMethod.Get, Routes.Uploads + "/" + id, theirs);
        AssertError(s1, b1, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        var (s2, b2) = await SendAsync(HttpMethod.Delete, Routes.Uploads + "/" + id, theirs);
        AssertError(s2, b2, HttpStatusCode.NotFound, ErrorCodes.NotFound);
        Assert.Single(Files());

        var (s3, b3) = await SendAsync(_host.Authed(HttpMethod.Post, Routes.VideoJoins, theirs, $$"""{ "clips": ["{{id}}"] }"""));
        AssertError(s3, b3, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ARequestWithoutAStreamedBodyIsRefused()
    {
        // What a LAN or relay request looks like to the pipeline: the body was buffered, no stream is handed over.
        var token = await _host.GetTokenAsync();
        var sink = new BufferSink();
        await _host.Runtime.Pipeline.HandleAsync(new ApiRequest
        {
            Method = "POST",
            Path = Routes.Uploads,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + token,
                ["Content-Type"] = "image/png",
            },
            Body = Png,
        }, sink);
        Assert.Equal(404, sink.Status);
        Assert.Contains(ErrorCodes.FeatureDisabled, sink.Text);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task AnAbortedUploadLeavesNoPartialFile()
    {
        var token = await _host.GetTokenAsync();
        var content = new StallingContent(50_000);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        var msg = _host.Authed(HttpMethod.Post, Routes.Uploads, token);
        msg.Content = content;
        msg.Headers.TransferEncodingChunked = true;
        using var cts = new CancellationTokenSource();
        var send = _host.Http.SendAsync(msg, cts.Token);
        await content.Sent.Task;
        for (var i = 0; i < 100 && Directory.GetFiles(Folder, "*.part").Length == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(Directory.GetFiles(Folder, "*.part"));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => send.WaitAsync(TimeSpan.FromSeconds(15)));
        for (var i = 0; i < 200 && Files().Length > 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.Empty(Files());
    }

    [Fact]
    public async Task AJoinTakesTheCallersOwnVideoUploadsAndRefusesPictures()
    {
        var token = await _host.GetTokenAsync();
        var (_, v1) = await PostAsync(token, "video/mp4", [1, 2, 3]);
        var (_, v2) = await PostAsync(token, "video/webm", [4, 5, 6]);
        var (_, pic) = await PostAsync(token, "image/png", Png);
        var a = v1.GetProperty("id").GetString()!;
        var b = v2.GetProperty("id").GetString()!;
        var picId = pic.GetProperty("id").GetString();

        var (sp, bp) = await SendAsync(_host.Authed(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{a}}", "{{picId}}"] }"""));
        AssertError(sp, bp, HttpStatusCode.NotFound, ErrorCodes.NotFound);

        var (s, job) = await SendAsync(_host.Authed(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{a}}", "{{b}}"] }"""));
        Assert.True(s == HttpStatusCode.Accepted, job.ToString());
        var joinId = job.GetProperty("id").GetString()!;
        for (var i = 0; i < 300; i++)
        {
            var (_, j) = await SendAsync(HttpMethod.Get, Routes.VideoJoins + "/" + joinId, token);
            if (j.GetProperty("state").GetString() == GenerationJobStates.Succeeded)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The join never finished.");
    }

    [Fact]
    public async Task TheExpirySweepRemovesOnlyOwnFilesAndNeverAStrayOne()
    {
        var clock = new HandClock(DateTimeOffset.UtcNow);
        await RestartAsync(new UploadServiceOptions { Folder = "x", Time = clock, Expiry = TimeSpan.FromHours(24) });
        var token = await _host.GetTokenAsync();
        var (_, info) = await PostAsync(token, "image/png", Png);
        var id = info.GetProperty("id").GetString()!;

        // Files that are not the host's own: a user file and look-alikes. Plus an own-pattern file no record owns.
        var stray = Path.Combine(Folder, "holiday.png");
        var lookalike = Path.Combine(Folder, "upl_nothex.png");
        var wrongExt = Path.Combine(Folder, "upl_" + new string('a', 32) + ".txt");
        var oldOwn = Path.Combine(Folder, "upl_" + new string('b', 32) + ".png");
        foreach (var f in new[] { stray, lookalike, wrongExt, oldOwn })
        {
            await File.WriteAllTextAsync(f, "keep");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-10));
        }

        Assert.Equal(1, _uploads.SweepExpired()); // only the old, unowned file of the host's own pattern
        Assert.False(File.Exists(oldOwn));

        clock.Advance(TimeSpan.FromHours(23));
        _uploads.SweepExpired();
        Assert.True(File.Exists(Path.Combine(Folder, id + ".png")));

        clock.Advance(TimeSpan.FromHours(2));
        _uploads.SweepExpired();
        Assert.False(File.Exists(Path.Combine(Folder, id + ".png")));
        Assert.True(File.Exists(stray));
        Assert.True(File.Exists(lookalike));
        Assert.True(File.Exists(wrongExt));
        Assert.Equal("keep", await File.ReadAllTextAsync(stray));
        var (s, b) = await SendAsync(HttpMethod.Get, Routes.Uploads + "/" + id, token);
        AssertError(s, b, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task UsingAnUploadInAJoinMovesItsExpiryForward()
    {
        var clock = new HandClock(DateTimeOffset.UtcNow);
        await RestartAsync(new UploadServiceOptions { Folder = "x", Time = clock, Expiry = TimeSpan.FromHours(24) });
        var token = await _host.GetTokenAsync();
        var (_, v) = await PostAsync(token, "video/mp4", [1, 2, 3]);
        var id = v.GetProperty("id").GetString()!;
        clock.Advance(TimeSpan.FromHours(20));
        var (s, job) = await SendAsync(_host.Authed(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{id}}"] }"""));
        Assert.True(s == HttpStatusCode.Accepted, job.ToString());
        clock.Advance(TimeSpan.FromHours(10));
        _uploads.SweepExpired();
        Assert.True(File.Exists(Path.Combine(Folder, id + ".mp4")));
    }

    [Fact]
    public async Task TheStartSweepRemovesLeftoverPartsAndOldOwnFilesOnly()
    {
        var dir = Path.Combine(_host.RootPath, "uploads2");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "upl_" + new string('c', 32) + ".part");
        var oldFile = Path.Combine(dir, "upl_" + new string('d', 32) + ".mp4");
        var newFile = Path.Combine(dir, "upl_" + new string('e', 32) + ".mp4");
        var stray = Path.Combine(dir, "movie.mp4");
        foreach (var f in new[] { part, oldFile, newFile, stray })
        {
            await File.WriteAllTextAsync(f, "x");
        }

        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-30));
        using var service = new UploadService(new UploadServiceOptions { Folder = dir, SweepInterval = TimeSpan.Zero });
        Assert.False(File.Exists(part));
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.True(File.Exists(stray));
    }

    private sealed class BufferSink : IResponseSink
    {
        private readonly MemoryStream _body = new();

        public int Status { get; private set; }

        public CancellationToken ClientAborted => CancellationToken.None;

        public string Text => System.Text.Encoding.UTF8.GetString(_body.ToArray());

        public ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            Status = statusCode;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            _body.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask WriteSseCommentAsync(string comment, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
