using System.Diagnostics;
using System.Globalization;
using Portal.AiHost.VoiceServer;

namespace Portal.AiHost.VoiceServer.Tests;

/// <summary>
/// Opt-in: runs only when PORTAL_VOICE_MODELS points at a folder that holds the Kokoro folder and a Pocket TTS folder
/// (any folder names starting with "kokoro" and "sherpa-onnx-pocket-tts" or "cloning"/"fixed"). CPU only, output to the temp folder.
/// </summary>
public sealed class ModelIntegrationTests
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("PORTAL_VOICE_MODELS");

    public static bool HasModels => Root is { Length: > 0 } r && Directory.Exists(r);

    private static string? Find(params string[] prefixes) =>
        Directory.GetDirectories(Root!).FirstOrDefault(d => prefixes.Any(p => Path.GetFileName(d).StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    [Fact(SkipUnless = nameof(HasModels), SkipType = typeof(ModelIntegrationTests), Skip = "Set PORTAL_VOICE_MODELS to run.")]
    public void KokoroAndPocketCloneSpeak()
    {
        var kokoro = Find("fixed", "kokoro") ?? throw new InvalidOperationException("No Kokoro folder.");
        var pocket = Find("cloning", "sherpa-onnx-pocket-tts-2026", "sherpa-onnx-pocket-tts-int8") ?? throw new InvalidOperationException("No Pocket folder.");
        var wavs = Directory.GetFiles(Path.Combine(pocket, "test_wavs"), "*.wav");
        var sample = wavs.OrderBy(f => new FileInfo(f).Length).First();
        var outDir = Path.Combine(Path.GetTempPath(), "pvs-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        using var synth = new SherpaSynthesizer(kokoro, pocket, 4);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        Run(synth, new SpeechRequest("The quick brown fox jumps over the lazy dog. Portal speaks.", 1f, VoiceKind.BuiltIn, "af_heart", null, null), "kokoro", outDir, report, cts.Token);
        Run(synth, new SpeechRequest("This is a short test of a cloned voice.", 1f, VoiceKind.Clone, null, sample, null), "pocket", outDir, report, cts.Token);
        File.WriteAllLines(Path.Combine(outDir, "rtf.txt"), report);
    }

    private static void Run(SherpaSynthesizer synth, SpeechRequest req, string name, string outDir, List<string> report, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = synth.Synthesize(req, null, ct);
        sw.Stop();
        Assert.True(result.Seconds > 0.8, name + " made almost no audio.");
        Assert.True(result.Samples.Any(s => Math.Abs(s) > 0.01f), name + " is silent.");
        WavFileWriter.WriteNew(Path.Combine(outDir, name + ".wav"), WavFileWriter.Encode(result.Samples, result.SampleRate));
        var rtf = sw.Elapsed.TotalSeconds / result.Seconds;
        report.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {result.Seconds:F2}s audio, {sw.Elapsed.TotalSeconds:F2}s wall (includes model load), RTF {rtf:F2}, rate {result.SampleRate}"));
    }
}
