using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

public sealed class FfmpegEncodeTests
{
    private const string In = @"C:\data\generated\videos\a.avi";
    private const string Out = @"C:\data\generated\.work\gen_0123456789abcdef\output.mp4";

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static MediaEncodeSettings Quality(string id, string codec, string preset, int? q = null) =>
        new(id, codec, preset, EncodeValues.Quality, q, null, null);

    [Fact]
    public void WithoutEncodeTheWindowsEncoderFlagsAreExactlyToday()
    {
        var a = FfmpegArgs.ConvertToMp4(In, Out);
        var i = a.ToList().IndexOf("-c:v");
        Assert.Equal(["-c:v", "h264_mf", "-rate_control", "quality", "-quality", "80", "-pix_fmt", "yuv420p"], a.Skip(i).Take(8));
    }

    [Theory]
    [InlineData("h264_nvenc", "fast", "-preset", "p2")]
    [InlineData("hevc_nvenc", "best", "-multipass", "fullres")]
    [InlineData("h264_amf", "fast", "-quality", "speed")]
    [InlineData("hevc_amf", "best", "-quality", "quality")]
    [InlineData("h264_qsv", "balanced", "-preset", "medium")]
    [InlineData("libsvtav1", "best", "-preset", "4")]
    [InlineData("libaom-av1", "fast", "-cpu-used", "8")]
    [InlineData("librav1e", "balanced", "-speed", "6")]
    public void PresetFlagsComeFromTheTable(string id, string preset, string flag, string value)
    {
        var spec = FfmpegEncoders.Find(id)!;
        var a = FfmpegEncoders.VideoArgs(Quality(id, spec.Codec, preset));
        Assert.Equal(id, After(a, "-c:v"));
        Assert.Equal(value, After(a, flag));
    }

    [Theory]
    [InlineData("h264_nvenc", "-cq", "23")]
    [InlineData("av1_nvenc", "-cq", "30")]
    [InlineData("h264_amf", "-qp_i", "24")]
    [InlineData("h264_qsv", "-global_quality", "24")]
    [InlineData("h264_mf", "-quality", "75")]
    [InlineData("libsvtav1", "-crf", "32")]
    [InlineData("libaom-av1", "-crf", "30")]
    [InlineData("librav1e", "-qp", "90")]
    public void OmittedQualityUsesThePresetValue(string id, string flag, string expected)
    {
        var a = FfmpegEncoders.VideoArgs(Quality(id, FfmpegEncoders.Find(id)!.Codec, "balanced"));
        Assert.Equal(expected, After(a, flag));
    }

    [Theory]
    [InlineData("h264_mf", 0, 0)]
    [InlineData("h264_mf", 100, 100)]
    [InlineData("h264_nvenc", 0, 40)]
    [InlineData("h264_nvenc", 100, 14)]
    [InlineData("libsvtav1", 100, 15)]
    public void QualityMapsLinearlyAndHigherIsBetter(string id, int quality, int knob)
    {
        Assert.Equal(knob, FfmpegEncoders.KnobValue(FfmpegEncoders.Find(id)!, "balanced", quality));
    }

    [Fact]
    public void BitrateModeSetsTheRateAndHevcGetsATag()
    {
        var a = FfmpegEncoders.VideoArgs(new("hevc_amf", EncodeValues.Hevc, "best", EncodeValues.Bitrate, null, 12000, null));
        Assert.Equal("vbr_peak", After(a, "-rc"));
        Assert.Equal("12000k", After(a, "-b:v"));
        Assert.Equal("hvc1", After(a, "-tag:v"));
        Assert.DoesNotContain("-qp_i", a);
        Assert.Equal("12000k", After(FfmpegEncoders.VideoArgs(new("libsvtav1", EncodeValues.Av1, "fast", EncodeValues.Bitrate, null, 12000, null)), "-b:v"));
        Assert.Equal("pc_vbr", After(FfmpegEncoders.VideoArgs(new("h264_mf", EncodeValues.H264, "fast", EncodeValues.Bitrate, null, 5000, null)), "-rate_control"));
        Assert.Throws<ArgumentException>(() => FfmpegEncoders.VideoArgs(new("h264_mf", EncodeValues.H264, "fast", EncodeValues.Bitrate, null, null, null)));
    }

