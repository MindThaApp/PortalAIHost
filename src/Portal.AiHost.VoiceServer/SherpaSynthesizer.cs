using Portal.AiHost.Core.Voice;
using SherpaOnnx;

namespace Portal.AiHost.VoiceServer;

/// <summary>
/// Kokoro (built-in voices, folder "fixed") and Pocket TTS (cloning, folder "cloning") on sherpa-onnx, CPU only.
/// One model is loaded at a time: asking for the other kind frees the first.
/// </summary>
public sealed class SherpaSynthesizer : ISpeechSynthesizer
{
    public const int CloneSampleRate = 24000;
    private const int MaxSampleBytes = 20 * 1024 * 1024;
    private const double MaxSampleSeconds = 30;
    private const double GapSeconds = 0.12;

    /// <summary>Kokoro multi-lang v1.0 speaker order.</summary>
    public static readonly IReadOnlyList<string> KokoroVoices =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah",
        "af_sky", "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis", "ef_dora", "em_alex",
        "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara", "im_nicola", "jf_alpha", "jf_gongitsune",
        "jf_nezumi", "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex", "pm_santa", "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao",
        "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang",
    ];

    private readonly string _fixedDir;
    private readonly string _cloningDir;
    private readonly int _threads;
    private OfflineTts? _tts;
    private VoiceKind? _loaded;

    public SherpaSynthesizer(string fixedDir, string cloningDir, int threads)
    {
        _fixedDir = fixedDir;
        _cloningDir = cloningDir;
        _threads = Math.Max(1, threads);
    }

    public static SherpaSynthesizer FromModelRoot(string modelRoot, int threads) =>
        new(Path.Combine(modelRoot, "fixed"), Path.Combine(modelRoot, "cloning"), threads);

    public SynthResult Synthesize(SpeechRequest request, Action<int, int>? progress, CancellationToken ct)
    {
        var tts = Load(request.Kind);
        var sid = 0;
        float[]? reference = null;
        if (request.Kind == VoiceKind.BuiltIn)
        {
            sid = FindVoice(request.VoiceId!);
        }
        else
        {
            reference = ReadSample(request.SamplePath!);
        }

        var chunks = TextChunker.Split(request.Text);
        var parts = new List<float[]>(chunks.Count);
        var rate = tts.SampleRate;
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            OfflineTtsGeneratedAudio audio;
            if (reference is null)
            {
                audio = tts.GenerateWithCallback(chunks[i], request.Speed, sid, (_, _) => ct.IsCancellationRequested ? 0 : 1);
            }
            else
            {
                var cfg = new OfflineTtsGenerationConfig
                {
                    Speed = request.Speed,
                    ReferenceAudio = reference,
                    ReferenceSampleRate = CloneSampleRate,
                };
                audio = tts.GenerateWithConfig(chunks[i], cfg, (_, _, _, _) => ct.IsCancellationRequested ? 0 : 1);
            }

            ct.ThrowIfCancellationRequested();
            if (audio.Samples is { Length: > 0 } samples)
            {
                parts.Add(samples);
                rate = audio.SampleRate;
            }

            progress?.Invoke(i + 1, chunks.Count);
        }

        if (parts.Count == 0)
        {
            throw new SpeechException("The voice engine made no audio for this text.");
        }

        var gap = (int)(rate * GapSeconds);
        var total = parts.Sum(p => p.Length) + gap * (parts.Count - 1);
        var all = new float[total];
        var pos = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            parts[i].CopyTo(all, pos);
            pos += parts[i].Length + (i < parts.Count - 1 ? gap : 0);
        }

        return new SynthResult(all, rate);
    }

    private static int FindVoice(string id)
    {
        var sid = -1;
        for (var i = 0; i < KokoroVoices.Count; i++)
        {
            if (string.Equals(KokoroVoices[i], id, StringComparison.Ordinal))
            {
                sid = i;
                break;
            }
        }

        return sid >= 0 ? sid : throw new SpeechException("Unknown built-in voice.", 404);
    }

    /// <summary>Reads the clone sample (read-only), converts it to mono 24 kHz floats.</summary>
    private static float[] ReadSample(string path)
    {
        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length is 0 or > MaxSampleBytes)
            {
                throw new SpeechException("The voice sample is empty or too large.");
            }

            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SpeechException("The voice sample could not be read.");
        }

        PcmAudio pcm;
        try
        {
            pcm = WavAudio.ConvertToMono(bytes, CloneSampleRate);
        }
        catch (AudioFormatException)
        {
            throw new SpeechException("The voice sample is not a WAV file the engine can read.");
        }

        var count = (int)Math.Min(pcm.Samples.Length, (long)(CloneSampleRate * MaxSampleSeconds));
        if (count < CloneSampleRate / 2)
        {
            throw new SpeechException("The voice sample is too short.");
        }

        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = pcm.Samples[i] / 32768f;
        }

        return result;
    }

    private OfflineTts Load(VoiceKind kind)
    {
        if (_tts is not null && _loaded == kind)
        {
            return _tts;
        }

        Unload();
        var dir = kind == VoiceKind.BuiltIn ? _fixedDir : _cloningDir;
        if (!Directory.Exists(dir))
        {
            throw new SpeechException(kind == VoiceKind.BuiltIn ? "The built-in voices are not installed." : "Voice cloning is not installed.", 503);
        }

        var config = new OfflineTtsConfig();
        config.Model.NumThreads = _threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        if (kind == VoiceKind.BuiltIn)
        {
            config.Model.Kokoro.Model = Required(dir, "model.onnx");
            config.Model.Kokoro.Voices = Required(dir, "voices.bin");
            config.Model.Kokoro.Tokens = Required(dir, "tokens.txt");
            config.Model.Kokoro.DataDir = Required(dir, "espeak-ng-data");
            config.Model.Kokoro.DictDir = Required(dir, "dict");
            config.Model.Kokoro.Lexicon = Required(dir, "lexicon-us-en.txt") + "," + Required(dir, "lexicon-zh.txt");
            config.RuleFsts = string.Join(",", new[] { "date-zh.fst", "number-zh.fst", "phone-zh.fst" }.Select(f => Path.Combine(dir, f)));
        }
        else
        {
            config.Model.Pocket.LmFlow = Pick(dir, "lm_flow");
            config.Model.Pocket.LmMain = Pick(dir, "lm_main");
            config.Model.Pocket.Encoder = Pick(dir, "encoder");
            config.Model.Pocket.Decoder = Pick(dir, "decoder");
            config.Model.Pocket.TextConditioner = Pick(dir, "text_conditioner");
            config.Model.Pocket.VocabJson = Required(dir, "vocab.json");
            config.Model.Pocket.TokenScoresJson = Required(dir, "token_scores.json");
        }

        _tts = new OfflineTts(config);
        _loaded = kind;
        return _tts;
    }

    private static string Required(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        return File.Exists(p) || Directory.Exists(p) ? p : throw new SpeechException("A model file is missing: " + name, 503);
    }

    /// <summary>Full-precision file when present, else the int8 one.</summary>
    private static string Pick(string dir, string stem)
    {
        var full = Path.Combine(dir, stem + ".onnx");
        if (File.Exists(full))
        {
            return full;
        }

        var q = Path.Combine(dir, stem + ".int8.onnx");
        return File.Exists(q) ? q : throw new SpeechException("A model file is missing: " + stem + ".onnx", 503);
    }

    private void Unload()
    {
        _tts?.Dispose();
        _tts = null;
        _loaded = null;
    }

    public void Dispose() => Unload();
}
