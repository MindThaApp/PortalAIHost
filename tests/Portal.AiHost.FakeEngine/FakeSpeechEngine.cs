using System.Collections.Concurrent;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Voice;

namespace Portal.AiHost.FakeEngine;

/// <summary>In-memory speech engine for tests: a short tone per chunk, optional gate to hold a chunk, no process.</summary>
public sealed class FakeSpeechEngine : ISpeechEngine
{
    public VoiceAvailability Availability { get; set; } = new(
        Cloning: true,
        BuiltIn: true,
        BuiltInVoices: [new BuiltInVoiceInfo { Id = "af_heart", Name = "Heart", Language = "en-US" }]);

    /// <summary>Every chunk the engine was asked to read, in order.</summary>
    public ConcurrentQueue<SpeechChunkRequest> Chunks { get; } = new();

    /// <summary>When set, a chunk waits here before it is read (to test cancel and progress).</summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Raised when a chunk starts, before the gate.</summary>
    public SemaphoreSlim Started { get; } = new(0);

    public async Task<PcmAudio> SynthesizeAsync(SpeechChunkRequest request, CancellationToken ct)
    {
        Chunks.Enqueue(request);
        Started.Release();
        if (Gate is { } gate)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (request.Text.Contains("FAIL", StringComparison.Ordinal))
        {
            throw new EngineException(ErrorCodes.EngineError, "fake failure");
        }

        const int rate = 24000;
        var samples = new short[rate / 10];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
        }

        return new PcmAudio(samples, rate);
    }
}
