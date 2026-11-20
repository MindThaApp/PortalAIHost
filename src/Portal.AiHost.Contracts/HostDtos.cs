using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.AiHost.Contracts;

// DTOs for /host/* endpoints (api-v1.md sections 2, 3, 6). Wire names are snake_case.
// Clients must ignore unknown fields and unknown values, so every string-valued
// enumeration is a plain string with constants in Constants.cs.

/// <summary>Contents of host.json (api-v1.md section 2).</summary>
public sealed class DiscoveryFile
{
    public int Schema { get; init; } = 1;
    public string Service { get; init; } = ApiInfo.ServiceName;
    public string ApiVersion { get; init; } = Contracts.ApiInfo.ApiVersion;
    public string BaseUrl { get; init; } = "";
    public int Port { get; init; }
    public int Pid { get; init; }
    public string InstanceId { get; init; } = "";
    public string HostVersion { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>GET /host/ping.</summary>
public sealed class PingResponse
{
    public string Service { get; init; } = ApiInfo.ServiceName;
    public string ApiVersion { get; init; } = Contracts.ApiInfo.ApiVersion;
    public string HostVersion { get; init; } = "";
    public string InstanceId { get; init; } = "";
}

// ---- /host/auth ------------------------------------------------------------

/// <summary>POST /host/auth/requests body.</summary>
public sealed class AuthRequestCreate
{
    public string AppId { get; init; } = "";
    public string AppName { get; init; } = "";
    public string? AppVersion { get; init; }
    public string? Reason { get; init; }
}

/// <summary>202 response of POST /host/auth/requests.</summary>
public sealed class AuthRequestCreated
{
    public string RequestId { get; init; } = "";
    public string Status { get; init; } = AuthStatuses.Pending;
    public DateTimeOffset ExpiresAt { get; init; }
    public int PollWaitMs { get; init; } = ApiLimits.AuthWaitMsDefault;
}

/// <summary>GET /host/auth/requests/{id}. The token is present once, on approval.</summary>
public sealed class AuthRequestStatus
{
    public string Status { get; init; } = AuthStatuses.Pending;
    public string? Token { get; init; }
    public string? TokenId { get; init; }
    public string? AppId { get; init; }
}

// ---- /host/status ----------------------------------------------------------

/// <summary>GET /host/status.</summary>
public sealed class HostStatus
{
    public string ApiVersion { get; init; } = Contracts.ApiInfo.ApiVersion;
    public string HostVersion { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string State { get; init; } = HostStates.NoModel;
    public StatusClient? Client { get; init; }

    /// <summary>Keyed by model kind. Expect unknown kinds and ignore them.</summary>
    public Dictionary<string, ModelStatus> Models { get; init; } = new();

    public QueueStatus Queue { get; init; } = new();
    public HardwareInfo Hardware { get; init; } = new();
    public bool UpgradeAvailable { get; init; }
    public SharingStatus Sharing { get; init; } = new();
}

public sealed class StatusClient
{
    public string AppId { get; init; } = "";
    public string TokenId { get; init; } = "";
    public DateTimeOffset ApprovedAt { get; init; }
}

public sealed class ModelStatus
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string State { get; init; } = ModelStates.NotInstalled;
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
    public string Os { get; init; } = "windows";
    public string Arch { get; init; } = "x64";
    public int CpuCores { get; init; }
    public long RamBytes { get; init; }
    public List<GpuInfo> Gpus { get; init; } = new();
}

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public long VramBytes { get; init; }
    public string Backend { get; init; } = "";
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
    public string ApiVersion { get; init; } = Contracts.ApiInfo.ApiVersion;
    public long CapabilitiesRevision { get; init; }
    public ChatCapabilities Chat { get; init; } = new();
    public EmbeddingCapabilities Embeddings { get; init; } = new();
    public CapabilityLimits Limits { get; init; } = new();
    public List<string> Endpoints { get; init; } = new();
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
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
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
    public string Backend { get; init; } = "";
    public string GpuOffload { get; init; } = "";
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
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
}

public sealed class CapabilityLimits
{
    public int MaxRequestBytes { get; init; } = ApiLimits.MaxRequestBytes;
    public int MaxQueuedPerApp { get; init; } = ApiLimits.MaxQueuedPerApp;
    public DeadlineDefaults DefaultDeadlineMs { get; init; } = new();
    public int MaxDeadlineMs { get; init; } = ApiLimits.MaxDeadlineMs;
}

public sealed class DeadlineDefaults
{
    public int Interactive { get; init; } = ApiLimits.DefaultInteractiveDeadlineMs;
    public int Batch { get; init; } = ApiLimits.DefaultBatchDeadlineMs;
}

// ---- /v1/models, warmup, tokenize -------------------------------------------

/// <summary>GET /v1/models.</summary>
public sealed class ModelList
{
    public string Object { get; init; } = "list";
    public List<ModelEntry> Data { get; init; } = new();
}

public sealed class ModelEntry
{
    public string Id { get; init; } = "";
    public string Object { get; init; } = "model";
    public string OwnedBy { get; init; } = ApiInfo.ServiceName;
    public string Kind { get; init; } = ModelKinds.Chat;
    public bool Active { get; init; }
    public bool Loaded { get; init; }
    public int? ContextTokens { get; init; }
}

/// <summary>POST /host/warmup body.</summary>
public sealed class WarmupRequest
{
    public string Kind { get; init; } = ModelKinds.Chat;
}

/// <summary>POST /host/warmup response: 202 (loading) or 200 (loaded).</summary>
public sealed class WarmupResponse
{
    public string State { get; init; } = ModelStates.Loading;
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
    public ApiErrorBody Error { get; init; } = new();
}

public sealed class ApiErrorBody
{
    public string Message { get; init; } = "";
    public string Type { get; init; } = ErrorTypes.Server;
    public string Code { get; init; } = ErrorCodes.InternalError;

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
    public string Event { get; init; } = ProgressEvents.Queued;

    public int? Position { get; init; }
    public double? Progress { get; init; }
}
