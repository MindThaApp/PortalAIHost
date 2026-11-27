using Portal.AiHost.Contracts;

namespace Portal.AiHost.Engine;

/// <summary>
/// Finds the encoders that really work on this PC: reads <c>ffmpeg -encoders</c>, then test-encodes 0.1 s of a
/// generated picture per candidate that is present. The test output goes to the null muxer, so no file is written.
/// The process runner is a delegate so tests can fake it.
/// </summary>
public sealed class EncoderProbe(Func<IReadOnlyList<string>, CancellationToken, Task<(int Code, string Output)>> run, Action<string>? log = null)
{
    public async Task<IReadOnlyList<MediaEncoder>> RunAsync(CancellationToken ct)
    {
        var (code, text) = await Safe(["-hide_banner", "-encoders"], ct).ConfigureAwait(false);
        if (code != 0)
        {
            return [];
        }

        var present = FfmpegEncoders.ParseEncoderNames(text);
        var passed = new List<MediaEncoder>();
        foreach (var spec in FfmpegEncoders.Candidates)
        {
            if (!present.Contains(spec.Id))
            {
                continue;
            }

            var (c, _) = await Safe(FfmpegEncoders.ProbeArgs(spec.Id), ct).ConfigureAwait(false);
            if (c == 0)
            {
                passed.Add(new MediaEncoder
                {
                    Id = spec.Id,
                    Codec = spec.Codec,
                    Hardware = spec.Hardware,
                    Vendor = spec.Vendor,
                    Modes = [EncodeValues.Quality, EncodeValues.Bitrate],
                });
            }
            else
            {
                log?.Invoke($"Encoder {spec.Id} did not pass the test encode (exit code {c}).");
            }
        }

        return passed;
    }

    private async Task<(int Code, string Output)> Safe(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return await run(args, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
        {
            return (-1, "");
        }
    }
}
