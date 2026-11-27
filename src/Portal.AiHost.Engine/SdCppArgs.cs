using System.Globalization;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>
/// The stable-diffusion.cpp command-line flags, in one table, so a runtime update that renames a flag is a change here
/// and nowhere else. <see cref="Entry.Verified"/> is true for every flag checked against <c>sd-cli --help</c> of the
/// pinned release (engine/sdcpp.lock.json, master-1142-908965d). Check the table again whenever the pin changes.
/// </summary>
public static class SdCppFlags
{
    public sealed record Entry(string Option, string Flag, bool Verified);

    public const string Mode = "mode";
    public const string ModeImage = "mode.image";
    public const string ModeVideo = "mode.video";
    public const string Prompt = "prompt";
    public const string NegativePrompt = "negative_prompt";
    public const string Width = "width";
    public const string Height = "height";
    public const string Steps = "steps";
    public const string Seed = "seed";
    public const string CfgScale = "cfg_scale";
    public const string Sampler = "sampler";
    public const string FlowShift = "flow_shift";
    public const string Frames = "frames";
    public const string Fps = "fps";
    public const string InitImage = "init_image";
    public const string Strength = "strength";
    public const string Output = "output";
    public const string Threads = "threads";
    public const string OffloadToCpu = "offload_to_cpu";
    public const string Backend = "backend";
    public const string BackendAllCpu = "backend.cpu";
    public const string ClipOnCpu = "backend.te_cpu";
    public const string VaeOnCpu = "backend.vae_cpu";
    public const string MaxVram = "max_vram";
    public const string AutoFit = "auto_fit";
    public const string Off = "off";
    public const string VaeTiling = "vae_tiling";
    public const string FlashAttention = "flash_attn";
    public const string FlashAttentionDiffusion = "flash_attn.diffusion";
    public const string Preview = "preview";
    public const string PreviewMethod = "preview.method";
    public const string PreviewPath = "preview_path";

    /// <summary>Prefix of the per-file entries: "file." + a <see cref="GenerationFileRoles"/> value.</summary>
    public const string FilePrefix = "file.";

    public static IReadOnlyList<Entry> Table { get; } =
    [
        // Run mode.
        new(Mode, "-M", true),
        new(ModeImage, "img_gen", true),
        new(ModeVideo, "vid_gen", true),

        // Model files by role.
        new(FilePrefix + GenerationFileRoles.Model, "-m", true),
        new(FilePrefix + GenerationFileRoles.Diffusion, "--diffusion-model", true),
        new(FilePrefix + GenerationFileRoles.HighNoise, "--high-noise-diffusion-model", true),
        new(FilePrefix + GenerationFileRoles.ClipL, "--clip_l", true),
        new(FilePrefix + GenerationFileRoles.ClipG, "--clip_g", true),
        new(FilePrefix + GenerationFileRoles.T5xxl, "--t5xxl", true),
        new(FilePrefix + GenerationFileRoles.Vae, "--vae", true),
        new(FilePrefix + GenerationFileRoles.ClipVision, "--clip_vision", true),

        // Request values.
        new(Prompt, "-p", true),
        new(NegativePrompt, "-n", true),
        new(Width, "-W", true),
        new(Height, "-H", true),
        new(Steps, "--steps", true),
        new(Seed, "-s", true),
        new(CfgScale, "--cfg-scale", true),
        new(Sampler, "--sampling-method", true),
        new(FlowShift, "--flow-shift", true),
        new(Frames, "--video-frames", true),
        new(Fps, "--fps", true),
        new(InitImage, "-i", true),
        new(Strength, "--strength", true),
        new(Output, "-o", true),
        new(Threads, "-t", true),

        // Memory placement. --clip-on-cpu, --vae-on-cpu and --diffusion-fa are deprecated in 908965d: module
        // assignments through --backend (te=cpu, vae=cpu; one value, comma-separated) and --flash-attn replace them.
        // --max-vram caps the runtime's own automatic fitting (--auto-fit, on by default) at the host's plan; runs that fit the card turn it off.
        new(OffloadToCpu, "--offload-to-cpu", true),
        new(Backend, "--backend", true),
        new(BackendAllCpu, "cpu", true),
        new(ClipOnCpu, "te=cpu", true),
        new(VaeOnCpu, "vae=cpu", true),
        new(MaxVram, "--max-vram", true),
        new(AutoFit, "--auto-fit", true),
        new(Off, "off", true),
        new(VaeTiling, "--vae-tiling", true),
        new(FlashAttention, "--flash-attn", true),
        new(FlashAttentionDiffusion, "auto,diffusion=on", true),

        // Preview picture while sampling.
        new(Preview, "--preview", true),
        new(PreviewMethod, "proj", true),
        new(PreviewPath, "--preview-path", true),
    ];

