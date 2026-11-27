namespace Portal.AiHost.Contracts;

/// <summary>API version and fixed identifiers (api-v1.md sections 1, 2, 6).</summary>
/// <summary>Folder names shared by the host and the window.</summary>
public static class HostFolders
{
    /// <summary>Subfolder the host creates (and marks) inside a models folder the user picked.</summary>
    public const string ModelsSubfolder = "Portal AI Host Models";
}

public static class ApiInfo
{
    public const string ApiVersion = "1";
    public const string ServiceName = "portal-ai-host";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 41870;
    public const string UrlOverrideEnvironmentVariable = "PORTAL_AI_HOST_URL";
    public const string TokenPrefix = "pah_";
    public const string ProtocolScheme = "portal-ai-host";
}

/// <summary>Request and response header names (api-v1.md section 4).</summary>
public static class HeaderNames
{
    public const string Authorization = "Authorization";
    public const string Priority = "X-Portal-Priority";
    public const string DeadlineMs = "X-Portal-Deadline-Ms";
    public const string Load = "X-Portal-Load";
    public const string ClientRequestId = "X-Portal-Client-Request-Id";
    public const string RequestId = "X-Portal-Request-Id";
    public const string QueueMs = "X-Portal-Queue-Ms";
    public const string Model = "X-Portal-Model";
    public const string CapabilitiesRevision = "X-Portal-Capabilities-Revision";

    /// <summary>Asks for a smaller working context for this request; clamped, never an error.</summary>
    public const string ContextTokens = "X-Portal-Context-Tokens";
    public const string RetryAfter = "Retry-After";
}

/// <summary>Values of <see cref="HeaderNames.Priority"/>.</summary>
public static class PriorityValues
{
    public const string Interactive = "interactive";
    public const string Batch = "batch";
}

/// <summary>Values of <see cref="HeaderNames.Load"/>.</summary>
public static class LoadBehaviorValues
{
    public const string Wait = "wait";
    public const string Fail = "fail";
}

/// <summary>Numeric limits stated in api-v1.md.</summary>
public static class ApiLimits
{
    public const int MinDeadlineMs = 1000;
    public const int MaxDeadlineMs = 3_600_000;
    public const int DefaultInteractiveDeadlineMs = 120_000;
    public const int DefaultBatchDeadlineMs = 1_800_000;
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    public const int MaxQueuedPerApp = 8;
    public const int MaxClientRequestIdLength = 64;
    public const int AuthWaitMsDefault = 25_000;
    public const int AuthRequestLifetimeMinutes = 10;
    public const int AuthRequestsPerHourPerApp = 10;
    public const int DeniedCooldownHours = 24;
    public const int ProgressCommentIntervalSeconds = 5;
}

/// <summary>
/// Model kinds. Open strings so a host or client ignores kinds it does not know
/// (a speech kind can be added later without a contract change).
/// </summary>
public static class ModelKinds
{
    public const string Chat = "chat";
    public const string Embedding = "embedding";
    public const string Transcription = "transcription";
    public const string Voice = "voice";

    /// <summary>Still pictures from a text prompt (generation engine, <see cref="Routes.ImageJobs"/>).</summary>
    public const string Image = "image";

    /// <summary>Short clips from a text prompt or a start picture (generation engine, <see cref="Routes.VideoJobs"/>).</summary>
    public const string Video = "video";
}

/// <summary>
/// Use-case categories the Models page groups by. Open strings: a catalogue or host may send an id this build does not
/// know, which then shows under <see cref="Other"/>. <see cref="Resolve"/> falls back to the model kind.
/// </summary>
public static class ModelCategories
{
    public const string Chat = "chat";
    public const string Coding = "coding";
    public const string Reasoning = "reasoning";
    public const string Vision = "vision";
    public const string Search = "search";
    public const string Image = "image";
    public const string Video = "video";
    public const string Speech = "speech";
    public const string Other = "other";

    /// <summary>Display order of the known categories.</summary>
    public static IReadOnlyList<string> Ordered { get; } =
        [Chat, Coding, Reasoning, Vision, Search, Image, Video, Speech, Other];

    /// <summary>Category for a model kind when the catalogue gives none.</summary>
    public static string FromKind(string? kind) => kind switch
    {
        ModelKinds.Chat => Chat,
        ModelKinds.Embedding => Search,
        ModelKinds.Transcription => Speech,
        ModelKinds.Voice => Speech,
        "speech" => Speech,
        "image" => Image,
        "video" => Video,
        _ => Other,
    };

    /// <summary>The catalogue category when it is a known id, otherwise the one derived from the kind.</summary>
    public static string Resolve(string? category, string? kind)
    {
        var c = category?.Trim().ToLowerInvariant();
        return c is { Length: > 0 } && Ordered.Contains(c) ? c : FromKind(kind);
    }
}

/// <summary>
/// The second concurrent engine slot of a kind. A slot is named by its key: the kind itself for the first slot (the PC's
/// own model lives there) and the kind plus ".2" for the second, which only a device with Model control fills.
/// </summary>
public static class ModelSlots
{
    public const string SecondSuffix = ".2";

    public static string Second(string kind) => kind + SecondSuffix;

    public static bool IsSecond(string key) => key.EndsWith(SecondSuffix, StringComparison.Ordinal);

    /// <summary>The model kind of a slot key ("chat.2" is "chat").</summary>
    public static string KindOf(string key) => IsSecond(key) ? key[..^SecondSuffix.Length] : key;
}

