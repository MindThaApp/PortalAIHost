using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Generation;

/// <summary>
/// Turns the <c>encode</c> object of a request into the encoder settings ffmpeg runs with: checks every field
/// (<see cref="Validate"/>, no ffmpeg needed), then picks the encoder from the probed list (<see cref="Resolve"/>).
/// Pure: no process, no file.
/// </summary>
public static class MediaEncodeResolver
{
    /// <summary>Checks the shape of an <c>encode</c> object. Throws 400 <see cref="ApiException"/> naming the field.</summary>
    public static void Validate(EncodeRequest? e)
    {
        if (e is null)
        {
            return;
        }

        if (e.Codec is not null && e.Codec is not (EncodeValues.H264 or EncodeValues.Hevc or EncodeValues.Av1 or EncodeValues.Vp9 or EncodeValues.Auto))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.codec must be h264, hevc, av1, vp9 or auto.", "encode.codec");
        }

        if (e.Codec == EncodeValues.Vp9)
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "vp9 is a WebM codec; MP4 output takes h264, hevc or av1.", "encode.codec");
        }

        if (e.Preset is not null && e.Preset is not (EncodeValues.Fast or EncodeValues.Balanced or EncodeValues.Best))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.preset must be fast, balanced or best.", "encode.preset");
        }

        if (e.Mode is not null && e.Mode is not (EncodeValues.Quality or EncodeValues.Bitrate))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.mode must be quality or bitrate.", "encode.mode");
        }

        var bitrate = e.Mode == EncodeValues.Bitrate;
        if (e.Quality is { } q && (q < 0 || q > 100))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.quality must be 0 to 100.", "encode.quality");
        }

        if (e.Quality is not null && bitrate)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.quality is for mode quality.", "encode.quality");
        }

        if (bitrate && e.BitrateKbps is null)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.bitrate_kbps is required in mode bitrate.", "encode.bitrate_kbps");
        }

        if (e.BitrateKbps is { } k && (k < 100 || k > EncodeValues.MaxKbps))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"encode.bitrate_kbps must be 100 to {EncodeValues.MaxKbps}.", "encode.bitrate_kbps");
        }

        if (e.BitrateKbps is not null && !bitrate)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "encode.bitrate_kbps is for mode bitrate.", "encode.bitrate_kbps");
        }
    }

    /// <summary>
    /// The encoder "auto" uses per codec from the probed list: the hardware encoder first (list order is the table's
    /// order), then the Windows one, then software.
    /// </summary>
    public static Dictionary<string, string> AutoChoices(IReadOnlyList<MediaEncoder> probed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var codec in new[] { EncodeValues.H264, EncodeValues.Hevc, EncodeValues.Av1, EncodeValues.Vp9 })
        {
            var pick = probed.Where(p => p.Codec == codec).OrderBy(Rank).FirstOrDefault();
            if (pick is not null)
            {
                result[codec] = pick.Id;
            }
        }

        return result;
    }

    private static int Rank(MediaEncoder e) => e.Hardware ? 0 : e.Vendor == "Windows" ? 1 : 2;

    /// <summary>
    /// Validates and resolves. Null in, null out (today's encoder). A codec no probed encoder can produce is
    /// <c>400 unsupported_parameter</c>.
    /// </summary>
    public static MediaEncodeSettings? Resolve(EncodeRequest? e, IReadOnlyList<MediaEncoder> probed)
    {
        if (e is null)
        {
            return null;
        }

        Validate(e);
        var codec = e.Codec is null or EncodeValues.Auto ? EncodeValues.H264 : e.Codec;
        if (!AutoChoices(probed).TryGetValue(codec, out var encoder))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter,
                $"This PC cannot encode {codec}. GET /host/media/encoders lists what it can.", "encode.codec");
        }

        var bitrate = e.Mode == EncodeValues.Bitrate;
        return new MediaEncodeSettings(encoder, codec, e.Preset ?? EncodeValues.Balanced,
            bitrate ? EncodeValues.Bitrate : EncodeValues.Quality, bitrate ? null : e.Quality, bitrate ? e.BitrateKbps : null, null);
    }
}