    [Fact]
    public void ConvertAndJoinUseTheChosenEncoderAndStillNeverOverwrite()
    {
        var e = Quality("h264_amf", EncodeValues.H264, "best", 80);
        var a = FfmpegArgs.ConvertToMp4(In, Out, e);
        Assert.Equal("h264_amf", After(a, "-c:v"));
        Assert.Equal("yuv420p", After(a, "-pix_fmt"));
        Assert.Contains("-n", a);
        Assert.DoesNotContain("h264_mf", a);

        var plan = new MediaJoinPlan([In, In], [new MediaTransition(MediaTransitions.Cut, 0)], [], false, e);
        var j = FfmpegArgs.Join(plan, new FfmpegMediaInfo(640, 360, "16/1", 2), [2, 2], Out, out _);
        Assert.Equal("h264_amf", After(j, "-c:v"));
        Assert.Contains("-n", j);
    }

    [Fact]
    public void EveryCandidateHasAnArgumentBuilderAndAUniqueId()
    {
        Assert.Equal(FfmpegEncoders.Candidates.Count, FfmpegEncoders.Candidates.Select(c => c.Id).Distinct().Count());
        foreach (var c in FfmpegEncoders.Candidates)
        {
            var a = FfmpegEncoders.VideoArgs(Quality(c.Id, c.Codec, "balanced"));
            Assert.True(a.Count > 2, c.Id);
        }
    }

    [Fact]
    public void ProbeArgsWriteNothing()
    {
        var a = FfmpegEncoders.ProbeArgs("h264_amf");
        Assert.Equal("null", a[^2]);
        Assert.Equal("-", a[^1]);
        Assert.Contains("lavfi", a);
    }

    [Fact]
    public void EncoderListIsParsed()
    {
        const string text = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264
             V.S... h264_amf             AMD AMF H.264 Encoder (codec h264)
             A....D aac                  AAC
             V....D libsvtav1            SVT-AV1
            """;
        var names = FfmpegEncoders.ParseEncoderNames(text);
        Assert.Contains("h264_amf", names);
        Assert.Contains("libsvtav1", names);
        Assert.DoesNotContain("aac", names);
    }

    [Fact]
    public async Task TheProbeKeepsOnlyPresentEncodersThatPassTheTestEncode()
    {
        var calls = new List<string>();
        Task<(int, string)> Run(IReadOnlyList<string> args, CancellationToken ct)
        {
            if (args.Contains("-encoders"))
            {
                return Task.FromResult((0, " V....D h264_amf  a\n V....D av1_amf  b\n V....D libsvtav1  c\n V....D h264_mf  d\n"));
            }

            var enc = After(args, "-c:v");
            calls.Add(enc);
            return enc == "h264_mf" ? throw new EngineException(ErrorCodes.EngineError, "timeout") : Task.FromResult((enc == "av1_amf" ? 1 : 0, ""));
        }

        var list = await new EncoderProbe(Run).RunAsync(CancellationToken.None);
        Assert.Equal(["h264_amf", "libsvtav1"], list.Select(e => e.Id));
        Assert.Equal(["h264_amf", "av1_amf", "h264_mf", "libsvtav1"], calls);
        Assert.True(list[0].Hardware);
        Assert.Equal("AMD", list[0].Vendor);
        Assert.False(list[1].Hardware);
    }

    [Fact]
    public async Task AFailedEncoderListGivesAnEmptyResultAndCancellationPropagates()
    {
        Assert.Empty(await new EncoderProbe((_, _) => Task.FromResult((1, ""))).RunAsync(CancellationToken.None));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EncoderProbe((_, ct) => Task.FromCanceled<(int, string)>(new CancellationToken(true))).RunAsync(cts.Token));
    }
}
