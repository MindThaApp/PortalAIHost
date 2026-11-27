using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>What ffprobe reports about the first video stream of a file.</summary>
public sealed record FfmpegMediaInfo(int Width, int Height, string FrameRate, double DurationSeconds);

/// <summary>
/// Command lines of the host's ffmpeg jobs. Builders are pure: they take full paths and return the argument list
/// (never a shell string). Video is always H.264 from the Windows Media Foundation encoder (<c>h264_mf</c>, supplied
/// by the OS; the LGPL build has no libx264) and audio is AAC from ffmpeg's own encoder. Inputs are limited to the
/// file and pipe protocols, and <c>-n</c> makes ffmpeg refuse to overwrite an existing output.
/// </summary>
public static partial class FfmpegArgs
{
    public const string VideoEncoder = "h264_mf";
    public const string AudioEncoder = "aac";

    /// <summary>Shortest crossfade the join keeps; anything shorter is a cut.</summary>
    public const double MinCrossfadeSeconds = 0.05;

    private const int AudioRate = 48000;
    private const double FadeOutSeconds = 0.5;

    private static readonly string[] Quiet = ["-hide_banner", "-nostdin", "-loglevel", "info", "-stats"];

    [GeneratedRegex(@"^\d{1,6}(/\d{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RatePattern();

    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Full(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains("://", StringComparison.Ordinal) || path.StartsWith("pipe:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A media path must be a full local file path.", nameof(path));
        }

        return path;
    }

    private static void Input(List<string> a, string path)
    {
        a.AddRange(["-protocol_whitelist", "file,pipe", "-i", Full(path)]);
    }

    private static void VideoOut(List<string> a, MediaEncodeSettings? encode = null)
    {
        if (encode is null)
        {
            a.AddRange(["-c:v", VideoEncoder, "-rate_control", "quality", "-quality", "80", "-pix_fmt", "yuv420p"]);
            return;
        }

        a.AddRange(FfmpegEncoders.VideoArgs(encode));
        a.AddRange(["-pix_fmt", "yuv420p"]);
    }

    /// <summary>Probe: first video stream width, height and rate plus the container duration, as key=value lines.</summary>
    public static IReadOnlyList<string> Probe(string input) =>
    [
        "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,r_frame_rate", "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1", Full(input),
    ];

    /// <summary>Reads the key=value lines of <see cref="Probe"/>. Null when width, height or duration are missing.</summary>
    public static FfmpegMediaInfo? ParseProbe(string text)
    {
        int? width = null, height = null;
        string? rate = null;
        double? duration = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq];
            var value = line[(eq + 1)..];
            switch (key)
            {
                case "width" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var w):
                    width = w;
                    break;
                case "height" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var h):
                    height = h;
                    break;
                case "r_frame_rate" when RatePattern().IsMatch(value) && value != "0/0":
                    rate = value;
                    break;
                case "duration" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                    double.IsFinite(d) && d > 0:
                    duration = d;
                    break;
            }
        }

        return width is > 0 && height is > 0 && duration is > 0
            ? new FfmpegMediaInfo(width.Value, height.Value, rate ?? "16/1", duration.Value)
            : null;
    }

    /// <summary>AVI (motion JPEG) to MP4: even sides, 4:2:0, index at the front so a viewer can start at once. No sound is added.</summary>
    public static IReadOnlyList<string> ConvertToMp4(string input, string output, MediaEncodeSettings? encode = null)
    {
        var a = new List<string>(Quiet) { "-n" };
        Input(a, input);
        a.AddRange(["-an", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p"]);
        VideoOut(a, encode);
        a.AddRange(["-movflags", "+faststart", Full(output)]);
        return a;
    }

    /// <summary>The pictures of the last second as concatenated PNGs on stdout; <see cref="LastPngSink"/> keeps the last.</summary>
    public static IReadOnlyList<string> LastFrame(string input)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-sseof", "-1" };
        Input(a, input);
        a.AddRange(["-an", "-f", "image2pipe", "-c:v", "png", "pipe:1"]);
        return a;
    }

    /// <summary>
    /// Joins the clips. Every clip is fitted (letterboxed, never stretched) into the size and rate of the first one.
    /// A cut is a concat, a crossfade an <c>xfade</c> that overlaps the two clips by its length. Soundtracks are
    /// delayed to their start, scaled by their volume and mixed; with ducking the music sinks under the voice
    /// (sidechain compression); the sound is padded or cut to the video's length and fades out over its last
    /// half second. <paramref name="durations"/> are the clip lengths in seconds. <paramref name="totalSeconds"/> is
    /// the length of the result.
    /// </summary>
    public static IReadOnlyList<string> Join(MediaJoinPlan plan, FfmpegMediaInfo first, IReadOnlyList<double> durations,
        string output, out double totalSeconds)
    {
        var n = plan.Clips.Count;
        if (n < 1 || durations.Count != n || plan.Transitions.Count != n - 1)
        {
            throw new ArgumentException("The plan needs one transition between each two clips.", nameof(plan));
        }

        var width = first.Width / 2 * 2;
        var height = first.Height / 2 * 2;
        var rate = RatePattern().IsMatch(first.FrameRate) ? first.FrameRate : "16/1";
        var a = new List<string>(Quiet) { "-n" };
        foreach (var c in plan.Clips)
        {
            Input(a, c);
        }

        foreach (var t in plan.Audio)
        {
            Input(a, t.Path);
        }

        var f = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            f.Append(CultureInfo.InvariantCulture,
                $"[{i}:v]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={rate},format=yuv420p[v{i}];");
        }

        var acc = "v0";
        var length = durations[0];
        for (var i = 1; i < n; i++)
        {
            var t = plan.Transitions[i - 1];
            var seconds = Math.Min(t.Seconds, Math.Min(length, durations[i]) / 2);
            var next = i == n - 1 ? "vout" : $"x{i}";
            if (t.Kind == MediaTransitions.Crossfade && seconds >= MinCrossfadeSeconds)
            {
                f.Append(CultureInfo.InvariantCulture,
                    $"[{acc}][v{i}]xfade=transition=fade:duration={N(seconds)}:offset={N(length - seconds)}[{next}];");
                length += durations[i] - seconds;
            }
            else
            {
                f.Append(CultureInfo.InvariantCulture, $"[{acc}][v{i}]concat=n=2:v=1:a=0[{next}];");
                length += durations[i];
            }

            acc = next;
        }

        if (n == 1)
        {
            f.Append("[v0]null[vout];");
        }

        totalSeconds = length;
        var hasAudio = plan.Audio.Count > 0;
        if (hasAudio)
        {
            AppendAudio(f, plan, n, length);
        }

        a.AddRange(["-filter_complex", f.ToString().TrimEnd(';'), "-map", "[vout]"]);
        if (hasAudio)
        {
            a.AddRange(["-map", "[aout]"]);
        }

        VideoOut(a, plan.Encode);
        if (hasAudio)
        {
            a.AddRange(["-c:a", AudioEncoder, "-b:a", "160k"]);
        }

        a.AddRange(["-movflags", "+faststart", Full(output)]);
        return a;
    }

    private static void AppendAudio(StringBuilder f, MediaJoinPlan plan, int firstAudioInput, double length)
    {
        var voices = new List<string>();
        var music = new List<string>();
        for (var j = 0; j < plan.Audio.Count; j++)
        {
            var t = plan.Audio[j];
            var label = $"t{j}";
            var delay = (long)Math.Round(Math.Max(0, t.StartSeconds) * 1000);
            f.Append(CultureInfo.InvariantCulture,
                $"[{firstAudioInput + j}:a]aresample={AudioRate},aformat=sample_fmts=fltp:channel_layouts=stereo,volume={N(t.Volume)}");
            if (delay > 0)
            {
                f.Append(CultureInfo.InvariantCulture, $",adelay={delay}:all=1");
            }

            f.Append(CultureInfo.InvariantCulture, $"[{label}];");
            (t.Role == MediaAudioRoles.Voice ? voices : music).Add(label);
        }

        string mixed;
        if (plan.Duck && voices.Count > 0 && music.Count > 0)
        {
            var voice = Mix(f, voices, "voice");
            var bed = Mix(f, music, "music");
            f.Append(CultureInfo.InvariantCulture, $"[{voice}]asplit=2[vo][vsc];");
            f.Append(CultureInfo.InvariantCulture,
                $"[{bed}][vsc]sidechaincompress=threshold=0.05:ratio=8:attack=20:release=300[md];");
            f.Append("[vo][md]amix=inputs=2:duration=longest:normalize=0[amix];");
            mixed = "amix";
        }
        else
        {
            mixed = Mix(f, [.. voices, .. music], "amix");
        }

        var fade = Math.Min(FadeOutSeconds, length / 2);
        f.Append(CultureInfo.InvariantCulture,
            $"[{mixed}]apad,atrim=0:{N(length)},afade=t=out:st={N(Math.Max(0, length - fade))}:d={N(fade)}[aout];");
    }

    /// <summary>Mixes labels into one (or passes a single label through); returns the label of the result.</summary>
    private static string Mix(StringBuilder f, List<string> labels, string name)
    {
        if (labels.Count == 1 && name != "amix")
        {
            return labels[0];
        }

        foreach (var l in labels)
        {
            f.Append(CultureInfo.InvariantCulture, $"[{l}]");
        }

        f.Append(CultureInfo.InvariantCulture, $"amix=inputs={labels.Count}:duration=longest:normalize=0[{name}];");
        return name;
    }

    /// <summary>Any audio to a mono 16-bit WAV on stdout, from stdin (no file is read or written).</summary>
    public static IReadOnlyList<string> DecodeToWav(int sampleRate) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-i", "pipe:0",
        "-vn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
        "-c:a", "pcm_s16le", "-f", "wav", "pipe:1",
    ];
}
