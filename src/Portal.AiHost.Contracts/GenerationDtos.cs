namespace Portal.AiHost.Contracts;

// DTOs for the image and video job routes (api-v1.md section 6, "Image and video jobs"). Wire names are snake_case.
// Every string enumeration is open: clients ignore values they do not know.

/// <summary>Values of the job <c>state</c> field.</summary>
public static class GenerationJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>True for a state that never changes again.</summary>
    public static bool IsFinal(string state) => state is Succeeded or Failed or Cancelled;
}

/// <summary>Values of the job <c>stage</c> field while it runs.</summary>
public static class GenerationStages
{
    public const string Loading = "loading";
    public const string Encoding = "encoding";
    public const string Sampling = "sampling";
    public const string Decoding = "decoding";
    public const string Saving = "saving";
}

/// <summary>Limits of the job routes (api-v1.md).</summary>
public static class GenerationLimits
{
    public const int MaxPromptChars = 4000;
    public const int MinSide = 64;
    public const int MaxSide = 2048;

    /// <summary>Width and height must be multiples of this.</summary>
    public const int SideMultiple = 16;

    public const int MaxSteps = 150;
    public const int MaxFrames = 241;
    public const int MaxFps = 60;
    public const double MaxCfgScale = 30;

    /// <summary>Jobs one app may have queued or running at once (all kinds together).</summary>
    public const int MaxOpenJobsPerApp = 8;

    /// <summary>Finished jobs remembered per host; older ones are forgotten (their files stay).</summary>
    public const int MaxRememberedJobs = 200;
}

/// <summary>POST /host/image/jobs and POST /host/video/jobs.</summary>
public sealed class GenerationJobRequest
{
    /// <summary>Installed model id. Omitted: the first installed model of the route's kind.</summary>
    public string? Model { get; set; }

    public string Prompt { get; set; } = "";
    public string? NegativePrompt { get; set; }

    /// <summary>Pixels; multiples of 16. Omitted: the model's default.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    public int? Steps { get; set; }

    /// <summary>Omitted or negative: a random seed, reported back in the job.</summary>
    public long? Seed { get; set; }

    public double? CfgScale { get; set; }

    /// <summary>Sampler name of the engine, for example <c>euler</c> or <c>euler_a</c>. Omitted: the model's default.</summary>
    public string? Sampler { get; set; }

    /// <summary>Video only: frame count (the model may round it, for example to 4n+1).</summary>
    public int? Frames { get; set; }

    /// <summary>Video only: frames per second of the clip file.</summary>
    public int? Fps { get; set; }

    /// <summary>
    /// Optional start picture, base64 PNG or JPEG (a <c>data:image/...;base64,</c> prefix is accepted). Video: the
    /// first frame (image to video). Image: the picture to rework (image to image, see <see cref="Strength"/>).
    /// </summary>
    public string? InitImage { get; set; }

    /// <summary>Image to image: how much of the start picture is replaced, 0 to 1.</summary>
    public double? Strength { get; set; }
}

/// <summary>Where the work runs, as planned against the memory left on this PC.</summary>
public sealed class GenerationPlanInfo
{
    /// <summary><c>vulkan</c> or <c>cpu</c>.</summary>
    public string Backend { get; set; } = "cpu";

    public long GpuBytes { get; init; }
    public long CpuBytes { get; init; }

    /// <summary>Parts moved off the graphics card to fit: <c>text_encoder</c>, <c>vae</c>, <c>weights</c>.</summary>
    public List<string> OnCpu { get; set; } = [];
}

/// <summary>The finished file of a job.</summary>
public sealed class GenerationResultInfo
{
    /// <summary>Path of GET .../result, relative to the host's base URL.</summary>
    public string Url { get; set; } = "";

    public string ContentType { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Video only.</summary>
    public int? Frames { get; init; }

    public long Seed { get; init; }
}

/// <summary>GET /host/{kind}/jobs/{id} (also the answer of POST and DELETE).</summary>
public sealed class GenerationJob
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "generation.job";

