using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.AiHost.Contracts;

// DTOs for /host/* endpoints (api-v1.md sections 2, 3, 6). Wire names are snake_case.
// Clients must ignore unknown fields and unknown values, so every string-valued
// enumeration is a plain string with constants in Constants.cs.

/// <summary>Contents of host.json (api-v1.md section 2).</summary>
public sealed class DiscoveryFile
{
    public int Schema { get; set; } = 1;
    public string Service { get; set; } = ApiInfo.ServiceName;
    public string ApiVersion { get; set; } = Contracts.ApiInfo.ApiVersion;
    public string BaseUrl { get; set; } = "";
    public int Port { get; init; }
    public int Pid { get; init; }
    public string InstanceId { get; set; } = "";
    public string HostVersion { get; set; } = "";
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>GET /host/ping.</summary>
public sealed class PingResponse
{
    public string Service { get; set; } = ApiInfo.ServiceName;
    public string ApiVersion { get; set; } = Contracts.ApiInfo.ApiVersion;
    public string HostVersion { get; set; } = "";
    public string InstanceId { get; set; } = "";
}

// ---- /host/auth ------------------------------------------------------------

/// <summary>POST /host/auth/requests body.</summary>
public sealed class AuthRequestCreate
{
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string? AppVersion { get; init; }
    public string? Reason { get; init; }
}

/// <summary>202 response of POST /host/auth/requests.</summary>
public sealed class AuthRequestCreated
{
    public string RequestId { get; set; } = "";
    public string Status { get; set; } = AuthStatuses.Pending;
    public DateTimeOffset ExpiresAt { get; init; }
    public int PollWaitMs { get; set; } = ApiLimits.AuthWaitMsDefault;
}

/// <summary>GET /host/auth/requests/{id}. The token is present once, on approval.</summary>
public sealed class AuthRequestStatus
{
    public string Status { get; set; } = AuthStatuses.Pending;
    public string? Token { get; init; }
    public string? TokenId { get; init; }
    public string? AppId { get; init; }
}

// ---- /host/status ----------------------------------------------------------

/// <summary>GET /host/status.</summary>
public sealed class HostStatus
{
    public string ApiVersion { get; set; } = Contracts.ApiInfo.ApiVersion;
    public string HostVersion { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string State { get; set; } = HostStates.NoModel;
    public StatusClient? Client { get; init; }

    /// <summary>Keyed by model kind. Expect unknown kinds and ignore them.</summary>
    public Dictionary<string, ModelStatus> Models { get; set; } = new();

    public QueueStatus Queue { get; set; } = new();
    public HardwareInfo Hardware { get; set; } = new();
    public bool UpgradeAvailable { get; init; }
    public SharingStatus Sharing { get; set; } = new();

    /// <summary>Live use of the engine. Counts and speeds only. Null until the host has sampled.</summary>
    public HostUsage? Usage { get; set; }
}

public sealed class StatusClient
{
    public string AppId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
}

public sealed class ModelStatus
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string State { get; set; } = ModelStates.NotInstalled;
    public DateTimeOffset? LoadedSince { get; init; }
    public double LoadProgress { get; init; }
    public bool KeepLoaded { get; init; }
    public int IdleUnloadSeconds { get; init; }
}

public sealed class QueueStatus
{
    public int Running { get; init; }
    public int Waiting { get; init; }
    public int WaitingInteractive { get; init; }
    public int WaitingBatch { get; init; }
    public int YoursWaiting { get; init; }
}

public sealed class HardwareInfo
{
    public string Os { get; set; } = "windows";
    public string Arch { get; set; } = "x64";
    public int CpuCores { get; init; }
    public long RamBytes { get; init; }
    public List<GpuInfo> Gpus { get; set; } = new();
}

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public long VramBytes { get; init; }
    public string Backend { get; set; } = "";
}

public sealed class SharingStatus
{
    public bool Lan { get; init; }
    public bool Relay { get; init; }
}

// ---- /host/capabilities ----------------------------------------------------

/// <summary>GET /host/capabilities.</summary>
public sealed class HostCapabilities
{
    public string ApiVersion { get; set; } = Contracts.ApiInfo.ApiVersion;
    public long CapabilitiesRevision { get; init; }
    public ChatCapabilities Chat { get; set; } = new();
    public EmbeddingCapabilities Embeddings { get; set; } = new();
    public CapabilityLimits Limits { get; set; } = new();
    public List<string> Endpoints { get; set; } = new();
}

public sealed class ChatCapabilities
{
    public bool Available { get; init; }

    /// <summary>Set when Available is false: see <see cref="UnavailableReasons"/>.</summary>
    public string? Reason { get; init; }

