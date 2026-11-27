using System.Net;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Voice;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>The voice routes over the real HTTP adapter: auth, consent, library, jobs, the WAV tag and capabilities.</summary>
public sealed class VoiceApiTests : IAsyncLifetime
{
    private readonly TestRoot _root = new();
    private readonly FakeSpeechEngine _engine = new();
    private TestHost _host = null!;
    private string _token = null!;

    private string VoicesDir => Path.Combine(_root.Path, "voices");
    private string OutDir => Path.Combine(_root.Path, "out");

    public async ValueTask InitializeAsync()
    {
        var voice = new VoiceService(new VoiceServiceOptions { VoicesDirectory = VoicesDir, OutputDirectory = OutDir }, _engine,
            new FakeMp3Decoder());
        _host = await TestHost.StartAsync(voice: voice);
        _token = await _host.GetTokenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _root.Dispose();
    }

    private sealed class FakeMp3Decoder : IAudioDecoder
    {
        public IReadOnlyList<string> Formats => [VoiceSampleFormats.Mp3];

        public Task<byte[]> DecodeToWavAsync(byte[] input, string format, CancellationToken ct) =>
            Task.FromResult(Tone(8));
    }

    /// <summary>A mono 24 kHz 16-bit tone WAV.</summary>
    private static byte[] Tone(double seconds)
    {
        var n = (int)(seconds * 24000);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + n * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(24000);
        w.Write(48000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(n * 2);
        for (var i = 0; i < n; i++)
        {
            w.Write((short)(Math.Sin(i * 0.05) * 6000));
        }

        return ms.ToArray();
    }

    private static string CreateBody(byte[] sample, string? consent = "own_voice", string name = "Anna")
    {
        var consentPart = consent is null ? "" : $", \"consent\": \"{consent}\"";
        return $"{{ \"name\": \"{name}\"{consentPart}, \"sample_base64\": \"{Convert.ToBase64String(sample)}\" }}";
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? json = null, string? token = null)
    {
        using var req = _host.Authed(method, path, token ?? _token, json);
        using var resp = await _host.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<string> MakeVoiceAsync()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.Voices, CreateBody(Tone(12)));
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> WaitForAsync(string jobId, string status)
    {
        for (var i = 0; i < 200; i++)
        {
            var (_, job) = await SendAsync(HttpMethod.Get, $"{Routes.SpeechJobs}/{jobId}");
            if (job.GetProperty("status").GetString() == status)
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task RoutesNeedAToken()
    {
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, Routes.Voices), (HttpMethod.Post, Routes.Voices), (HttpMethod.Post, Routes.SpeechJobs),
            (HttpMethod.Get, Routes.SpeechJobs + "/sp_x"), (HttpMethod.Delete, Routes.Voices + "/vc_x"),
        })
        {
            using var req = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                req.Content = TestHost.Json("{}");
            }

            using var resp = await _host.Http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        var (status, body) = await SendAsync(HttpMethod.Get, Routes.Voices, token: "pah_notatoken");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.TokenInvalid, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("whatever")]
    public async Task ConsentIsRequiredOnCreate(string? consent)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.Voices, CreateBody(Tone(10), consent));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(ErrorCodes.ConsentRequired, body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(VoicesDir) && Directory.EnumerateDirectories(VoicesDir).Any());
    }

    [Fact]
    public async Task UploadStoresAConvertedCopyAndListsIt()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, Routes.Voices, CreateBody(Tone(12), "permission"));
        Assert.Equal(HttpStatusCode.Created, status);
        var id = body.GetProperty("id").GetString()!;
        Assert.Matches("^vc_[0-9a-f]{24}$", id);
        Assert.Equal("permission", body.GetProperty("consent").GetString());
        Assert.True(File.Exists(Path.Combine(VoicesDir, id, VoiceLibrary.SampleFileName)));

        var (_, list) = await SendAsync(HttpMethod.Get, Routes.Voices);
        Assert.Equal(id, list.GetProperty("voices")[0].GetProperty("id").GetString());
        Assert.Equal("af_heart", list.GetProperty("builtin")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Mp3SamplesGoThroughTheDecoder()
    {
        var mp3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        var (status, _) = await SendAsync(HttpMethod.Post, Routes.Voices, CreateBody(mp3));
        Assert.Equal(HttpStatusCode.Created, status);

        var (bad, body) = await SendAsync(HttpMethod.Post, Routes.Voices, CreateBody("hello, not audio"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal(ErrorCodes.UnsupportedAudio, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RenameThenDeleteRemovesOnlyThatVoicesFolder()
    {
        var keep = await MakeVoiceAsync();
        var gone = await MakeVoiceAsync();
        var stranger = Path.Combine(VoicesDir, "my-own-folder");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "note.txt"), "mine");

        var (renamed, body) = await SendAsync(HttpMethod.Patch, $"{Routes.Voices}/{gone}", "{ \"name\": \"Other\" }");
        Assert.Equal(HttpStatusCode.OK, renamed);
        Assert.Equal("Other", body.GetProperty("name").GetString());

        var (deleted, _) = await SendAsync(HttpMethod.Delete, $"{Routes.Voices}/{gone}");
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        Assert.False(Directory.Exists(Path.Combine(VoicesDir, gone)));
        Assert.True(File.Exists(Path.Combine(VoicesDir, keep, VoiceLibrary.SampleFileName)));
        Assert.True(File.Exists(Path.Combine(stranger, "note.txt")));

        var (again, err) = await SendAsync(HttpMethod.Delete, $"{Routes.Voices}/{gone}");
        Assert.Equal(HttpStatusCode.NotFound, again);
        Assert.Equal(ErrorCodes.VoiceNotFound, err.GetProperty("error").GetProperty("code").GetString());

        var (traversal, _) = await SendAsync(HttpMethod.Delete, $"{Routes.Voices}/my-own-folder");
        Assert.Equal(HttpStatusCode.NotFound, traversal);
        Assert.True(File.Exists(Path.Combine(stranger, "note.txt")));
    }

    [Fact]
    public async Task SpeechJobRunsToACompletedTaggedWavThatCanBeDownloaded()
    {
        var voiceId = await MakeVoiceAsync();
        var (status, created) = await SendAsync(HttpMethod.Post, Routes.SpeechJobs,
            $"{{ \"voice_id\": \"{voiceId}\", \"text\": \"Hello there. How are you?\", \"speed\": 1.1 }}");
        Assert.Equal(HttpStatusCode.Accepted, status);
        var id = created.GetProperty("id").GetString()!;

        var job = await WaitForAsync(id, "completed");
        Assert.Equal(1, job.GetProperty("progress").GetDouble());
        var result = job.GetProperty("result");
        var file = Path.Combine(OutDir, result.GetProperty("file_name").GetString()!);
        Assert.True(File.Exists(file));
        Assert.True(WavAudio.HasPortalTag(File.ReadAllBytes(file)));

        using var req = _host.Authed(HttpMethod.Get, $"{Routes.SpeechJobs}/{id}/audio", _token);
        using var resp = await _host.Http.SendAsync(req);
        Assert.Equal("audio/wav", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(File.ReadAllBytes(file), await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DeleteCancelsAJob()
    {
        _engine.Gate = new SemaphoreSlim(0);
        var (_, created) = await SendAsync(HttpMethod.Post, Routes.SpeechJobs, "{ \"builtin_voice\": \"af_heart\", \"text\": \"Slow one.\" }");
        var id = created.GetProperty("id").GetString()!;
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var (status, body) = await SendAsync(HttpMethod.Delete, $"{Routes.SpeechJobs}/{id}");
        Assert.Equal(HttpStatusCode.OK, status);
        await WaitForAsync(id, "cancelled");
        Assert.False(Directory.Exists(OutDir) && Directory.EnumerateFiles(OutDir).Any());
        Assert.NotNull(body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AVoiceInUseCannotBeDeleted()
    {
        var voiceId = await MakeVoiceAsync();
        _engine.Gate = new SemaphoreSlim(0);
        var (_, created) = await SendAsync(HttpMethod.Post, Routes.SpeechJobs, $"{{ \"voice_id\": \"{voiceId}\", \"text\": \"Hold on.\" }}");
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var (status, body) = await SendAsync(HttpMethod.Delete, $"{Routes.Voices}/{voiceId}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.VoiceInUse, body.GetProperty("error").GetProperty("code").GetString());
        Assert.True(File.Exists(Path.Combine(VoicesDir, voiceId, VoiceLibrary.SampleFileName)));

        await SendAsync(HttpMethod.Delete, $"{Routes.SpeechJobs}/{created.GetProperty("id").GetString()}");
    }

    [Fact]
    public async Task AnotherAppCannotSeeOrCancelMyJob()
    {
        var (_, created) = await SendAsync(HttpMethod.Post, Routes.SpeechJobs, "{ \"builtin_voice\": \"af_heart\", \"text\": \"Mine.\" }");
        var id = created.GetProperty("id").GetString()!;
        var other = await _host.GetTokenAsync("com.mindthaapp.aiportal");

        var (status, _) = await SendAsync(HttpMethod.Get, $"{Routes.SpeechJobs}/{id}", token: other);
        Assert.Equal(HttpStatusCode.NotFound, status);
        // The voice library, in contrast, is shared.
        var (listStatus, _) = await SendAsync(HttpMethod.Get, Routes.Voices, token: other);
        Assert.Equal(HttpStatusCode.OK, listStatus);
    }

    [Fact]
    public async Task CapabilitiesAdvertiseVoiceAndFollowTheEngine()
    {
        var (_, caps) = await SendAsync(HttpMethod.Get, Routes.Capabilities);
        var voice = caps.GetProperty("voice");
        Assert.True(voice.GetProperty("cloning").GetBoolean());
        Assert.True(voice.GetProperty("speech").GetBoolean());
        Assert.Equal("wav", Assert.Single(voice.GetProperty("formats").EnumerateArray()).GetString());
        Assert.Contains("voices", caps.GetProperty("endpoints").EnumerateArray().Select(e => e.GetString()));

        _engine.Availability = VoiceAvailability.None;
        var (_, off) = await SendAsync(HttpMethod.Get, Routes.Capabilities);
        var offVoice = off.GetProperty("voice");
        Assert.False(offVoice.GetProperty("cloning").GetBoolean());
        Assert.False(offVoice.GetProperty("speech").GetBoolean());
        Assert.Empty(offVoice.GetProperty("formats").EnumerateArray());

        var (status, body) = await SendAsync(HttpMethod.Post, Routes.SpeechJobs, "{ \"builtin_voice\": \"af_heart\", \"text\": \"Hi.\" }");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.NoModel, body.GetProperty("error").GetProperty("code").GetString());
    }
}

/// <summary>A host started without the voice feature has no voice routes and no voice capability.</summary>
public sealed class NoVoiceApiTests
{
    [Fact]
    public async Task WithoutTheServiceTheCapabilityIsAbsentAndTheRoutesAreUnknown()
    {
        await using var host = await TestHost.StartAsync();
        var token = await host.GetTokenAsync();
        using var caps = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Capabilities, token));
        var text = await caps.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"voice\"", text);
        using var list = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Voices, token));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }
}
