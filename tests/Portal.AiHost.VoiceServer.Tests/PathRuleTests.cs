using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Portal.AiHost.VoiceServer;

namespace Portal.AiHost.VoiceServer.Tests;

public sealed class PathRuleTests
{
    [Theory]
    [InlineData(@"C:\voices\a.wav")]
    [InlineData(@"d:\x\y z\a (2).wav")]
    public void PlainDrivePathsPass(string p) => Assert.Null(LocalPath.Check(p));

    [Theory]
    [InlineData(@"\\server\share\a.wav")]
    [InlineData(@"\\.\C:\a.wav")]
    [InlineData(@"\\?\C:\a.wav")]
    [InlineData("//server/share/a.wav")]
    [InlineData(@"a.wav")]
    [InlineData(@"..\a.wav")]
    [InlineData(@"\a.wav")]
    [InlineData(@"C:a.wav")]
    [InlineData(@"C:\a.wav:stream")]
    [InlineData(@"C:\dir\..\a.wav")]
    [InlineData(@"C:\dir\.\a.wav")]
    [InlineData(@"C:\NUL")]
    [InlineData(@"C:\dir\com1.wav")]
    [InlineData(@"C:\dir\CON.txt")]
    [InlineData(@"C:\dir\a.wav.")]
    [InlineData(@"C:\dir\a?.wav")]
    [InlineData("C:\\dir\\a\0.wav")]
    [InlineData("")]
    public void OtherPathsAreRefused(string p) => Assert.NotNull(LocalPath.Check(p));

    [Fact]
    public void RequestParserRefusesBadPaths()
    {
        static byte[] Body(string voice, string extra = "") =>
            System.Text.Encoding.UTF8.GetBytes("{\"text\":\"Hi.\",\"voice\":" + voice + extra + "}");

        Assert.Throws<FormatException>(() => SpeechRequest.Parse(Body("{\"kind\":\"clone\",\"sample_path\":\"\\\\\\\\srv\\\\s\\\\a.wav\"}")));
        Assert.Throws<FormatException>(() => SpeechRequest.Parse(Body("{\"kind\":\"clone\",\"sample_path\":\"a.wav\"}")));
        Assert.Throws<FormatException>(() => SpeechRequest.Parse(Body("{\"kind\":\"builtin\",\"id\":\"af_heart\"}", ",\"out_path\":\"C:\\\\x\\\\NUL\"")));
        Assert.Throws<FormatException>(() => SpeechRequest.Parse(Body("{\"kind\":\"builtin\",\"id\":\"af_heart\"}").Concat("x"u8.ToArray()).ToArray()));
    }

    [Fact]
    public void TextOverTheCapIsRefused()
    {
        var json = "{\"text\":\"" + new string('a', SpeechRequest.MaxTextChars + 1) + "\",\"voice\":{\"kind\":\"builtin\",\"id\":\"x\"}}";
        Assert.Throws<FormatException>(() => SpeechRequest.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void NewFileCheckRefusesExistingFileAndMissingFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "a.wav");
            Assert.Null(LocalPath.CheckNewFile(f));
            File.WriteAllBytes(f, [1]);
            Assert.NotNull(LocalPath.CheckNewFile(f));
            Assert.NotNull(LocalPath.CheckNewFile(Path.Combine(dir, "no", "a.wav")));
        }
        finally
        {
            Directory.Delete(dir, true); // The folder this test created.
        }
    }

    private sealed class CountingSynth : ISpeechSynthesizer
    {
        public int Calls;

        public SynthResult Synthesize(SpeechRequest request, Action<int, int>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return new SynthResult(new float[2400], 24000);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task BadOutPathIsRefusedBeforeAnySynthesis()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var synth = new CountingSynth();
        using var server = new VoiceHttpServer(port, "k", synth, TextWriter.Null);
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "k");
            var missing = Path.Combine(Path.GetTempPath(), "pvs-none-" + Guid.NewGuid().ToString("N"), "a.wav").Replace("\\", "\\\\");
            var json = "{\"text\":\"Hi.\",\"voice\":{\"kind\":\"builtin\",\"id\":\"af_heart\"},\"out_path\":\"" + missing + "\"}";
            using var r = await http.PostAsync("/v1/speech", new StringContent(json));
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
            Assert.Equal(0, synth.Calls);
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