    /// <summary><c>image</c> or <c>video</c>.</summary>
    public string Kind { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>See <see cref="GenerationJobStates"/>.</summary>
    public string State { get; set; } = GenerationJobStates.Queued;

    /// <summary>While running, see <see cref="GenerationStages"/>.</summary>
    public string? Stage { get; set; }

    /// <summary>0 to 1 over the whole job.</summary>
    public double Progress { get; init; }

    public int? Step { get; init; }
    public int? Steps { get; init; }

    /// <summary>While queued: 1 is next.</summary>
    public int? QueuePosition { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Path of GET .../preview while a preview picture exists, else null.</summary>
    public string? PreviewUrl { get; set; }

    public GenerationPlanInfo? Plan { get; set; }
    public GenerationResultInfo? Result { get; set; }

    /// <summary>Set when the state is failed (or cancelled by the host): the same body as an API error.</summary>
    public ApiErrorBody? Error { get; set; }
}

/// <summary>The <c>generation</c> section of /host/status and /host/capabilities.</summary>
public sealed class GenerationAvailability
{
    /// <summary>True when the engine is installed and an available image model is installed.</summary>
    public bool Image { get; init; }

    /// <summary>True when the engine is installed and an available video model is installed.</summary>
    public bool Video { get; init; }

    /// <summary>Jobs waiting in the queue on this host (every app).</summary>
    public int Queued { get; init; }

    /// <summary>Jobs running now (at most one).</summary>
    public int Running { get; init; }

    /// <summary>Why a kind is or is not available (additive; older hosts leave it out). The booleans above stay as they were.</summary>
    public GenerationDetail? Detail { get; init; }
}

/// <summary>Values of <see cref="GenerationDetail"/>. The set is open: clients ignore values they do not know.</summary>
public static class GenerationDetailStates
{
    /// <summary>Engine and a model are installed and no job is running.</summary>
    public const string Ready = "ready";

    /// <summary>Engine and a model are installed and a job is running now; new jobs wait in the queue.</summary>
    public const string Busy = "busy";

    /// <summary>The engine is installed but no usable model of this kind is.</summary>
    public const string NoModel = "no_model";

    /// <summary>The image and video engine is not installed on this PC.</summary>
    public const string NoEngine = "no_engine";
}

/// <summary><c>generation.detail</c>: one state per kind, see <see cref="GenerationDetailStates"/>.</summary>
public sealed class GenerationDetail
{
    public string Image { get; init; } = GenerationDetailStates.NoEngine;
    public string Video { get; init; } = GenerationDetailStates.NoEngine;
}

/// <summary>GET /host/image/models and /host/video/models: the installed models of that kind.</summary>
public sealed class GenerationModelList
{
    public string Object { get; set; } = "list";
    public List<GenerationModelInfo> Data { get; set; } = [];
}

/// <summary>One installed image or video model.</summary>
public sealed class GenerationModelInfo
{
    /// <summary>Pass this as <c>model</c> when creating a job.</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Category id (see ModelCategories): image or video.</summary>
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary>Family the engine runs, for example sd1.5, sdxl, flux or wan.</summary>
    public string Family { get; set; } = "";

    /// <summary>Total size of the model's files on disk.</summary>
    public long SizeBytes { get; set; }

    /// <summary>True when a job with this model's default settings fits this PC's memory budget now (ignoring other jobs).</summary>
    public bool FitsThisPc { get; set; }

    /// <summary>True when the engine can run this model. A model that is not runnable is listed but jobs with it are refused.</summary>
    public bool Runnable { get; set; }

    /// <summary>True for the model a job uses when it names none.</summary>
    public bool Default { get; set; }
}

/// <summary>GET /host/{kind}/jobs: the calling app's jobs of that kind, newest first.</summary>
public sealed class GenerationJobList
{
    public string Object { get; set; } = "list";
    public List<GenerationJob> Data { get; set; } = [];
}