    private static readonly Dictionary<string, string> ByOption = Table.ToDictionary(e => e.Option, e => e.Flag, StringComparer.Ordinal);

    /// <summary>The flag (or value word) for an option. Throws for an option the table does not have.</summary>
    public static string Of(string option) =>
        ByOption.TryGetValue(option, out var flag) ? flag : throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown sd.cpp option.");

    /// <summary>The flags this host is not sure of (listed in the docs and checked against the pinned release).</summary>
    public static IEnumerable<Entry> Unverified => Table.Where(e => !e.Verified);
}

/// <summary>Switches for the sd.cpp argument list.</summary>
public sealed record SdCppOptions
{
    /// <summary>Ask the engine for a preview picture while it samples (projection preview, cheap). Off by default.</summary>
    public bool Preview { get; init; }

    /// <summary>Extra flags appended last (local tuning). They may not repeat the output or model flags.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

/// <summary>Builds and checks the sd.cpp command line for one job.</summary>
public static class SdCppArgs
{
    /// <summary>Families the engine runs, per kind. LTX-Video and others are not supported by this runtime.</summary>
    public static bool Supports(string family, string kind) => kind switch
    {
        "image" => family is GenerationFamilies.Sd15 or GenerationFamilies.Sd2 or GenerationFamilies.Sdxl
            or GenerationFamilies.Sd3 or GenerationFamilies.Flux,
        "video" => family is GenerationFamilies.Wan,
        _ => false,
    };

    /// <summary>File extension of the output of a kind (sd.cpp writes an MJPEG .avi for video).</summary>
    public static string OutputExtension(string kind) => kind == "video" ? ".avi" : ".png";

    public static IReadOnlyList<string> Build(GenerationLaunch launch, SdCppOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        options ??= new SdCppOptions();
        var video = launch.Kind == "video";
        if (!Supports(launch.Family, launch.Kind))
        {
            throw new ArgumentException($"The generation engine does not run {launch.Family} models for {launch.Kind}.", nameof(launch));
        }

        if (string.IsNullOrEmpty(launch.OutputPath) || !Path.IsPathFullyQualified(launch.OutputPath))
        {
            throw new ArgumentException("A full output path is required.", nameof(launch));
        }

        var main = launch.Components.Count(c => c.Role is GenerationFileRoles.Model or GenerationFileRoles.Diffusion);
        if (main != 1)
        {
            throw new ArgumentException("Exactly one model or diffusion file is required.", nameof(launch));
        }

        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        static string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        var args = new List<string> { SdCppFlags.Of(SdCppFlags.Mode), SdCppFlags.Of(video ? SdCppFlags.ModeVideo : SdCppFlags.ModeImage) };

        // Model files in a fixed order: main file first, then the rest by role.
        foreach (var c in launch.Components.OrderBy(c => c.Role is GenerationFileRoles.Model or GenerationFileRoles.Diffusion ? 0 : 1)
            .ThenBy(c => c.Role, StringComparer.Ordinal))
        {
            if (!Path.IsPathFullyQualified(c.Path))
            {
                throw new ArgumentException("Model file paths must be full paths.", nameof(launch));
            }

            if (args.Contains(SdCppFlags.Of(SdCppFlags.FilePrefix + c.Role)))
            {
                throw new ArgumentException($"Two files have the role {c.Role}.", nameof(launch));
            }

            args.Add(SdCppFlags.Of(SdCppFlags.FilePrefix + c.Role));
            args.Add(c.Path);
        }

        args.Add(SdCppFlags.Of(SdCppFlags.Prompt));
        args.Add(launch.Prompt);
        if (!string.IsNullOrEmpty(launch.NegativePrompt))
        {
            args.Add(SdCppFlags.Of(SdCppFlags.NegativePrompt));
            args.Add(launch.NegativePrompt);
        }

        args.AddRange([SdCppFlags.Of(SdCppFlags.Width), I(launch.Width), SdCppFlags.Of(SdCppFlags.Height), I(launch.Height)]);
        args.AddRange([SdCppFlags.Of(SdCppFlags.Steps), I(launch.Steps)]);
        args.AddRange([SdCppFlags.Of(SdCppFlags.Seed), launch.Seed.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange([SdCppFlags.Of(SdCppFlags.CfgScale), D(launch.CfgScale)]);
        if (!string.IsNullOrEmpty(launch.Sampler))
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.Sampler), launch.Sampler]);
        }

