using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>A stand-in for ffmpeg: writes small files where the real tools would, and records what it was asked.</summary>
internal sealed class FakeMediaTools : IMediaTools
{
    public bool Present { get; set; } = true;
    public bool FailConvert { get; set; }
    public TaskCompletionSource? JoinGate { get; set; }
    public List<string> Converted { get; } = [];
    public List<MediaJoinPlan> Joins { get; } = [];
    public byte[] Png { get; set; } = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    public bool Available => Present;

    public Task ConvertToMp4Async(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        Converted.Add(inputPath);
        if (FailConvert)
        {
            throw new EngineException(ErrorCodes.EngineError, "convert failed");
        }

        File.WriteAllText(outputPath, "mp4 of " + Path.GetFileName(inputPath));
        return Task.CompletedTask;
    }

    public Task<byte[]> ExtractLastFramePngAsync(string inputPath, CancellationToken ct) => Task.FromResult(Png);

    public async Task JoinAsync(MediaJoinPlan plan, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        Joins.Add(plan);
        progress?.Report(0.5);
        if (JoinGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        await File.WriteAllTextAsync(outputPath, "joined " + plan.Clips.Count, ct);
    }

    public Task<double?> ProbeDurationAsync(string path, CancellationToken ct) => Task.FromResult<double?>(2);
}

/// <summary>MP4 output, last pictures and joins of video jobs (docs/api-v1.md), with a fake engine and fake ffmpeg.</summary>
public sealed class GenerationMediaTests : IAsyncLifetime
{
    private const string VideoBody = """{ "prompt": "waves", "frames": 9 }""";

    private TestHost _host = null!;
    private FakeGenerationEngine _engine = null!;
    private FakeGenerationModels _models = null!;
    private FakeMediaTools _media = null!;
    private GenerationOutputFolder _output = null!;
    private GenerationService _service = null!;
    private Func<string, string, byte[]?> _speech = (_, _) => null;

    public async ValueTask InitializeAsync() => await StartAsync(new FakeMediaTools());

