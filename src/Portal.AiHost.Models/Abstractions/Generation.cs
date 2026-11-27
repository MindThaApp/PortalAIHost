namespace Portal.AiHost.Core.Abstractions;

/// <summary>Model families the generation engine knows. Open strings; see <see cref="Normalize"/>.</summary>
public static class GenerationFamilies
{
    public const string Sd15 = "sd1.5";
    public const string Sd2 = "sd2";
    public const string Sdxl = "sdxl";
    public const string Sd3 = "sd3";
    public const string Flux = "flux";
    public const string Wan = "wan";

    /// <summary>
    /// Maps the catalogue's family text ("SD 1.5", "stable-diffusion-xl", "wan2.1") to a known family, or null. A
    /// generic family ("stable-diffusion") is narrowed by the model id or name (<paramref name="hint"/>): "xl" is SDXL,
    /// "3" SD3, "2" SD 2, anything else SD 1.5.
    /// </summary>
    public static string? Normalize(string? family, string? hint = null)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        var f = family.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("_", "-", StringComparison.Ordinal);
        if (f is "stable-diffusion" or "sd")
        {
            var h = (hint ?? "").ToLowerInvariant();
            return h.Contains("xl", StringComparison.Ordinal) ? Sdxl
                : h.Contains("sd3", StringComparison.Ordinal) || h.Contains("-3", StringComparison.Ordinal) ? Sd3
                : h.Contains("sd2", StringComparison.Ordinal) || h.Contains("-2", StringComparison.Ordinal) ? Sd2
                : Sd15;
        }

        if (f.StartsWith("stable-diffusion-", StringComparison.Ordinal))
        {
            f = "sd" + f["stable-diffusion-".Length..];
        }

        return f switch
        {
            "sd1.5" or "sd15" or "sd-1.5" or "sd1" or "sd1.x" => Sd15,
            "sd2" or "sd2.1" or "sd21" or "sd-2.1" => Sd2,
            "sdxl" or "sd-xl" or "sdxl-turbo" or "sdxl1.0" => Sdxl,
            "sd3" or "sd3.5" or "sd35" => Sd3,
            _ when f.StartsWith("flux", StringComparison.Ordinal) => Flux,
            _ when f.StartsWith("wan", StringComparison.Ordinal) => Wan,
            _ => null,
        };
    }
}

/// <summary>Which part of a generation model a file is. Open strings, from the catalogue file <c>role</c>.</summary>
public static class GenerationFileRoles
{
    /// <summary>A complete checkpoint (diffusion model, text encoders and VAE in one file; SD 1.5 and SDXL).</summary>
    public const string Model = "model";

    /// <summary>The diffusion model alone (FLUX, Wan, SD3).</summary>
    public const string Diffusion = "diffusion";

    /// <summary>Wan 2.2 A14B: the high-noise expert, used before <see cref="Diffusion"/>.</summary>
    public const string HighNoise = "high_noise";

    public const string ClipL = "clip_l";
    public const string ClipG = "clip_g";

    /// <summary>T5-XXL (FLUX, SD3) or UMT5-XXL (Wan).</summary>
    public const string T5xxl = "t5xxl";

    public const string Vae = "vae";

    /// <summary>Wan 2.1 image to video: the picture encoder.</summary>
    public const string ClipVision = "clip_vision";

    /// <summary>
    /// The engine role of a file from its catalogue role (checkpoint, diffusion_model, vae, text_encoder; this host's own
    /// role words are accepted too). A text_encoder is narrowed by its file name (clip_l, clip_g, clip_vision, else
    /// T5/UMT5). No role: guessed from the name.
    /// </summary>
    public static string FromCatalogue(string? role, string fileName, int fileCount)
    {
        var r = role?.Trim().ToLowerInvariant();
        switch (r)
        {
            case null or "":
                return Guess(fileName, fileCount);
            case "checkpoint":
                return Model;
            case "diffusion_model":
                var n = fileName.ToLowerInvariant();
                return n.Contains("high_noise", StringComparison.Ordinal) || n.Contains("high-noise", StringComparison.Ordinal) ? HighNoise : Diffusion;
            case "text_encoder":
                var guess = Guess(fileName, 2);
                return IsEncoder(guess) ? guess : T5xxl;
            case Model or Diffusion or HighNoise or ClipL or ClipG or T5xxl or Vae or ClipVision:
                return r;
            default:
                return Guess(fileName, fileCount);
        }
    }

    /// <summary>True for the text and picture encoders (they can run on the processor with clip-on-cpu).</summary>
    public static bool IsEncoder(string role) => role is ClipL or ClipG or T5xxl or ClipVision;

