using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Generation;

namespace Portal.AiHost.Core.Tests.Api;

public sealed class MediaEncodeResolverTests
{
    private static MediaEncoder E(string id, string codec, bool hw, string vendor) =>
        new() { Id = id, Codec = codec, Hardware = hw, Vendor = vendor, Modes = ["quality", "bitrate"] };

    private static readonly MediaEncoder[] Amd =
    [
        E("h264_amf", "h264", true, "AMD"), E("hevc_amf", "hevc", true, "AMD"),
        E("h264_mf", "h264", false, "Windows"), E("libsvtav1", "av1", false, "software"),
    ];

    [Fact]
    public void NoEncodeObjectMeansNoChange() => Assert.Null(MediaEncodeResolver.Resolve(null, Amd));

    [Fact]
    public void AutoPrefersTheCardThenWindowsThenSoftware()
    {
        var auto = MediaEncodeResolver.AutoChoices([E("h264_mf", "h264", false, "Windows"), E("h264_nvenc", "h264", true, "NVIDIA"),
            E("libsvtav1", "av1", false, "software"), E("librav1e", "av1", false, "software")]);
        Assert.Equal("h264_nvenc", auto["h264"]);
        Assert.Equal("libsvtav1", auto["av1"]);
        Assert.False(auto.ContainsKey("hevc"));
        Assert.Equal("h264_mf", MediaEncodeResolver.AutoChoices([E("h264_mf", "h264", false, "Windows")])["h264"]);
    }

    [Fact]
    public void DefaultsAreH264BalancedQualityWithThePresetValue()
    {
        var r = MediaEncodeResolver.Resolve(new EncodeRequest(), Amd)!;
        Assert.Equal(("h264_amf", "h264", "balanced", "quality", (int?)null), (r.EncoderId, r.Codec, r.Preset, r.RateMode, r.Quality));
    }

    [Fact]
    public void HevcAndBitrateResolve()
    {
        var r = MediaEncodeResolver.Resolve(new EncodeRequest { Codec = "hevc", Preset = "best", Mode = "bitrate", BitrateKbps = 8000 }, Amd)!;
        Assert.Equal("hevc_amf", r.EncoderId);
        Assert.Equal(8000, r.Kbps);
    }

    [Theory]
    [InlineData("codec", "vp9", ErrorCodes.UnsupportedParameter, "encode.codec")]
    [InlineData("codec", "mpeg2", ErrorCodes.InvalidRequest, "encode.codec")]
    [InlineData("preset", "slow", ErrorCodes.InvalidRequest, "encode.preset")]
    [InlineData("mode", "crf", ErrorCodes.InvalidRequest, "encode.mode")]
    [InlineData("quality", "101", ErrorCodes.InvalidRequest, "encode.quality")]
    [InlineData("quality", "-1", ErrorCodes.InvalidRequest, "encode.quality")]
    [InlineData("bitrate", "50", ErrorCodes.InvalidRequest, "encode.bitrate_kbps")]
    [InlineData("bitrate-no-mode", "5000", ErrorCodes.InvalidRequest, "encode.bitrate_kbps")]
    [InlineData("bitrate-missing", "0", ErrorCodes.InvalidRequest, "encode.bitrate_kbps")]
    [InlineData("quality-in-bitrate", "50", ErrorCodes.InvalidRequest, "encode.quality")]
    public void BadFieldsAreRefusedWithTheFieldName(string field, string value, string code, string param)
    {
        var e = new EncodeRequest();
        switch (field)
        {
            case "codec": e.Codec = value; break;
            case "preset": e.Preset = value; break;
            case "mode": e.Mode = value; break;
            case "quality": e.Quality = int.Parse(value); break;
            case "bitrate": e.Mode = "bitrate"; e.BitrateKbps = int.Parse(value); break;
            case "bitrate-no-mode": e.BitrateKbps = int.Parse(value); break;
            case "bitrate-missing": e.Mode = "bitrate"; break;
            case "quality-in-bitrate": e.Mode = "bitrate"; e.BitrateKbps = 5000; e.Quality = int.Parse(value); break;
        }

        var ex = Assert.Throws<ApiException>(() => MediaEncodeResolver.Resolve(e, Amd));
        Assert.Equal(code, ex.Code);
        Assert.Equal(param, ex.Param);
    }

    [Fact]
    public void ACodecThisPcCannotEncodeIsRefused()
    {
        var ex = Assert.Throws<ApiException>(() => MediaEncodeResolver.Resolve(new EncodeRequest { Codec = "av1" }, [E("h264_mf", "h264", false, "Windows")]));
        Assert.Equal(ErrorCodes.UnsupportedParameter, ex.Code);
        Assert.Equal("encode.codec", ex.Param);
    }
}
