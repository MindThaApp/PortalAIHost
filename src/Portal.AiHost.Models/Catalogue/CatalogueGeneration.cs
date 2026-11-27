namespace Portal.AiHost.Core.Catalogue;

// Optional generation defaults for image and video models (engine stable-diffusion-cpp). Older hosts ignore the
// block; every field may be missing, and the engine then uses its per-family defaults. The file roles
// (CatalogueFile.Role), availability and graphics-memory needs (MinVramGb) are the entry's own catalogue fields.

public sealed partial class CatalogueEntry
{
    /// <summary>Image and video models only: defaults and limits for the generation engine. Null for other kinds.</summary>
    public CatalogueGeneration? Generation { get; init; }
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
