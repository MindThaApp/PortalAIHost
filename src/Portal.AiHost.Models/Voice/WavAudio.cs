using System.Buffers.Binary;
using System.Text;

namespace Portal.AiHost.Core.Voice;

/// <summary>Why a sample or a generated file could not be read.</summary>
public sealed class AudioFormatException(string message) : Exception(message);

/// <summary>Facts about a parsed WAV file.</summary>
public sealed record WavInfo(int Channels, int SampleRate, int BitsPerSample, bool IsFloat, int DataOffset, int DataLength)
{
    public int BytesPerFrame => Channels * (BitsPerSample / 8);
    public int Frames => BytesPerFrame == 0 ? 0 : DataLength / BytesPerFrame;
    public double Seconds => SampleRate == 0 ? 0 : (double)Frames / SampleRate;
}

/// <summary>Mono 16-bit samples with their rate.</summary>
public sealed record PcmAudio(short[] Samples, int SampleRate)
{
    public long DurationMs => SampleRate == 0 ? 0 : Samples.LongLength * 1000 / SampleRate;
}

/// <summary>
/// Minimal WAV reading and writing for the voice feature: parse PCM and float files, convert to mono 16-bit at a target
/// rate, and write 16-bit mono files that carry a "made with Portal" LIST/INFO chunk. Pure functions on byte arrays.
/// </summary>
public static class WavAudio
{
    public const string MadeWithPortal = "Made with Portal";
    public const string Software = "Portal AI Host";

    public static bool LooksLikeWav(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8);

