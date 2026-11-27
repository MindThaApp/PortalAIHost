namespace Portal.AiHost.Core.Catalogue;

// Optional catalogue fields for image and video models (generation engine). Older hosts ignore them; entries of
// other kinds leave them out. Every field may be missing: the engine then uses its per-family defaults.

public sealed partial class CatalogueEntry
{
    /// <summary>Image and video models only: defaults and limits for the generation engine. Null for other kinds.</summary>
    public CatalogueGeneration? Generation { get; init; }
}

public sealed partial class CatalogueFile
{
    /// <summary>
    /// Generation models: which part of the model this file is (see <c>GenerationFileRoles</c>: model, diffusion,
    /// high_noise, clip_l, clip_g, t5xxl, vae, clip_vision). Null: guessed from the file name.
    /// </summary>
    public string? Role { get; init; }
}

/// <summary>Generation defaults and limits of one catalogue entry.</summary>
public sealed class CatalogueGeneration
{
    public int? DefaultWidth { get; init; }
    public int? DefaultHeight { get; init; }
    public int? DefaultSteps { get; init; }
    public double? DefaultCfgScale { get; init; }
    public string? Sampler { get; init; }

    /// <summary>Flow-matching shift (Wan and other flow models).</summary>
    public double? FlowShift { get; init; }

    /// <summary>Video: frame count used when a request gives none, and the most accepted.</summary>
    public int? DefaultFrames { get; init; }
    public int? MaxFrames { get; init; }
    public int? Fps { get; init; }

    /// <summary>Video: the model can start from a picture.</summary>
    public bool ImageToVideo { get; init; }

    /// <summary>Largest side accepted, in pixels.</summary>
    public int? MaxSide { get; init; }

    /// <summary>Compute buffers at the default size, in bytes. Null: estimated from the size.</summary>
    public long? ComputeBytes { get; init; }
}
