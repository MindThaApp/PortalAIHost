namespace Portal.AiHost.Contracts;

/// <summary>Where an upscale takes its picture from: exactly one of these.</summary>
public sealed class UpscaleSource
{
    /// <summary>The id of an image upload of this app.</summary>
    public string? Upload { get; set; }

    /// <summary>The id of a finished image job of this app.</summary>
    public string? Job { get; set; }
}

/// <summary>POST /host/image/upscales and POST /host/image/upscales/estimate.</summary>
public sealed class UpscaleRequest
{
    public UpscaleSource? Source { get; set; }

    /// <summary><c>fast</c> or <c>balanced</c> (default); <c>best</c> only while the Best add-on is installed.</summary>
    public string? Tier { get; set; }

    /// <summary>2 or 4. Default 2.</summary>
    public int? Scale { get; set; }

    /// <summary><c>auto</c> (default), <c>gpu</c> or <c>cpu</c>.</summary>
    public string? Device { get; set; }
}

/// <summary>Estimate for an upscale: seconds and a warning level (<see cref="UpscaleWarnings"/>).</summary>
public sealed class UpscaleEstimate
{
    public string Tier { get; init; } = "";
    public string Device { get; init; } = "";
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public double SecondsPerImage { get; init; }
    public string Warning { get; init; } = UpscaleWarnings.None;
}

/// <summary>Values of the estimate <c>warning</c> field (seconds per image: above 10 warns, above 100 warns strongly).</summary>
public static class UpscaleWarnings
{
    public const string None = "none";
    public const string Slow = "slow";
    public const string VerySlow = "very_slow";
    public const double SlowSeconds = 10;
    public const double VerySlowSeconds = 100;

    public static string For(double secondsPerImage) =>
        secondsPerImage > VerySlowSeconds ? VerySlow : secondsPerImage > SlowSeconds ? Slow : None;
}

/// <summary>The <c>upscale</c> capability flag.</summary>
public sealed class UpscaleAvailability
{
    public bool Image { get; init; }

    /// <summary><c>ready</c>, <c>no_model</c> or <c>no_engine</c>.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Per tier: <c>ready</c>, <c>no_model</c>, <c>no_engine</c> or <c>needs_download</c>.</summary>
    public Dictionary<string, string> Tiers { get; init; } = [];
}
