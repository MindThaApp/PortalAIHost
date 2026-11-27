using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Portal.AiHost.Engine;
using Xunit;
using HelperClient = Portal.AiHost.Engine.UpscaleHelper;

namespace Portal.AiHost.UpscaleHelper.Tests;

/// <summary>The line protocol with a stub model (in process), and the whole path through the real helper exe with a tiny generated model on the CPU.</summary>
public sealed class ProtocolTests
{
    private sealed class Wire : IDisposable
    {
        private readonly AnonymousPipeServerStream _toSession = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream _sessionIn;
        private readonly AnonymousPipeServerStream _fromSession = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _sessionOut;
        public Task<int> Run;

        public Wire(Func<JsonObject, IModelRunner> open)
        {
            _sessionIn = new AnonymousPipeClientStream(PipeDirection.In, _toSession.ClientSafePipeHandle);
            _sessionOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromSession.ClientSafePipeHandle);
            Run = Task.Run(() => new UpscaleSession(_sessionIn, _sessionOut, open).RunAsync());
        }

        public void Send(string line) => _toSession.Write(Encoding.UTF8.GetBytes(line + "\n"));

        public void SendBytes(byte[] b) => _toSession.Write(b);

        public JsonObject Next()
        {
            var buf = new List<byte>();
            while (true)
            {
                var b = _fromSession.ReadByte();
                Assert.True(b >= 0, "the helper closed its output");
                if (b == '\n')
                {
                    return JsonNode.Parse(Encoding.UTF8.GetString([.. buf]))!.AsObject();
                }

                buf.Add((byte)b);
            }
        }

        public JsonObject NextNonProgress()
        {
            while (true)
            {
                var m = Next();
                if ((string?)m["event"] != "progress")
                {
                    return m;
                }
            }
        }

        public byte[] ReadBytes(int n)
        {
            var b = new byte[n];
            _fromSession.ReadExactly(b);
            return b;
        }

