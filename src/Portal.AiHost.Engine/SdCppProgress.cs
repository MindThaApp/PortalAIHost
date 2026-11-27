using System.Globalization;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>
/// Reads stable-diffusion.cpp output lines and turns them into job progress. The runtime prints a progress bar
/// (<c>  |=====&gt;     | 3/20 - 1.25s/it</c>, redrawn with carriage returns) for sampling and for tiled decoding, plus
/// log lines such as <c>[INFO ] stable-diffusion.cpp:1234 - sampling completed, taking 12.3s</c>. Unknown lines are
/// ignored, so a changed log text only makes progress coarser, never wrong. Progress never goes backwards.
/// </summary>
public sealed partial class SdCppProgressParser
{
    // Share of the whole job each stage ends at.
    private const double LoadingEnd = 0.05;
    private const double EncodingEnd = 0.08;
    private const double SamplingEnd = 0.92;
    private const double DecodingEnd = 0.98;

    private string _stage = GenerationStages.Loading;
    private double _fraction;
    private int _step;
    private int _steps;

    [GeneratedRegex(@"(?<step>\d+)/(?<steps>\d+)\s*-\s*[\d.]+\s*(?:s/it|it/s)", RegexOptions.CultureInvariant)]
    private static partial Regex Bar();

    [GeneratedRegex(@"\[ERROR\]\s*(?:[^\s]+:\d+\s*-\s*)?(?<msg>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLine();

    /// <summary>The last error line the engine printed (without its source location), or null.</summary>
    public string? LastError { get; private set; }

    public string Stage => _stage;

    /// <summary>Reads one line. Returns the new progress when it changed, else null.</summary>
    public GenerationProgress? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (ErrorLine().Match(line) is { Success: true } err)
        {
            LastError = err.Groups["msg"].Value.Trim();
            return null;
        }

        var bar = Bar().Match(line);
        if (bar.Success &&
            int.TryParse(bar.Groups["step"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var step) &&
            int.TryParse(bar.Groups["steps"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var steps) &&
            steps > 0)
        {
            step = Math.Clamp(step, 0, steps);
            if (_stage is GenerationStages.Loading or GenerationStages.Encoding)
            {
                _stage = GenerationStages.Sampling;
            }

            var (start, end) = Range(_stage);
            _step = step;
            _steps = steps;
            return Move(start + (end - start) * step / steps);
        }

        var lower = line.ToLowerInvariant();
        string? next = null;
        if (lower.Contains("save result", StringComparison.Ordinal) || lower.Contains("saving", StringComparison.Ordinal))
        {
            next = GenerationStages.Saving;
        }
        else if (lower.Contains("sampling completed", StringComparison.Ordinal) || lower.Contains("decod", StringComparison.Ordinal))
        {
            next = GenerationStages.Decoding;
        }
        else if (lower.Contains("get_learned_condition", StringComparison.Ordinal) || lower.Contains("text encod", StringComparison.Ordinal))
        {
            next = _stage == GenerationStages.Loading ? GenerationStages.Encoding : null;
        }
        else if (lower.Contains("sampling using", StringComparison.Ordinal) || lower.Contains("generating image", StringComparison.Ordinal)
                 || lower.Contains("generating video", StringComparison.Ordinal))
        {
            next = _stage is GenerationStages.Loading or GenerationStages.Encoding ? GenerationStages.Sampling : null;
        }

        // Decode and save only count after sampling started: load lines can name decoder tensors.
        if (next is null || Order(next) <= Order(_stage) ||
            (Order(next) > Order(GenerationStages.Sampling) && Order(_stage) < Order(GenerationStages.Sampling)))
        {
            return null;
        }

        _stage = next;
        _step = 0;
        _steps = 0;
        return Move(Range(next).Start);
    }

    private GenerationProgress? Move(double fraction)
    {
        var f = Math.Clamp(Math.Max(_fraction, fraction), 0, 1);
        _fraction = f;
        return new GenerationProgress(_stage, _step, _steps, f);
    }

    private static int Order(string stage) => stage switch
    {
        GenerationStages.Loading => 0,
        GenerationStages.Encoding => 1,
        GenerationStages.Sampling => 2,
        GenerationStages.Decoding => 3,
        GenerationStages.Saving => 4,
        _ => -1,
    };

    private static (double Start, double End) Range(string stage) => stage switch
    {
        GenerationStages.Loading => (0, LoadingEnd),
        GenerationStages.Encoding => (LoadingEnd, EncodingEnd),
        GenerationStages.Sampling => (EncodingEnd, SamplingEnd),
        GenerationStages.Decoding => (SamplingEnd, DecodingEnd),
        _ => (DecodingEnd, 1),
    };
}