        if (launch.FlowShift is { } shift)
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.FlowShift), D(shift)]);
        }

        if (video)
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.Frames), I(Math.Max(1, launch.Frames))]);
            args.AddRange([SdCppFlags.Of(SdCppFlags.Fps), I(Math.Max(1, launch.Fps))]);
        }

        if (launch.InitImagePath is { } init)
        {
            if (!Path.IsPathFullyQualified(init))
            {
                throw new ArgumentException("The start picture path must be a full path.", nameof(launch));
            }

            args.AddRange([SdCppFlags.Of(SdCppFlags.InitImage), init]);
            if (!video && launch.Strength is { } strength)
            {
                args.AddRange([SdCppFlags.Of(SdCppFlags.Strength), D(strength)]);
            }
        }

        args.AddRange([SdCppFlags.Of(SdCppFlags.Output), launch.OutputPath]);
        if (launch.Threads is > 0)
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.Threads), I(launch.Threads.Value)]);
        }

        var p = launch.Placement;
        var gpu = !string.Equals(p.Backend, "cpu", StringComparison.OrdinalIgnoreCase);
        if (!gpu)
        {
            // Processor only: works with the graphics-card build too (its ggml-cpu backends come with it).
            args.AddRange([SdCppFlags.Of(SdCppFlags.Backend), SdCppFlags.Of(SdCppFlags.BackendAllCpu)]);
        }
        else
        {
            if (p.OffloadToCpu)
            {
                args.Add(SdCppFlags.Of(SdCppFlags.OffloadToCpu));
            }

            var onCpu = new List<string>();
            if (p.ClipOnCpu)
            {
                onCpu.Add(SdCppFlags.Of(SdCppFlags.ClipOnCpu));
            }

            if (p.VaeOnCpu)
            {
                onCpu.Add(SdCppFlags.Of(SdCppFlags.VaeOnCpu));
            }

            if (onCpu.Count > 0)
            {
                args.AddRange([SdCppFlags.Of(SdCppFlags.Backend), string.Join(',', onCpu)]);
            }

            if (p.OffloadToCpu)
            {
                // Streaming run: the runtime's own fitting stays on, capped at the host's graphics-memory plan.
                // At least 0.1 GiB: 0 would mean "live free memory" to the runtime, not "none".
                if (p.MaxVramBytes is { } maxVram && maxVram >= (1L << 30) / 10)
                {
                    args.AddRange([SdCppFlags.Of(SdCppFlags.MaxVram), (maxVram / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture)]);
                }
            }
            else
            {
                // The plan says the weights fit the card: keep them there. The runtime's automatic fitting would
                // otherwise segment the graph and manage weight residency in RAM for no gain (and failed with a
                // zero-byte RAM weight capacity in a measured run).
                args.AddRange([SdCppFlags.Of(SdCppFlags.AutoFit), SdCppFlags.Of(SdCppFlags.Off)]);
            }
        }

        if (p.VaeTiling)
        {
            args.Add(SdCppFlags.Of(SdCppFlags.VaeTiling));
        }

        if (p.FlashAttention)
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.FlashAttention), SdCppFlags.Of(SdCppFlags.FlashAttentionDiffusion)]);
        }

        if (options.Preview && launch.PreviewPath is { } preview && Path.IsPathFullyQualified(preview))
        {
            args.AddRange([SdCppFlags.Of(SdCppFlags.Preview), SdCppFlags.Of(SdCppFlags.PreviewMethod), SdCppFlags.Of(SdCppFlags.PreviewPath), preview]);
        }

        var protectedFlags = new[] { SdCppFlags.Of(SdCppFlags.Output), SdCppFlags.Of(SdCppFlags.Mode), SdCppFlags.Of(SdCppFlags.Backend), SdCppFlags.Of(SdCppFlags.MaxVram), SdCppFlags.Of(SdCppFlags.AutoFit) }
            .Concat(SdCppFlags.Table.Where(e => e.Option.StartsWith(SdCppFlags.FilePrefix, StringComparison.Ordinal)).Select(e => e.Flag))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extra in options.ExtraArgs)
        {
            if (protectedFlags.Contains(extra) || extra.StartsWith("--output", StringComparison.Ordinal) || extra == "--mode")
            {
                throw new ArgumentException($"Extra argument {extra} may not change the output, mode or model files.", nameof(options));
            }

            args.Add(extra);
        }

        return args;
    }
}