        public void Dispose()
        {
            _toSession.Dispose();
            _fromSession.Dispose();
            _sessionIn.Dispose();
            _sessionOut.Dispose();
        }
    }

    [Fact]
    public async Task Load_upscale_raw_and_file_round_trip_with_a_stub_model()
    {
        var dir = TestImages.TempDir();
        try
        {
            using var wire = new Wire(_ => new NearestRunner(2));
            Assert.Equal("ready", (string?)wire.Next()["event"]);
            wire.Send("""{"op":"load","id":"l","model":"x","tile":64,"overlap":8}""");
            var loaded = wire.Next();
            Assert.Equal("loaded", (string?)loaded["event"]);
            Assert.Equal(2, (int?)loaded["scale"]);
            Assert.True((long?)loaded["peakWorkingSetBytes"] > 0);

            var rgb = TestImages.Gradient(70, 50);
            wire.Send("""{"op":"upscale-raw","id":"r","width":70,"height":50}""");
            wire.SendBytes(rgb);
            var frame = wire.NextNonProgress();
            Assert.Equal("frame", (string?)frame["event"]);
            Assert.Equal((140, 100), ((int?)frame["width"], (int?)frame["height"]));
            var bytes = wire.ReadBytes((int)frame["bytes"]!);
            var expected = TestImages.NearestExpected(rgb, 70, 50, 2);
            Assert.Equal(expected.Length, bytes.Length);
            Assert.All(bytes.Zip(expected), p => Assert.InRange(Math.Abs(p.First - p.Second), 0, 1));

            var input = Path.Combine(dir, "in.png");
            File.WriteAllBytes(input, Png.Encode(rgb, 70, 50));
            var outDir = Path.Combine(dir, "out");
            wire.Send(JsonSerialize("upscale", "f", input, outDir));
            var done = wire.NextNonProgress();
            Assert.Equal("done", (string?)done["event"]);
            var first = (string)done["output"]!;
            Assert.StartsWith(outDir, first);
            Assert.True((long?)done["peakWorkingSetBytes"] > 0);

            wire.Send(JsonSerialize("upscale", "g", input, outDir));
            var second = (string)wire.NextNonProgress()["output"]!;
            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first) && File.Exists(second));
            Assert.Equal(140, Png.Decode(File.ReadAllBytes(first)).Width);

            wire.Send("""{"op":"ping"}""");
            Assert.Equal("pong", (string?)wire.Next()["event"]);
            wire.Send("""{"op":"quit"}""");
            Assert.Equal("bye", (string?)wire.Next()["event"]);
            Assert.Equal(0, await wire.Run);
        }
        finally
        {
            Directory.Delete(dir, true); // Test folder created above.
        }
    }

    [Fact]
    public async Task Errors_are_reported_and_cancel_ends_the_running_job()
    {
        var gate = new ManualResetEventSlim();
        var runner = new NearestRunner(2) { OnCall = _ => gate.Wait(TimeSpan.FromSeconds(10)) };
        using var wire = new Wire(_ => runner);
        wire.Next();
        wire.Send("""{"op":"upscale","id":"a","input":"nope.png","outputDir":"x"}""");
        var err = wire.Next();
        Assert.Equal("error", (string?)err["event"]);
        Assert.Contains("No model", (string?)err["message"]);

        wire.Send("""{"op":"load","id":"l","model":"x","tile":64,"overlap":8}""");
        wire.Next();
        var rgb = TestImages.Gradient(70, 50);
        wire.Send("""{"op":"upscale-raw","id":"r","width":70,"height":50}""");
        wire.SendBytes(rgb);
        wire.Send("""{"op":"upscale-raw","id":"busy","width":70,"height":50}""");
        wire.SendBytes(rgb);
        var busy = wire.Next();
        Assert.Equal("busy", (string?)busy["message"]);
        wire.Send("""{"op":"cancel"}""");
        gate.Set();
        var cancelled = wire.NextNonProgress();
        Assert.Equal("cancelled", (string?)cancelled["event"]);
        wire.Send("""{"op":"quit"}""");
        Assert.Equal("bye", (string?)wire.NextNonProgress()["event"]);
        Assert.Equal(0, await wire.Run);
    }

    private static string JsonSerialize(string op, string id, string input, string outDir) =>
        new JsonObject { ["op"] = op, ["id"] = id, ["input"] = input, ["outputDir"] = outDir }.ToJsonString();

    [Fact]
    public async Task Host_client_runs_the_real_helper_with_a_generated_model_on_the_cpu()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "portal-upscale.exe");
        Assert.True(File.Exists(exe), "portal-upscale.exe is next to the test assembly");
        var dir = TestImages.TempDir();
        await using var helper = new HelperClient(exe);
        try
        {
            var model = Path.Combine(dir, "nearest2.onnx");
            File.WriteAllBytes(model, TinyOnnx.NearestResize(2));

            var loaded = await helper.LoadAsync(model, "cpu", 64, 8, 2, CancellationToken.None);
            Assert.Equal(2, loaded.Scale);
            Assert.Equal("cpu", loaded.Device);
            Assert.True(await helper.HealthAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

            var rgb = TestImages.Gradient(90, 70);
            var progress = new List<int>();
            var frame = await helper.UpscaleRawAsync(rgb, 90, 70, 2, (d, _) => progress.Add(d), CancellationToken.None);
            Assert.Equal((180, 140), (frame.Width, frame.Height));
            Assert.Equal(TestImages.NearestExpected(rgb, 90, 70, 2).Length, frame.Rgb.Length);
            Assert.NotEmpty(progress);
            Assert.True(frame.PeakWorkingSetBytes > 0);

            var input = Path.Combine(dir, "in.png");
            File.WriteAllBytes(input, Png.Encode(rgb, 90, 70));
            var outDir = Path.Combine(dir, "out");
            var one = await helper.UpscaleFileAsync(input, outDir, null, 2, null, CancellationToken.None);
            var two = await helper.UpscaleFileAsync(input, outDir, null, 2, null, CancellationToken.None);
            Assert.NotEqual(one.OutputPath, two.OutputPath);
            Assert.Equal(180, Png.Decode(File.ReadAllBytes(one.OutputPath)).Width);

            // A DirectML request works or falls back to the CPU; either way a model is loaded.
            var dml = await helper.LoadAsync(model, "dml", 64, 8, 0, CancellationToken.None);
            Assert.True(dml.Device is "cpu" or "dml:0");

            // A helper that died is started again on the next call, with the model loaded again.
            helper.Stop();
            await Task.Delay(300);
            Assert.False(helper.IsRunning);
            var again = await helper.UpscaleRawAsync(rgb, 90, 70, 2, null, CancellationToken.None);
            Assert.Equal(180, again.Width);

            await Assert.ThrowsAnyAsync<Exception>(() => helper.UpscaleFileAsync(Path.Combine(dir, "missing.png"), outDir, null, 2, null, CancellationToken.None));
        }
        finally
        {
            await helper.DisposeAsync();
            Directory.Delete(dir, true); // Test folder created above.
        }
    }
}