    /// <summary>Guesses the role from a file name when the catalogue gives none.</summary>
    public static string Guess(string fileName, int fileCount)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("clip_vision", StringComparison.Ordinal) || n.Contains("clip-vision", StringComparison.Ordinal))
        {
            return ClipVision;
        }

        if (n.Contains("clip_l", StringComparison.Ordinal) || n.Contains("clip-l", StringComparison.Ordinal))
        {
            return ClipL;
        }

        if (n.Contains("clip_g", StringComparison.Ordinal) || n.Contains("clip-g", StringComparison.Ordinal))
        {
            return ClipG;
        }

        if (n.Contains("t5", StringComparison.Ordinal) || n.Contains("umt5", StringComparison.Ordinal))
        {
            return T5xxl;
        }

        if (n.Contains("vae", StringComparison.Ordinal) || n.Contains("ae.safetensors", StringComparison.Ordinal))
        {
            return Vae;
        }

        if (n.Contains("high_noise", StringComparison.Ordinal) || n.Contains("high-noise", StringComparison.Ordinal))
        {
            return HighNoise;
        }

        return fileCount == 1 ? Model : Diffusion;
    }
}

/// <summary>One file of a generation model on disk.</summary>
public sealed record GenerationComponent(string Role, string Path, long Bytes);

/// <summary>Where the parts of a generation run go. Produced by the generation planner, read by the engine adapter.</summary>
public sealed record GenerationPlacement
{
    /// <summary>vulkan or cpu.</summary>
    public string Backend { get; init; } = "vulkan";

    /// <summary>Weights stay in RAM and move to the graphics card when needed.</summary>
    public bool OffloadToCpu { get; init; }

    /// <summary>Text and picture encoders run on the processor.</summary>
    public bool ClipOnCpu { get; init; }

    /// <summary>The VAE (decode to pixels) runs on the processor.</summary>
    public bool VaeOnCpu { get; init; }

    /// <summary>Decode in tiles to keep the decode buffers small.</summary>
    public bool VaeTiling { get; init; }

    /// <summary>Flash attention in the diffusion model (less memory for video).</summary>
    public bool FlashAttention { get; init; }
}

/// <summary>Everything one generation run needs. The engine writes only <see cref="OutputPath"/> and <see cref="PreviewPath"/>.</summary>
public sealed record GenerationLaunch
{
    /// <summary>image or video.</summary>
    public string Kind { get; init; } = "image";

    /// <summary>See <see cref="GenerationFamilies"/>.</summary>
    public string Family { get; init; } = GenerationFamilies.Sd15;

    public IReadOnlyList<GenerationComponent> Components { get; init; } = Array.Empty<GenerationComponent>();

    public string Prompt { get; init; } = "";
    public string? NegativePrompt { get; init; }
    public int Width { get; init; } = 512;
    public int Height { get; init; } = 512;
    public int Steps { get; init; } = 20;
    public long Seed { get; init; }
    public double CfgScale { get; init; } = 7;
    public string? Sampler { get; init; }
    public double? FlowShift { get; init; }
    public int Frames { get; init; } = 1;
    public int Fps { get; init; } = 16;

    /// <summary>A start picture the host wrote into the job's own work folder, or null.</summary>
    public string? InitImagePath { get; init; }

    public double? Strength { get; init; }

    /// <summary>The job's partial output file, inside the host's work folder. Must not exist before the run.</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>Optional preview picture path in the same work folder, or null for no preview.</summary>
    public string? PreviewPath { get; init; }

    public GenerationPlacement Placement { get; init; } = new();

    /// <summary>Processor threads, or null for the engine's own choice.</summary>
    public int? Threads { get; init; }

    /// <summary>Cap on the memory the engine process may commit (Windows job object), or null for none.</summary>
    public long? MemoryLimitBytes { get; init; }
}

/// <summary>Progress of one run. <see cref="Fraction"/> runs from 0 to 1 over the whole run.</summary>
public sealed record GenerationProgress(string Stage, int Step, int Steps, double Fraction);

/// <summary>Whether the generation engine can run on this installation.</summary>
public sealed record GenerationEngineCheck(bool Present, bool GpuBuild, string? Reason);

/// <summary>
/// A generation engine: one supervised child process per job (see the adapter for why). Implementations kill the
/// process (and everything it started) on cancellation and never write anywhere but the launch's output and
/// preview paths.
/// </summary>
public interface IGenerationEngine
{
    /// <summary>Whether the engine binary is installed, and whether a graphics-card build is among it.</summary>
    GenerationEngineCheck Check();

    /// <summary>True when the engine can run models of this family (see <see cref="GenerationFamilies"/>).</summary>
    bool SupportsFamily(string family, string kind);

    /// <summary>
    /// Runs one job. Completes when the output file is written. Throws <see cref="EngineException"/> on failure and
    /// <see cref="OperationCanceledException"/> after a cancellation, once the process has ended.
    /// </summary>
    Task RunAsync(GenerationLaunch launch, IProgress<GenerationProgress>? progress, CancellationToken ct);
}