    public static bool LooksLikeMp3(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && ((data[0] == 'I' && data[1] == 'D' && data[2] == '3') ||
            (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0));

    public static WavInfo Parse(ReadOnlySpan<byte> wav)
    {
        if (!LooksLikeWav(wav))
        {
            throw new AudioFormatException("Not a WAV file.");
        }

        int channels = 0, rate = 0, bits = 0;
        var isFloat = false;
        var haveFmt = false;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > wav.Length)
                {
                    throw new AudioFormatException("The WAV header is damaged.");
                }

                var tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
                if (tag == 0xFFFE && size >= 26 && body + 26 <= wav.Length)
                {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 24, 2));
                }

                if (tag is not (1 or 3))
                {
                    throw new AudioFormatException("Only PCM and float WAV files are supported.");
                }

                isFloat = tag == 3;
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFmt)
                {
                    throw new AudioFormatException("The WAV file has no format chunk.");
                }

                if (channels is < 1 or > 8 || rate is < 8000 or > 192000 ||
                    !(bits is 8 or 16 or 24 or 32) || (isFloat && bits != 32))
                {
                    throw new AudioFormatException("This WAV format is not supported.");
                }

                var available = wav.Length - body;
                var length = (int)Math.Min(size, (uint)available); // A streamed file may claim more than it holds.
                var frame = channels * (bits / 8);
                return new WavInfo(channels, rate, bits, isFloat, body, length - length % frame);
            }

            var next = (long)body + size + (size & 1);
            if (next > wav.Length)
            {
                break;
            }

            pos = (int)next;
        }

        throw new AudioFormatException("The WAV file has no audio data.");
    }

    /// <summary>Reads the audio as mono 16-bit samples at the file's own rate.</summary>
    public static PcmAudio ReadMono(ReadOnlySpan<byte> wav)
    {
        var info = Parse(wav);
        var data = wav.Slice(info.DataOffset, info.DataLength);
        var frames = info.Frames;
        var bytes = info.BitsPerSample / 8;
        var samples = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var c = 0; c < info.Channels; c++)
            {
                var o = (f * info.Channels + c) * bytes;
                sum += info.BitsPerSample switch
                {
                    8 => (data[o] - 128) / 128.0,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(o, 2)) / 32768.0,
                    24 => (data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16)) / 8388608.0,
                    _ => info.IsFloat
                        ? BitConverter.ToSingle(data.Slice(o, 4))
                        : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(o, 4)) / 2147483648.0,
                };
            }

            samples[f] = (short)Math.Clamp(Math.Round(sum / info.Channels * 32767.0), short.MinValue, short.MaxValue);
        }

        return new PcmAudio(samples, info.SampleRate);
    }

    /// <summary>Area-average resampler: good enough for speech references, no dependencies.</summary>
    public static PcmAudio Resample(PcmAudio audio, int targetRate)
    {
        if (audio.SampleRate == targetRate || audio.Samples.Length == 0)
        {
            return audio with { SampleRate = targetRate };
        }

        var ratio = (double)audio.SampleRate / targetRate;
        var outLength = (int)Math.Max(1, Math.Floor(audio.Samples.Length / ratio));
        var result = new short[outLength];
        var src = audio.Samples;
        for (var i = 0; i < outLength; i++)
        {
            var start = i * ratio;
            if (ratio <= 1)
            {
                var i0 = Math.Min((int)Math.Floor(start), src.Length - 1);
                var i1 = Math.Min(i0 + 1, src.Length - 1);
                var frac = start - i0;
                result[i] = (short)Math.Round(src[i0] * (1 - frac) + src[i1] * frac);
                continue;
            }

            var end = start + ratio;
            double acc = 0, weight = 0;
            for (var j = (int)Math.Floor(start); j < Math.Ceiling(end) && j < src.Length; j++)
            {
                var w = Math.Min(end, j + 1) - Math.Max(start, j);
                if (w <= 0)
                {
                    continue;
                }

                acc += src[j] * w;
                weight += w;
            }

            result[i] = weight <= 0 ? (short)0 : (short)Math.Round(acc / weight);
        }

        return new PcmAudio(result, targetRate);
    }

    /// <summary>Any supported WAV in, mono 16-bit at <paramref name="targetRate"/> out.</summary>
    public static PcmAudio ConvertToMono(ReadOnlySpan<byte> wav, int targetRate) => Resample(ReadMono(wav), targetRate);

    /// <summary>
    /// Writes a 16-bit mono WAV. The file ends with a LIST/INFO chunk (ISFT, ICMT) saying it was made with Portal,
    /// which every file the host produces carries, whatever its content.
    /// </summary>
    public static byte[] WritePcm16(PcmAudio audio)
    {
        var dataBytes = audio.Samples.Length * 2;
        var info = BuildInfoChunk();
        var total = 44 + dataBytes + info.Length;
        var buffer = new byte[total];
        var span = buffer.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(total - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(audio.SampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        for (var i = 0; i < audio.Samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], audio.Samples[i]);
        }

        info.CopyTo(span[(44 + dataBytes)..]);
        return buffer;
    }

    /// <summary>True when the file has the host's "made with Portal" comment in a LIST/INFO chunk.</summary>
    public static bool HasPortalTag(ReadOnlySpan<byte> wav)
    {
        if (!LooksLikeWav(wav))
        {
            return false;
        }

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = pos + 8;
            if (wav.Slice(pos, 4).SequenceEqual("LIST"u8) && body + 4 <= wav.Length &&
                wav.Slice(body, 4).SequenceEqual("INFO"u8))
            {
                var end = (int)Math.Min((long)body + size, wav.Length);
                var p = body + 4;
                while (p + 8 <= end)
                {
                    var subSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(p + 4, 4));
                    if (wav.Slice(p, 4).SequenceEqual("ICMT"u8) && subSize >= 0 && p + 8 + subSize <= wav.Length &&
                        Encoding.ASCII.GetString(wav.Slice(p + 8, subSize)).TrimEnd('\0') == MadeWithPortal)
                    {
                        return true;
                    }

                    p += 8 + subSize + (subSize & 1);
                }
            }

            var next = (long)body + size + (size & 1);
            if (next > wav.Length)
            {
                break;
            }

            pos = (int)next;
        }

        return false;
    }

    private static byte[] BuildInfoChunk()
    {
        static byte[] Sub(string id, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            var padded = bytes.Length + (bytes.Length & 1);
            var result = new byte[8 + padded];
            Encoding.ASCII.GetBytes(id).CopyTo(result, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)bytes.Length);
            bytes.CopyTo(result, 8);
            return result;
        }

        var software = Sub("ISFT", Software);
        var comment = Sub("ICMT", MadeWithPortal);
        var listSize = 4 + software.Length + comment.Length;
        var chunk = new byte[8 + listSize];
        "LIST"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)listSize);
        "INFO"u8.CopyTo(chunk.AsSpan(8));
        software.CopyTo(chunk, 12);
        comment.CopyTo(chunk, 12 + software.Length);
        return chunk;
    }
}
