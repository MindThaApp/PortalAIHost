using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>
/// The encoders the host knows how to drive, their preset flags and quality ranges (one table, pure). Whether an
/// encoder works on this PC is decided by <see cref="EncoderProbe"/>; nothing here starts a process.
/// </summary>
public static class FfmpegEncoders
{
    /// <summary>
    /// One known encoder. <see cref="Worst"/> and <see cref="Best"/> are the knob values at quality 0 and 100;
    /// <see cref="FastQ"/>, <see cref="BalancedQ"/> and <see cref="BestQ"/> the preset's own knob value.
    /// </summary>
    public sealed record Spec(string Id, string Codec, string Vendor, bool Hardware, int Worst, int Best,
        int FastQ, int BalancedQ, int BestQ, string[] Fast, string[] Balanced, string[] BestPreset);

    /// <summary>The Windows H.264 encoder supplied by the OS; the default of jobs without an encode object.</summary>
    public const string MfH264 = "h264_mf";

    private static readonly string[] None = [];

    /// <summary>All candidates, in the order "auto" prefers them within one codec (hardware, then Windows, then software).</summary>
    public static IReadOnlyList<Spec> Candidates { get; } =
    [
        new("h264_nvenc", EncodeValues.H264, "NVIDIA", true, 40, 14, 28, 23, 19, ["-preset", "p2"], ["-preset", "p5", "-tune", "hq"], ["-preset", "p7", "-tune", "hq", "-multipass", "fullres"]),
        new("hevc_nvenc", EncodeValues.Hevc, "NVIDIA", true, 40, 14, 28, 23, 19, ["-preset", "p2"], ["-preset", "p5", "-tune", "hq"], ["-preset", "p7", "-tune", "hq", "-multipass", "fullres"]),
        new("av1_nvenc", EncodeValues.Av1, "NVIDIA", true, 51, 12, 34, 30, 26, ["-preset", "p2"], ["-preset", "p5", "-tune", "hq"], ["-preset", "p7", "-tune", "hq", "-multipass", "fullres"]),
        new("h264_amf", EncodeValues.H264, "AMD", true, 40, 14, 28, 24, 20, ["-quality", "speed"], ["-quality", "balanced"], ["-quality", "quality"]),
        new("hevc_amf", EncodeValues.Hevc, "AMD", true, 40, 14, 28, 24, 20, ["-quality", "speed"], ["-quality", "balanced"], ["-quality", "quality"]),
        new("av1_amf", EncodeValues.Av1, "AMD", true, 40, 14, 28, 24, 20, ["-quality", "speed"], ["-quality", "balanced"], ["-quality", "quality"]),
        new("h264_qsv", EncodeValues.H264, "Intel", true, 40, 14, 28, 24, 20, ["-preset", "veryfast"], ["-preset", "medium"], ["-preset", "veryslow"]),
        new("hevc_qsv", EncodeValues.Hevc, "Intel", true, 40, 14, 28, 24, 20, ["-preset", "veryfast"], ["-preset", "medium"], ["-preset", "veryslow"]),
        new(MfH264, EncodeValues.H264, "Windows", false, 0, 100, 60, 75, 88, None, None, None),
        new("hevc_mf", EncodeValues.Hevc, "Windows", false, 0, 100, 60, 75, 88, None, None, None),
        new("libsvtav1", EncodeValues.Av1, "software", false, 55, 15, 38, 32, 26, ["-preset", "10"], ["-preset", "7"], ["-preset", "4"]),
        new("libaom-av1", EncodeValues.Av1, "software", false, 55, 15, 36, 30, 26, ["-cpu-used", "8", "-row-mt", "1"], ["-cpu-used", "6", "-row-mt", "1"], ["-cpu-used", "4", "-row-mt", "1"]),
        new("librav1e", EncodeValues.Av1, "software", false, 220, 40, 120, 90, 70, ["-speed", "10"], ["-speed", "6"], ["-speed", "4"]),
        new("libvpx-vp9", EncodeValues.Vp9, "software", false, 55, 15, 38, 32, 28, ["-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1"], ["-deadline", "good", "-cpu-used", "2", "-row-mt", "1"], ["-deadline", "good", "-cpu-used", "1", "-row-mt", "1"]),
    ];

