using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Voice;
using Portal.AiHost.Engine;
using Portal.AiHost.VoiceServer;

namespace Portal.AiHost.VoiceServer.Tests;

/// <summary>The Host adapter's command line and request body go into the helper's parser and server, with a fake engine.</summary>
public sealed class AdapterRoundTripTests
{
    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public SpeechRequest? Last;
        public bool Throw404;

        public SynthResult Synthesize(SpeechRequest request, Action<int, int>? progress, CancellationToken ct)
        {
            Last = request;
            if (Throw404)
            {
                throw new SpeechException("Unknown built-in voice.", 404);
            }

            progress?.Invoke(1, 1);
            return new SynthResult(Enumerable.Range(0, 2400).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray(), 24000);
        }

        public void Dispose()
        {
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public void HostArgumentsParseInTheHelper()
    {
        var spec = new EngineLaunchSpec { Engine = EngineKinds.VoiceServer, Kind = ModelKinds.Voice, ModelFiles = [@"C:\data\voice-models"], Threads = 5 };
        var args = VoiceServerArgs.Build(spec, new EngineEndpoint(45999, "key999"));
        var parsed = VoiceArgs.Parse(args);
        Assert.Equal(45999, parsed.Port);
        Assert.Equal("key999", parsed.ApiKey);
        Assert.Equal(5, parsed.Threads);
        Assert.Equal(@"C:\data\voice-models", parsed.ModelDir);
    }

    [Fact]
    public async Task HealthAndSpeechRoundTrip()
    {
        var port = FreePort();
        var fake = new FakeSynth();
        using var server = new VoiceHttpServer(port, "secret", fake, TextWriter.Null);
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/health")).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);

            var body = VoiceEngineClient.BuildBody(new SpeechChunkRequest("Hello.", 1.25f, "af_heart", null));
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await http.PostAsync("/v1/speech", content);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var pcm = WavAudio.ReadMono(await resp.Content.ReadAsByteArrayAsync());
            Assert.Equal(24000, pcm.SampleRate);
            Assert.Equal(2400, pcm.Samples.Length);
            Assert.Equal(VoiceKind.BuiltIn, fake.Last!.Kind);
            Assert.Equal("af_heart", fake.Last.VoiceId);
            Assert.Equal(1.25f, fake.Last.Speed);

            var clone = VoiceEngineClient.BuildBody(new SpeechChunkRequest("Hi.", 1f, null, @"C:\v\sample.wav"));
            using var c2 = new ByteArrayContent(clone);
            using var r2 = await http.PostAsync("/v1/speech", c2);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal(VoiceKind.Clone, fake.Last.Kind);
            Assert.Equal(@"C:\v\sample.wav", fake.Last.SamplePath);

            fake.Throw404 = true;
            using var c3 = new ByteArrayContent(body);
            using var r3 = await http.PostAsync("/v1/speech", c3);
            Assert.Equal(HttpStatusCode.NotFound, r3.StatusCode);

            using var bad = new ByteArrayContent("{}"u8.ToArray());
            using var r4 = await http.PostAsync("/v1/speech", bad);
            Assert.Equal(HttpStatusCode.BadRequest, r4.StatusCode);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task OutPathWritesANewFileOnlyOnce()
    {
        var port = FreePort();
        var dir = Path.Combine(Path.GetTempPath(), "pvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var server = new VoiceHttpServer(port, "secret", new FakeSynth(), TextWriter.Null);
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
            var outPath = Path.Combine(dir, "o.wav").Replace("\\", "\\\\");
            var json = "{\"text\":\"Hi.\",\"voice\":{\"kind\":\"builtin\",\"id\":\"af_heart\"},\"out_path\":\"" + outPath + "\"}";
            using var first = await http.PostAsync("/v1/speech", new StringContent(json));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var written = File.ReadAllBytes(Path.Combine(dir, "o.wav"));
            using var second = await http.PostAsync("/v1/speech", new StringContent(json));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(written, File.ReadAllBytes(Path.Combine(dir, "o.wav")));
        }
        finally
        {
            await server.StopAsync();
            Directory.Delete(dir, true); // The folder this test created.
        }
    }
}
