using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Catalogue;

/// <summary>
/// The signed model catalogue (catalogue/catalogue.json). Data-driven: tiers are not hard-coded,
/// the recommender simply picks the best entry that fits the hardware.
/// </summary>
public sealed class ModelCatalogue
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Monotonic. A copy with a lower version than the last accepted one is a rollback and is ignored.</summary>
    public long CatalogueVersion { get; init; }

    public string? Published { get; init; }

    public List<CatalogueEntry> Models { get; set; } = new();
}

public sealed partial class CatalogueEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Family { get; set; } = "";
    public string Maker { get; set; } = "";

    /// <summary>Engine kind, see <see cref="EngineKinds"/>. Unknown engines are ignored by the host.</summary>
    public string Engine { get; set; } = EngineKinds.LlamaServer;

    /// <summary>Model kind, see <see cref="ModelKinds"/> (chat, embedding, transcription). Open set.</summary>
    public string Kind { get; set; } = ModelKinds.Chat;

    /// <summary>
    /// Optional use-case category (see <see cref="ModelCategories"/>). Null or unknown: derived from <see cref="Kind"/>.
    /// Older hosts ignore the field.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// False for an entry whose engine this host does not have yet ("coming soon"): it is listed but never downloaded.
    /// Defaults to true when the field is missing.
    /// </summary>
    public bool Available { get; set; } = true; // set, not init: generated readers would reset an init default

    /// <summary>Minimum graphics memory in GB with CPU offload, for models that need a graphics card (video). Null when not given.</summary>
    public double? MinVramGb { get; init; }

    /// <summary>Recommended graphics memory in GB, or null.</summary>
    public double? RecommendedVramGb { get; init; }

    /// <summary>The category shown to people: <see cref="Category"/> when known, otherwise from <see cref="Kind"/>.</summary>
    [JsonIgnore]
    public string EffectiveCategory => ModelCategories.Resolve(Category, Kind);

    public CatalogueLicence Licence { get; set; } = new();

    /// <summary>Comparable quality score (higher is better); includes the quantisation penalty.</summary>
    public double Quality { get; init; }

    public double ParamsTotalB { get; init; }
    public double ParamsActiveB { get; init; }
    public string Quant { get; set; } = "";

    /// <summary>Hugging Face repository, for example "Qwen/Qwen3-8B-GGUF".</summary>
    public string HfRepo { get; set; } = "";

    /// <summary>Pinned 40-hex commit of <see cref="HfRepo"/>. Every URL must resolve against this commit.</summary>
    public string HfCommit { get; set; } = "";

    /// <summary>One file, or the parts of a split GGUF in load order (part 1 first).</summary>
    public List<CatalogueFile> Files { get; set; } = new();

    public ModelMemory Memory { get; set; } = new();
    public int ContextMax { get; init; }
    public int ContextDefault { get; init; }
    public ModelFeatures Features { get; set; } = new();
    public string MinHostVersion { get; set; } = "1.0.0";
    public RecommendedSampling? RecommendedSampling { get; init; }

    /// <summary>
    /// Optional plain-language guidance for people choosing a model (null when the catalogue has none). It is part
    /// of the signed catalogue, so changing it needs the catalogue to be signed again.
    /// </summary>
    public CatalogueAdvice? Advice { get; init; }

    /// <summary>Free text for maintainers (sources, which numbers are estimates).</summary>
    public string? Notes { get; init; }

    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Mixture-of-experts: expert tensors can be kept on the CPU with --n-cpu-moe.</summary>
    [JsonIgnore]
    public bool IsMoe => Memory.ExpertBytes > 0 && ParamsActiveB > 0 && ParamsActiveB < ParamsTotalB;

    /// <summary>
    /// True only when the commit is pinned and every part has a real SHA-256 and size. Placeholder entries
    /// stay visible in the catalogue but the downloader refuses them.
    /// </summary>
    [JsonIgnore]
    public bool IsDownloadable =>
        Available && CommitPattern().IsMatch(HfCommit) && Files.Count > 0 && Files.All(f => f.HasVerifiedHash && f.Size > 0);

    [GeneratedRegex("^[0-9a-f]{40}$")]
    internal static partial Regex CommitPattern();
}

public sealed partial class CatalogueFile
{
    /// <summary>Local file name (flat, no folders). Split parts keep their "-0000k-of-0000N.gguf" names.</summary>
    public string Name { get; set; } = "";

    public long Size { get; init; }

    /// <summary>Lower-case hex SHA-256. Anything else (for example a placeholder) fails verification.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>HTTPS URLs; the first is the pinned Hugging Face resolve URL, the rest optional mirrors.</summary>
    public List<string> Urls { get; set; } = new();

    [JsonIgnore]
    public bool HasVerifiedHash => Sha256Pattern().IsMatch(Sha256);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    internal static partial Regex Sha256Pattern();
}

public sealed class CatalogueLicence
{
    public string Spdx { get; set; } = "";
    public string Url { get; set; } = "";
    public bool CommercialOk { get; init; }
}

/// <summary>Memory needs used by the placement planner. Bytes, f16 KV unless stated.</summary>
public sealed class ModelMemory
{
    /// <summary>All weight tensors (about the file size).</summary>
    public long WeightsBytes { get; init; }

    /// <summary>MoE: attention, shared and embedding tensors (kept on the GPU). Dense: 0.</summary>
    public long NonExpertBytes { get; init; }

    /// <summary>MoE: expert tensors over all layers. Dense: 0.</summary>
    public long ExpertBytes { get; init; }

    /// <summary>Token embedding and output tensors (dense models). Null: estimated as 5 % of the weights.</summary>
    public long? EmbedOutputBytes { get; init; }

    public int NLayers { get; init; }

    /// <summary>KV cache for 1,024 tokens of context at f16, over all layers.</summary>
    [JsonPropertyName("kv_bytes_per_1k_ctx")]
    public long KvBytesPer1kCtx { get; init; }

    /// <summary>Compute buffers on the device that runs the model.</summary>
    public long ComputeOverheadBytes { get; init; }
}

/// <summary>User-facing guidance for one model. Every field is optional; apps show what is present.</summary>
public sealed class CatalogueAdvice
{
    /// <summary>One sentence on what the model is good at.</summary>
    public string? Summary { get; init; }

    /// <summary>Short tasks it suits, for example "everyday chat", "long documents", "writing code".</summary>
    public List<string> GoodFor { get; init; } = new();

    /// <summary>Short honest limits, for example "slow on laptops without a graphics card".</summary>
    public List<string> Caveats { get; init; } = new();

    /// <summary>Rough kind of PC it is meant for, for example "8 GB laptop" or "gaming PC".</summary>
    public string? Hardware { get; init; }
}

public sealed class ModelFeatures
{
    public bool Tools { get; init; }
    public bool JsonSchema { get; init; }
    public bool Reasoning { get; init; }
}

public sealed class RecommendedSampling
{
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }
    public double? MinP { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = false,
    WriteIndented = true)]
[JsonSerializable(typeof(ModelCatalogue))]
internal sealed partial class CatalogueJsonContext : JsonSerializerContext;