    public static Spec? Find(string id) => Candidates.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The knob value (CRF, CQ, QP, ...) for a quality of 0-100; null quality is the preset's own value.</summary>
    public static int KnobValue(Spec s, string preset, int? quality)
    {
        if (quality is not { } q)
        {
            return preset switch { EncodeValues.Fast => s.FastQ, EncodeValues.Best => s.BestQ, _ => s.BalancedQ };
        }

        return (int)Math.Round(s.Worst + (s.Best - s.Worst) * Math.Clamp(q, 0, 100) / 100.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The <c>-c:v ...</c> part of an encode (encoder, preset flags, rate); the pixel format is added by the caller.</summary>
    public static IReadOnlyList<string> VideoArgs(MediaEncodeSettings e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var s = Find(e.EncoderId) ?? throw new ArgumentException("Unknown encoder.", nameof(e));
        var a = new List<string> { "-c:v", s.Id };
        a.AddRange(e.Preset switch { EncodeValues.Fast => s.Fast, EncodeValues.Best => s.BestPreset, _ => s.Balanced });
        var bitrate = e.RateMode == EncodeValues.Bitrate;
        if (bitrate && e.Kbps is not > 0)
        {
            throw new ArgumentException("Bitrate mode needs a rate.", nameof(e));
        }

        var knob = I(KnobValue(s, e.Preset, e.Quality));
        var k = bitrate ? I(e.Kbps!.Value) + "k" : "";
        if (s.Id.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            a.AddRange(bitrate ? ["-rc", "vbr", "-b:v", k] : ["-rc", "vbr", "-cq", knob, "-b:v", "0"]);
        }
        else if (s.Id.EndsWith("_amf", StringComparison.Ordinal))
        {
            a.AddRange(bitrate ? ["-rc", "vbr_peak", "-b:v", k] : ["-rc", "cqp", "-qp_i", knob, "-qp_p", knob]);
        }
        else if (s.Id.EndsWith("_qsv", StringComparison.Ordinal))
        {
            a.AddRange(bitrate ? ["-b:v", k] : ["-global_quality", knob]);
        }
        else if (s.Id.EndsWith("_mf", StringComparison.Ordinal))
        {
            a.AddRange(bitrate ? ["-rate_control", "pc_vbr", "-b:v", k] : ["-rate_control", "quality", "-quality", knob]);
        }
        else if (s.Id == "libsvtav1")
        {
            a.AddRange(bitrate ? ["-b:v", k] : ["-crf", knob]);
        }
        else if (s.Id is "libaom-av1" or "libvpx-vp9")
        {
            a.AddRange(bitrate ? ["-b:v", k] : ["-crf", knob, "-b:v", "0"]);
        }
        else if (s.Id == "librav1e")
        {
            a.AddRange(bitrate ? ["-b:v", k] : ["-qp", knob]);
        }

        if (s.Codec == EncodeValues.Hevc)
        {
            a.AddRange(["-tag:v", "hvc1"]); // Lets QuickTime and Windows name the stream.
        }

        return a;
    }

    /// <summary>The 0.1 second test encode of one encoder, into the null muxer (no file is written).</summary>
    public static IReadOnlyList<string> ProbeArgs(string encoderId) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=s=256x256:r=25:d=0.1",
        "-frames:v", "2", "-c:v", encoderId, "-pix_fmt", "yuv420p", "-f", "null", "-",
    ];

    /// <summary>The encoder names listed by <c>ffmpeg -encoders</c> (video lines look like <c> V....D name  description</c>).</summary>
    public static HashSet<string> ParseEncoderNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.Length < 8 || line[0] != 'V' || line[6] != ' ')
            {
                continue;
            }

            var rest = line[7..].TrimStart();
            var end = rest.IndexOf(' ', StringComparison.Ordinal);
            names.Add(end < 0 ? rest : rest[..end]);
        }

        return names;
    }
}
