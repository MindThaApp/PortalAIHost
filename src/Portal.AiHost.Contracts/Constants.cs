namespace Portal.AiHost.Contracts;

/// <summary>API version and fixed identifiers (api-v1.md sections 1, 2, 6).</summary>
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
}

/// <summary>Engine kinds. Open strings, see <see cref="ModelKinds"/>.</summary>
public static class EngineKinds
{
    public const string LlamaServer = "llama-server";
    public const string WhisperServer = "whisper-server";
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
}

/// <summary>Route paths.</summary>
public static class Routes
{
    public const string Ping = "/host/ping";
    public const string Status = "/host/status";
    public const string Capabilities = "/host/capabilities";
    public const string Warmup = "/host/warmup";
    public const string Tokenize = "/host/tokenize";
    public const string AuthRequests = "/host/auth/requests";
    public const string AuthToken = "/host/auth/token";
    public const string Models = "/v1/models";
    public const string ChatCompletions = "/v1/chat/completions";
    public const string Embeddings = "/v1/embeddings";
    public const string AudioTranscriptions = "/v1/audio/transcriptions";
}
