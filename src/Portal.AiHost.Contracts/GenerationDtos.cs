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
    public const string Converting = "converting";
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

    /// <summary>
    /// Video only: the file type of the finished clip, <c>avi</c> (the default, motion JPEG) or <c>mp4</c> (H.264, plays
    /// everywhere). <c>mp4</c> needs ffmpeg on the host (see <see cref="GenerationAvailability.VideoContainers"/>).
    /// </summary>
    public string? Container { get; set; }
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

    /// <summary>Video file types a job may ask for in <c>container</c>. Present while a video model can run.</summary>
    public List<string>? VideoContainers { get; init; }

    /// <summary>True when POST /host/video/joins is available (ffmpeg is installed and a video model can run).</summary>
    public bool VideoJoins { get; init; }
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

/// <summary>Values of <see cref="GenerationJobRequest.Container"/>.</summary>
public static class GenerationContainers
{
    public const string Avi = "avi";
    public const string Mp4 = "mp4";
}

/// <summary>Limits of POST /host/video/joins (api-v1.md).</summary>
public static class VideoJoinLimits
{
    public const int MaxClips = 20;
    public const int MaxAudioTracks = 4;
    public const double MaxCrossfadeSeconds = 5;

    /// <summary>Joins one app may have queued or running at once.</summary>
    public const int MaxOpenJoinsPerApp = 2;
}

/// <summary>The change after one clip of a join: <c>cut</c>, or <c>crossfade</c> of <c>seconds</c>.</summary>
public sealed class VideoJoinTransition
{
    /// <summary><c>cut</c> (default) or <c>crossfade</c>.</summary>
    public string Kind { get; set; } = "cut";

    /// <summary>Crossfade length in seconds (0.05 to 5, and at most half of the shorter clip). Default 0.5.</summary>
    public double? Seconds { get; set; }
}

/// <summary>
/// One soundtrack of a join. Exactly one source: a finished speech job of this app (<c>speech_job</c>) or the audio
/// bytes themselves (<c>data_base64</c>, WAV or MP3, small: the request is limited in size).
/// </summary>
public sealed class VideoJoinAudio
{
    public string? SpeechJob { get; set; }
    public string? DataBase64 { get; set; }

    /// <summary><c>music</c> (default) or <c>voice</c>.</summary>
    public string? Role { get; set; }

    /// <summary>0 to 2, default 1.</summary>
    public double? Volume { get; set; }

    /// <summary>Seconds into the video where the track starts, default 0.</summary>
    public double? StartSeconds { get; set; }
}

/// <summary>POST /host/video/joins.</summary>
public sealed class VideoJoinRequest
{
    /// <summary>
    /// The clips in order, 1 to 20. Each is the id of a finished video job of this app, or the file name of a clip in
    /// the host's own video folder (a name only, no folders). Never a path.
    /// </summary>
    public List<string> Clips { get; set; } = [];

    /// <summary>
    /// Omitted: cuts. One entry: used between every two clips. Otherwise one entry per gap (clips minus one).
    /// </summary>
    public List<VideoJoinTransition>? Transitions { get; set; }

    public List<VideoJoinAudio>? Audio { get; set; }

    /// <summary>With both music and voice tracks: the music sinks while the voice speaks.</summary>
    public bool? Duck { get; set; }
}

/// <summary>POST /host/uploads (the answer) and GET /host/uploads/{id}: the record of one upload, never its content.</summary>
public sealed class UploadInfo
{
    /// <summary><c>upl_</c> and 32 hex digits.</summary>
    public string Id { get; set; } = "";

    public string Object { get; set; } = "upload";

    /// <summary>Size of the stored file.</summary>
    public long Bytes { get; set; }

    public string ContentType { get; set; } = "";

    /// <summary>UTC. Moves forward each time a job uses the upload.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
