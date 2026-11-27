using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Voice;

/// <summary>What the voice engine can do right now (files present, models installed).</summary>
public sealed record VoiceAvailability(
    bool Cloning,
    bool BuiltIn,
    IReadOnlyList<BuiltInVoiceInfo> BuiltInVoices,
    string? Reason = null)
{
    public static readonly VoiceAvailability None = new(false, false, [], UnavailableReasons.NoModel);

    public bool Speech => Cloning || BuiltIn;
}

/// <summary>One piece of text to read. The text is already split into sentence-sized pieces by the job runner.</summary>
public sealed record SpeechChunkRequest(string Text, double Speed, string? BuiltInVoice, string? CloneSamplePath);

/// <summary>
/// The engine that turns text into audio. The real one (Portal.AiHost.Engine) supervises the voice helper process; tests
/// use an in-memory fake. Calls are made one at a time by the job runner.
/// </summary>
public interface ISpeechEngine
{
    VoiceAvailability Availability { get; }

    /// <summary>Reads one chunk. Throws <see cref="Portal.AiHost.Core.Abstractions.EngineException"/> on engine failure.</summary>
    Task<PcmAudio> SynthesizeAsync(SpeechChunkRequest request, CancellationToken ct);
}

/// <summary>Turns a non-WAV sample (MP3) into a WAV file. Null when this host cannot decode it.</summary>
public interface IAudioDecoder
{
    IReadOnlyList<string> Formats { get; }

    Task<byte[]> DecodeToWavAsync(byte[] input, string format, CancellationToken ct);
}