/// <summary>Engine kinds. Open strings, see <see cref="ModelKinds"/>.</summary>
public static class EngineKinds
{
    public const string LlamaServer = "llama-server";
    public const string WhisperServer = "whisper-server";

    /// <summary>Image and video generation (model kinds image and video), served by the bundled stable-diffusion.cpp runtime.</summary>
    public const string StableDiffusionCpp = "stable-diffusion-cpp";
    public const string VoiceServer = "voice-server";
}

/// <summary>Model names clients may send in the request model field.</summary>
public static class ModelAliases
{
    public const string DefaultChat = "default";
    public const string DefaultEmbedding = "default-embedding";
}

/// <summary>Values of the /host/status state field.</summary>
public static class HostStates
{
    public const string Ready = "ready";
    public const string Busy = "busy";
    public const string Loading = "loading";
    public const string Idle = "idle";
    public const string NoModel = "no_model";
    public const string Downloading = "downloading";
    public const string Error = "error";
}

/// <summary>Values of the per-kind model state field.</summary>
public static class ModelStates
{
    public const string Unloaded = "unloaded";
    public const string Loading = "loading";
    public const string Loaded = "loaded";
    public const string Failed = "failed";
    public const string NotInstalled = "not_installed";
}

/// <summary>Values of the access-request status field.</summary>
public static class AuthStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Claimed = "claimed";
}

/// <summary>Chat roles.</summary>
public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>Chat finish reasons.</summary>
public static class FinishReasons
{
    public const string Stop = "stop";
    public const string Length = "length";
    public const string ToolCalls = "tool_calls";
}

/// <summary>Reasons a capabilities section is unavailable.</summary>
public static class UnavailableReasons
{
    public const string NoModel = "no_model";
    public const string InsufficientMemory = "insufficient_memory";
    public const string Disabled = "disabled";
}

/// <summary>Phases reported in deadline_exceeded details.</summary>
public static class DeadlinePhases
{
    public const string Queued = "queued";
    public const string Loading = "loading";
    public const string Generating = "generating";
}

/// <summary>Event names of the ": portal {...}" SSE comment lines.</summary>
public static class ProgressEvents
{
    public const string Queued = "queued";
    public const string Loading = "loading";
    public const string Started = "started";
    public const string CommentPrefix = ": portal ";
}

/// <summary>Endpoint names listed in /host/capabilities.</summary>
public static class EndpointNames
{
    public const string ChatCompletions = "chat.completions";
    public const string Embeddings = "embeddings";
    public const string Models = "models";
    public const string Tokenize = "tokenize";
    public const string Warmup = "warmup";
    public const string Performance = "performance";
    public const string EaseOff = "ease-off";
    public const string Health = "health";

    /// <summary>Listed while an image model can run (POST /host/image/jobs).</summary>
    public const string ImageJobs = "image.jobs";

    /// <summary>Listed while a video model can run (POST /host/video/jobs).</summary>
    public const string VideoJobs = "video.jobs";

    /// <summary>Listed while ffmpeg is installed and a video model can run (POST /host/video/joins).</summary>
    public const string VideoJoins = "video.joins";
    public const string Voices = "voices";
    public const string Speech = "speech";
}

/// <summary>Route paths.</summary>
public static class Routes
{
    public const string Ping = "/host/ping";
    public const string Status = "/host/status";
    public const string Capabilities = "/host/capabilities";
    public const string Performance = "/host/performance";
    public const string EaseOff = "/host/ease-off";

    // Model control (paired devices with the grant only; docs/client-mode-v1.md section 4.7).
    public const string DeviceModels = "/host/models";
    public const string DeviceModelLoad = "/host/models/load";
    public const string DeviceModelUnload = "/host/models/unload";
    public const string DevicePlacement = "/host/placement";
    public const string Health = "/host/health";
    public const string Warmup = "/host/warmup";
    public const string Tokenize = "/host/tokenize";
    public const string AuthRequests = "/host/auth/requests";
    public const string AuthToken = "/host/auth/token";
    public const string Models = "/v1/models";
    public const string ChatCompletions = "/v1/chat/completions";
    public const string Embeddings = "/v1/embeddings";
    public const string AudioTranscriptions = "/v1/audio/transcriptions";

    // Generation jobs (api-v1.md section 6, "Image and video jobs"): POST creates, GET /{id} reads, DELETE /{id} cancels,
    // GET /{id}/result and /{id}/preview return the file.
    public const string ImageJobs = "/host/image/jobs";
    public const string VideoJobs = "/host/video/jobs";

    // Joins: POST creates, GET /{id}, DELETE /{id}, GET /{id}/result like a job; GET /host/video/jobs/{id}/last-frame
    // (and /host/video/joins/{id}/last-frame) returns the last picture as PNG.
    public const string VideoJoins = "/host/video/joins";

    /// <summary>The job collection route of a generation kind (image or video), or null for any other kind.</summary>
    public static string? JobsFor(string kind) => kind switch
    {
        ModelKinds.Image => ImageJobs,
        ModelKinds.Video => VideoJobs,
        _ => null,
    };

    // Voice library and speech jobs (api-v1 section 6, Voice).
    public const string Voices = "/host/voices";
    public const string VoiceById = "/host/voices/{id}";
    public const string SpeechJobs = "/host/speech/jobs";
    public const string SpeechJobById = "/host/speech/jobs/{id}";
    public const string SpeechJobAudio = "/host/speech/jobs/{id}/audio";
}
