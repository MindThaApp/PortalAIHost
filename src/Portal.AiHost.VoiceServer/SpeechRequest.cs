using System.Text.Json;

namespace Portal.AiHost.VoiceServer;

public enum VoiceKind
{
    BuiltIn,
    Clone,
}

/// <summary>One /v1/speech request: { text, speed, format, voice, out_path? }.</summary>
public sealed record SpeechRequest(string Text, float Speed, VoiceKind Kind, string? VoiceId, string? SamplePath, string? OutPath)
{
    public const int MaxTextChars = 5000;
    public const int MaxBodyBytes = 256 * 1024;

    public static SpeechRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > MaxBodyBytes)
        {
            throw new FormatException("The request body is empty or too large.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            throw new FormatException("The request is not valid JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The request must be an object.");
            }

            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextChars)
            {
                throw new FormatException("text is missing or too long.");
            }

            var speed = 1f;
            if (root.TryGetProperty("speed", out var s))
            {
                if (s.ValueKind != JsonValueKind.Number || !s.TryGetSingle(out speed) || float.IsNaN(speed))
                {
                    throw new FormatException("speed must be a number.");
                }

                speed = Math.Clamp(speed, 0.5f, 2f);
            }

            if (root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() != "wav")
            {
                throw new FormatException("Only wav is supported.");
            }

            if (!root.TryGetProperty("voice", out var v) || v.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("voice is missing.");
            }

            var kind = v.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            string? outPath = null;
            if (root.TryGetProperty("out_path", out var o) && o.ValueKind == JsonValueKind.String)
            {
                outPath = o.GetString();
            }

            if (outPath is not null && LocalPath.Check(outPath) is { } outReason)
            {
                throw new FormatException("out_path: " + outReason);
            }

            switch (kind)
            {
                case "builtin":
                    var id = v.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        throw new FormatException("voice.id is missing.");
                    }

                    return new SpeechRequest(text, speed, VoiceKind.BuiltIn, id, null, outPath);
                case "clone":
                    var sample = v.TryGetProperty("sample_path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(sample))
                    {
                        throw new FormatException("voice.sample_path is missing.");
                    }

                    if (LocalPath.Check(sample) is { } sampleReason)
                    {
                        throw new FormatException("sample_path: " + sampleReason);
                    }

                    return new SpeechRequest(text, speed, VoiceKind.Clone, null, sample, outPath);
                default:
                    throw new FormatException("voice.kind must be builtin or clone.");
            }
        }
    }
}

/// <summary>Mono samples in -1..1 and their rate.</summary>
public sealed record SynthResult(float[] Samples, int SampleRate)
{
    public double Seconds => SampleRate == 0 ? 0 : (double)Samples.Length / SampleRate;
}

/// <summary>Why a request cannot be served (unknown voice, model not installed, bad sample). The message is safe to show.</summary>
public sealed class SpeechException(string message, int status = 422) : Exception(message)
{
    public int Status { get; } = status;
}

public interface ISpeechSynthesizer : IDisposable
{
    /// <summary>Speaks the text piece by piece. Reports (done, total) after each piece; stops between pieces when cancelled.</summary>
    SynthResult Synthesize(SpeechRequest request, Action<int, int>? progress, CancellationToken ct);
}