    private async Task StartAsync(FakeMediaTools media)
    {
        _media = media;
        _host = await TestHost.StartAsync(withModels: false);
        _engine = new FakeGenerationEngine();
        _models = new FakeGenerationModels(Path.Combine(_host.RootPath, "models"));
        _output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "generated"));
        _service = new GenerationService(new GenerationServiceOptions
        {
            Runtime = _host.Runtime,
            Scheduler = _host.Scheduler,
            Engine = _engine,
            Models = _models,
            Output = _output,
            Media = _media,
            SpeechAudio = (app, id) => _speech(app, id),
            CancelWait = TimeSpan.FromSeconds(5),
        });
        _service.MapRoutes(_host.Runtime.Pipeline);
    }

    public async ValueTask DisposeAsync()
    {
        _media.JoinGate?.TrySetResult();
        await _service.DisposeAsync();
        await _host.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string token, string? json = null)
    {
        using var response = await _host.Http.SendAsync(_host.Authed(method, path, token, json));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 || response.Content.Headers.ContentType?.MediaType != "application/json"
            ? default
            : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<JsonElement> WaitAsync(string token, string root, string id, string state)
    {
        JsonElement job = default;
        for (var i = 0; i < 300; i++)
        {
            (_, job) = await SendAsync(HttpMethod.Get, $"{root}/{id}", token);
            if (job.GetProperty("state").GetString() == state)
            {
                return job;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Job {id} never reached {state}: {job}");
        return job;
    }

    private async Task<string> VideoJobAsync(string token, string body = VideoBody)
    {
        var (status, job) = await SendAsync(HttpMethod.Post, Routes.VideoJobs, token, body);
        Assert.True(status == HttpStatusCode.Accepted, job.ToString());
        var id = job.GetProperty("id").GetString()!;
        await WaitAsync(token, Routes.VideoJobs, id, GenerationJobStates.Succeeded);
        return id;
    }

    private static void AssertError(HttpStatusCode status, JsonElement body, HttpStatusCode expected, string code)
    {
        Assert.Equal(expected, status);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnMp4JobIsConvertedPublishedAsMp4AndTheAviIsGone()
    {
        var token = await _host.GetTokenAsync();
        var id = await VideoJobAsync(token, """{ "prompt": "waves", "frames": 9, "container": "mp4" }""");
        var (_, job) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{id}", token);
        var result = job.GetProperty("result");
        Assert.Equal("video/mp4", result.GetProperty("content_type").GetString());
        Assert.EndsWith(".mp4", result.GetProperty("file_name").GetString());
        var finished = Directory.GetFiles(_output.FinishedFolder(ModelKinds.Video));
        Assert.Equal(".mp4", Path.GetExtension(Assert.Single(finished)));
        Assert.Equal("mp4 of output.avi", File.ReadAllText(finished[0]));
        Assert.Equal(".avi", Path.GetExtension(Assert.Single(_media.Converted)));
        Assert.Empty(Directory.Exists(_output.WorkRoot) ? Directory.GetDirectories(_output.WorkRoot) : []);

        using var download = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, result.GetProperty("url").GetString()!, token));
        Assert.Equal("video/mp4", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheDefaultStaysAviAndNothingIsConverted()
    {
        var token = await _host.GetTokenAsync();
        var id = await VideoJobAsync(token);
        var (_, job) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{id}", token);
        Assert.Equal("video/x-msvideo", job.GetProperty("result").GetProperty("content_type").GetString());
        Assert.Empty(_media.Converted);
    }

    [Fact]
    public async Task AFailedConversionKeepsTheAviAndTheJobSucceeds()
    {
        _media.FailConvert = true;
        var token = await _host.GetTokenAsync();
        var id = await VideoJobAsync(token, """{ "prompt": "waves", "frames": 9, "container": "mp4" }""");
        var (_, job) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{id}", token);
        Assert.Equal("video/x-msvideo", job.GetProperty("result").GetProperty("content_type").GetString());
        Assert.Equal(".avi", Path.GetExtension(Assert.Single(Directory.GetFiles(_output.FinishedFolder(ModelKinds.Video)))));
        Assert.Empty(Directory.GetFiles(_output.WorkRoot, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(ModelKinds.Image, """{ "prompt": "x", "container": "mp4" }""", ErrorCodes.UnsupportedParameter)]
    [InlineData(ModelKinds.Video, """{ "prompt": "x", "container": "mov" }""", ErrorCodes.InvalidRequest)]
    public async Task ABadContainerIsRefused(string kind, string body, string code)
    {
        var token = await _host.GetTokenAsync();
        var (status, error) = await SendAsync(HttpMethod.Post, Routes.JobsFor(kind)!, token, body);
        AssertError(status, error, HttpStatusCode.BadRequest, code);
        Assert.Equal("container", error.GetProperty("error").GetProperty("param").GetString());
    }

    [Fact]
    public async Task Mp4NeedsFfmpegAndTheFeaturesReportOff()
    {
        await _service.DisposeAsync();
        await _host.DisposeAsync();
        await StartAsync(new FakeMediaTools { Present = false });
        var token = await _host.GetTokenAsync();
        var (status, error) = await SendAsync(HttpMethod.Post, Routes.VideoJobs, token, """{ "prompt": "x", "container": "mp4" }""");
        AssertError(status, error, HttpStatusCode.BadRequest, ErrorCodes.UnsupportedParameter);

        await _service.RefreshAvailabilityAsync();
        var gen = _service.Availability();
        Assert.True(gen.Video);
        Assert.False(gen.VideoJoins);
        Assert.Equal([GenerationContainers.Avi], gen.VideoContainers);

        var (joinStatus, joinError) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, """{ "clips": ["gen_0123456789abcdef"] }""");
        AssertError(joinStatus, joinError, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);
        var id = await VideoJobAsync(token); // Plain video jobs are unaffected.
        var (frameStatus, frameError) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{id}/last-frame", token);
        AssertError(frameStatus, frameError, HttpStatusCode.ServiceUnavailable, ErrorCodes.EngineMissing);
    }

    [Fact]
    public async Task CapabilitiesListContainersAndJoinsWhileFfmpegIsThere()
    {
        await _service.RefreshAvailabilityAsync();
        var gen = _service.Availability();
        Assert.True(gen.VideoJoins);
        Assert.Equal([GenerationContainers.Avi, GenerationContainers.Mp4], gen.VideoContainers);
        var token = await _host.GetTokenAsync();
        var (_, caps) = await SendAsync(HttpMethod.Get, "/host/capabilities", token);
        Assert.Contains(caps.GetProperty("endpoints").EnumerateArray(), e => e.GetString() == EndpointNames.VideoJoins);
    }

    [Fact]
    public async Task TheLastFrameOfAFinishedJobIsAPng()
    {
        var token = await _host.GetTokenAsync();
        var id = await VideoJobAsync(token);
        using var response = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, $"{Routes.VideoJobs}/{id}/last-frame", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(_media.Png, await response.Content.ReadAsByteArrayAsync());

        var other = await _host.GetTokenAsync("com.example.other");
        var (status, error) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJobs}/{id}/last-frame", other);
        AssertError(status, error, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public async Task JoinsOnlyTakeTheCallersJobsOrPlainNamesFromTheHostFolder()
    {
        var token = await _host.GetTokenAsync();
        var mine = await VideoJobAsync(token);
        var other = await _host.GetTokenAsync("com.example.other");
        var theirs = await VideoJobAsync(other);
        var outside = Path.Combine(_host.RootPath, "secret.mp4");
        await File.WriteAllTextAsync(outside, "not the host's");

        foreach (var (clip, status, code) in new[]
        {
            (outside.Replace("\\", "\\\\"), HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),       // a full path
            ("..\\\\secret.mp4", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),                   // a relative path
            ("sub/secret.mp4", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
            ("C:secret.mp4", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),                       // drive-relative
            ("\\\\\\\\server\\\\share\\\\a.mp4", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),  // a share
            ("https://example.com/a.mp4", HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
            ("secret.mp4", HttpStatusCode.NotFound, ErrorCodes.NotFound),                                   // a name that is not in the host folder
            (theirs, HttpStatusCode.NotFound, ErrorCodes.NotFound),                                         // another app's job
            ("gen_00000000000000000000000000000000", HttpStatusCode.NotFound, ErrorCodes.NotFound),
        })
        {
            var (s, e) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{mine}}", "{{clip}}"] }""");
            AssertError(s, e, status, code);
            Assert.StartsWith("clips[1]", e.GetProperty("error").GetProperty("param").GetString());
        }

        Assert.Empty(_media.Joins);
        Assert.Equal("not the host's", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task JoinRequestsAreValidated()
    {
        var token = await _host.GetTokenAsync();
        var a = await VideoJobAsync(token);
        var b = await VideoJobAsync(token);
        foreach (var (body, param) in new[]
        {
            ("""{ "clips": [] }""", "clips"),
            ($$"""{ "clips": ["{{a}}", "{{b}}"], "transitions": [{ "kind": "wipe" }] }""", "transitions"),
            ($$"""{ "clips": ["{{a}}", "{{b}}"], "transitions": [{ "kind": "crossfade", "seconds": 99 }] }""", "transitions"),
            ($$"""{ "clips": ["{{a}}", "{{b}}"], "transitions": [{}, {}] }""", "transitions"),
            ($$"""{ "clips": ["{{a}}"], "transitions": [{}] }""", "transitions"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ "role": "loud", "data_base64": "AAAA" }] }""", "audio[0]"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ "volume": 9, "data_base64": "AAAA" }] }""", "audio[0]"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ }] }""", "audio[0]"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ "speech_job": "x", "data_base64": "AAAA" }] }""", "audio[0]"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ "data_base64": "!!!" }] }""", "audio[0]"),
            ($$"""{ "clips": ["{{a}}"], "audio": [{ "speech_job": "speech_nope" }] }""", "audio[0]"),
        })
        {
            var (s, e) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, body);
            Assert.True(s is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, e.ToString());
            Assert.Equal(param, e.GetProperty("error").GetProperty("param").GetString());
        }

        Assert.Empty(_media.Joins);
        Assert.Empty(Directory.GetFiles(_output.WorkRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AJoinRunsToANewUniqueMp4AndOldFilesStayUntouched()
    {
        _speech = (app, id) => id == "speech_ok" ? "RIFFwav"u8.ToArray() : null;
        var token = await _host.GetTokenAsync();
        var a = await VideoJobAsync(token);
        var b = await VideoJobAsync(token);
        var folder = _output.FinishedFolder(ModelKinds.Video);
        var before = Directory.GetFiles(folder).ToDictionary(f => f, File.ReadAllText);
        var fileName = Path.GetFileName(before.Keys.First());

        var body = $$"""
            { "clips": ["{{a}}", "{{fileName}}", "{{b}}"],
              "transitions": [{ "kind": "crossfade", "seconds": 0.4 }, { "kind": "cut" }],
              "audio": [{ "speech_job": "speech_ok", "role": "voice", "start_seconds": 1 }, { "data_base64": "UklGRg==", "volume": 0.5 }],
              "duck": true }
            """;
        var (status, accepted) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, body);
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = accepted.GetProperty("id").GetString()!;
        Assert.Equal("generation.join", accepted.GetProperty("object").GetString());
        var job = await WaitAsync(token, Routes.VideoJoins, id, GenerationJobStates.Succeeded);

        var plan = Assert.Single(_media.Joins);
        Assert.Equal(3, plan.Clips.Count);
        Assert.All(plan.Clips, c => Assert.Equal(folder, Path.GetDirectoryName(c)));
        Assert.Equal(MediaTransitions.Crossfade, plan.Transitions[0].Kind);
        Assert.Equal(0.4, plan.Transitions[0].Seconds);
        Assert.Equal(MediaTransitions.Cut, plan.Transitions[1].Kind);
        Assert.True(plan.Duck);
        Assert.Equal([MediaAudioRoles.Voice, MediaAudioRoles.Music], plan.Audio.Select(t => t.Role));
        Assert.Equal(1, plan.Audio[0].StartSeconds);
        Assert.All(plan.Audio, t => Assert.StartsWith(_output.WorkRoot, t.Path, StringComparison.OrdinalIgnoreCase));

        var result = job.GetProperty("result");
        Assert.Equal("video/mp4", result.GetProperty("content_type").GetString());
        var after = Directory.GetFiles(folder).ToDictionary(f => f, File.ReadAllText);
        var added = Assert.Single(after.Keys.Except(before.Keys));
        Assert.Equal("joined 3", after[added]);
        Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
        Assert.Empty(Directory.GetFiles(_output.WorkRoot, "*", SearchOption.AllDirectories));

        using var download = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, result.GetProperty("url").GetString()!, token));
        Assert.Equal("joined 3", await download.Content.ReadAsStringAsync());
        using var frame = await _host.Http.SendAsync(_host.Authed(HttpMethod.Get, $"{Routes.VideoJoins}/{id}/last-frame", token));
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);

        // The join's result is itself a clip for the next join, by id or by name.
        var (s2, again) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{id}}", "{{a}}"] }""");
        Assert.Equal(HttpStatusCode.Accepted, s2);
        await WaitAsync(token, Routes.VideoJoins, again.GetProperty("id").GetString()!, GenerationJobStates.Succeeded);
        var third = Directory.GetFiles(folder).Length;
        Assert.Equal(before.Count + 2, third);
    }

    [Fact]
    public async Task AJoinCanBeCancelledAndLeavesNoFiles()
    {
        _media.JoinGate = new TaskCompletionSource();
        var token = await _host.GetTokenAsync();
        var a = await VideoJobAsync(token);
        var folder = _output.FinishedFolder(ModelKinds.Video);
        var before = Directory.GetFiles(folder).Length;
        var (_, accepted) = await SendAsync(HttpMethod.Post, Routes.VideoJoins, token, $$"""{ "clips": ["{{a}}"] }""");
        var id = accepted.GetProperty("id").GetString()!;
        await WaitAsync(token, Routes.VideoJoins, id, GenerationJobStates.Running);
        var (status, _) = await SendAsync(HttpMethod.Delete, $"{Routes.VideoJoins}/{id}", token);
        Assert.Equal(HttpStatusCode.OK, status);
        await WaitAsync(token, Routes.VideoJoins, id, GenerationJobStates.Cancelled);
        Assert.Equal(before, Directory.GetFiles(folder).Length);
        Assert.Empty(Directory.GetFiles(_output.WorkRoot, "*", SearchOption.AllDirectories));

        var other = await _host.GetTokenAsync("com.example.other");
        var (s2, e) = await SendAsync(HttpMethod.Get, $"{Routes.VideoJoins}/{id}", other);
        AssertError(s2, e, HttpStatusCode.NotFound, ErrorCodes.NotFound);
    }

    [Fact]
    public void PublishNeverReplacesAnExistingMp4()
    {
        var output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "publish-test"));
        var when = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var first = Path.Combine(output.CreateWorkFolder("gen_0123456789abcdef"), "output.mp4");
        File.WriteAllText(first, "first");
        var one = output.Publish(first, ModelKinds.Video, "gen_0123456789abcdef", when);
        File.Delete(one);
        File.WriteAllText(one, "the user's file under the same name");
        var work = output.CreateWorkFolder("gen_0123456789abcde0");
        var second = Path.Combine(work, "output.mp4");
        File.WriteAllText(second, "second");
        var two = output.Publish(second, ModelKinds.Video, "gen_0123456789abcdef", when);
        Assert.NotEqual(one, two);
        Assert.Equal("the user's file under the same name", File.ReadAllText(one));
        Assert.Equal("second", File.ReadAllText(two));
    }

    [Fact]
    public void ASweepPublishesAFinishedMp4AsAVideo()
    {
        var output = new GenerationOutputFolder(Path.Combine(_host.RootPath, "sweep-test"));
        var work = output.CreateWorkFolder("gen_0123456789abcdef");
        File.WriteAllText(Path.Combine(work, "output.mp4"), "done");
        File.WriteAllText(Path.Combine(work, GenerationOutputFolder.ConvertingName), "half");
        output.MarkState(work, GenerationOutputFolder.StateFinished);
        var swept = output.SweepStaleWork();
        Assert.Equal(".mp4", Path.GetExtension(Assert.Single(swept.Published)));
        Assert.Contains(Path.DirectorySeparatorChar + "videos" + Path.DirectorySeparatorChar, Assert.Single(swept.Published));
        Assert.False(File.Exists(Path.Combine(work, GenerationOutputFolder.ConvertingName)));
    }
}