    public ChatModelInfo? Model { get; init; }
    public int? ContextTokens { get; init; }
    public int? ContextTokensMax { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int? ParallelSlots { get; init; }
    public ChatFeatures? Features { get; init; }
    public PlacementInfo? Placement { get; init; }
    public SpeedEstimate? SpeedEstimate { get; init; }
}

public sealed class ChatModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Family { get; init; }
    public double? ParamsTotalB { get; init; }
    public double? ParamsActiveB { get; init; }
    public string? Quant { get; init; }
    public long? SizeBytes { get; init; }
}

public sealed class ChatFeatures
{
    public bool Streaming { get; init; }
    public bool SystemPrompt { get; init; }
    public bool JsonObject { get; init; }
    public bool JsonSchema { get; init; }
    public bool Tools { get; init; }
    public bool Reasoning { get; init; }
}

public sealed class PlacementInfo
{
    public string Backend { get; set; } = "";
    public string GpuOffload { get; set; } = "";
    public int GpuLayers { get; init; }
    public int TotalLayers { get; init; }
    public int? MoeExpertLayersOnCpu { get; init; }
}

public sealed class SpeedEstimate
{
    public double PromptTokensPerSecond { get; init; }
    public double GenerationTokensPerSecond { get; init; }
}

public sealed class EmbeddingCapabilities
{
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public EmbeddingModelInfo? Model { get; init; }
    public int? Dimensions { get; init; }
    public int? MaxInputTokens { get; init; }
    public int? MaxBatchInputs { get; init; }
    public bool? Normalized { get; init; }
}

public sealed class EmbeddingModelInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class CapabilityLimits
{
    public int MaxRequestBytes { get; set; } = ApiLimits.MaxRequestBytes;
    public int MaxQueuedPerApp { get; set; } = ApiLimits.MaxQueuedPerApp;
    public DeadlineDefaults DefaultDeadlineMs { get; set; } = new();
    public int MaxDeadlineMs { get; set; } = ApiLimits.MaxDeadlineMs;
}

public sealed class DeadlineDefaults
{
    public int Interactive { get; set; } = ApiLimits.DefaultInteractiveDeadlineMs;
    public int Batch { get; set; } = ApiLimits.DefaultBatchDeadlineMs;
}

// ---- /v1/models, warmup, tokenize -------------------------------------------

/// <summary>GET /v1/models.</summary>
public sealed class ModelList
{
    public string Object { get; set; } = "list";
    public List<ModelEntry> Data { get; set; } = new();
}

public sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string Object { get; set; } = "model";
    public string OwnedBy { get; set; } = ApiInfo.ServiceName;
    public string Kind { get; set; } = ModelKinds.Chat;
    public bool Active { get; init; }
    public bool Loaded { get; init; }
    public int? ContextTokens { get; init; }
}

/// <summary>POST /host/warmup body.</summary>
public sealed class WarmupRequest
{
    public string Kind { get; set; } = ModelKinds.Chat;
}

/// <summary>POST /host/warmup response: 202 (loading) or 200 (loaded).</summary>
public sealed class WarmupResponse
{
    public string State { get; set; } = ModelStates.Loading;
    public double? LoadProgress { get; init; }
}

/// <summary>POST /host/tokenize body: either Messages or Text.</summary>
public sealed class TokenizeRequest
{
    public string? Model { get; init; }
    public List<ChatMessage>? Messages { get; init; }
    public string? Text { get; init; }
}

public sealed class TokenizeResponse
{
    public int Tokens { get; init; }
    public int ContextTokens { get; init; }
}

// ---- errors ----------------------------------------------------------------

/// <summary>The error envelope: {"error":{...}} (api-v1.md section 5).</summary>
public sealed class ApiError
{
    public ApiErrorBody Error { get; set; } = new();
}

public sealed class ApiErrorBody
{
    public string Message { get; set; } = "";
    public string Type { get; set; } = ErrorTypes.Server;
    public string Code { get; set; } = ErrorCodes.InternalError;

    /// <summary>Always written, as null when absent, to match the documented shape.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Param { get; init; }

    public int? RetryAfterMs { get; init; }
    public Dictionary<string, JsonElement>? Details { get; init; }
}

/// <summary>Payload of the ": portal {...}" SSE comment lines.</summary>
public sealed class PortalProgressEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = ProgressEvents.Queued;

    public int? Position { get; init; }
    public double? Progress { get; init; }
}

/// <summary>Live use of the engine process(es), sampled at most every 1.5 seconds and only while someone asks.</summary>
public sealed class HostUsage
{
    public long RamUsedBytes { get; init; }
    public long VramUsedBytes { get; init; }

    /// <summary>Engine processor use as a share of the whole PC, 0 to 100.</summary>
    public int CpuPercent { get; init; }

    /// <summary>Tokens per second of the last finished request, or null when none yet.</summary>
    public double? GenerationTokensPerSecond { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
