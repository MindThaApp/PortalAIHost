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

    /// <summary><c>client</c> while this host uses a paired PC (docs/client-mode-v1.md section 4.2); omitted in server mode.</summary>
    public string? Mode { get; set; }

    /// <summary>The paired PC and the route to it, in client mode only. Never an address, a key or a token.</summary>
    public StatusRemote? Remote { get; set; }

    /// <summary>Keyed by model kind. Expect unknown kinds and ignore them.</summary>
    public Dictionary<string, ModelStatus> Models { get; set; } = new();

    public QueueStatus Queue { get; set; } = new();
    public HardwareInfo Hardware { get; set; } = new();
    public bool UpgradeAvailable { get; init; }
    public SharingStatus Sharing { get; set; } = new();

    /// <summary>Live use of the engine. Counts and speeds only. Null until the host has sampled.</summary>
    public HostUsage? Usage { get; set; }

    /// <summary>Planned memory of every running engine against the user's RAM limit. Null when the host cannot tell yet.</summary>
    public MemoryBudgetStatus? Memory { get; set; }

    /// <summary>Image and video jobs: what can run here and how many jobs are queued or running. Null on older hosts.</summary>
    public GenerationAvailability? Generation { get; set; }
}

/// <summary>The paired PC as <c>/host/status</c> shows it in client mode.</summary>
public sealed class StatusRemote
{
    public string Name { get; set; } = "";

    /// <summary><c>lan</c>, <c>relay</c> or <c>down</c>.</summary>
    public string Route { get; set; } = "down";

    public int? RttMs { get; set; }
}

public sealed class StatusClient
{
    public string AppId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; init; }

    /// <summary>True while the user has paused this app: new generation requests get 403 app_paused.</summary>
    public bool Paused { get; init; }

    /// <summary>"high", "normal" or "low": how the queue ranks this app.</summary>
    public string Priority { get; init; } = "normal";
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

    /// <summary>Voice cloning and speech (api-v1 section 6, Voice). Absent when this host has no voice feature.</summary>
    public VoiceCapabilities? Voice { get; set; }

    /// <summary>
    /// Only in the answer to a paired device: whether this device has Model control (4.7). Missing for local apps.
    /// </summary>
    public bool? ModelControl { get; set; }

    /// <summary>Image and video jobs: what can run here and how many jobs are queued or running. Null on older hosts.</summary>
    public GenerationAvailability? Generation { get; set; }

    /// <summary>GPUs, the flavour each engine runs on and which flavours are installed. Null on older hosts.</summary>
    public HardwareCapabilities? Hardware { get; set; }
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

/// <summary>
/// Planned memory across all running engines (a loading engine counts from the moment its placement is decided).
/// "Planned" is what the planner reserved, not what the operating system reports.
/// </summary>
public sealed class MemoryBudgetStatus
{
    /// <summary>The user's RAM limit for the host's engines.</summary>
    public long RamLimitBytes { get; set; }

    /// <summary>Sum of the planned RAM of every engine, plus memory apps reserved for their own models.</summary>
    public long RamPlannedBytes { get; set; }

    /// <summary>Limit minus planned, never below zero.</summary>
    public long RamFreeBytes { get; set; }

    /// <summary>Video memory of the best graphics card; 0 when there is none or it is unknown.</summary>
    public long VramTotalBytes { get; set; }

    public long VramPlannedBytes { get; set; }

    public long VramFreeBytes { get; set; }

    /// <summary>Per engine split. Engines of other kinds and apps' own reservations use their own names.</summary>
    public List<EngineMemoryStatus> Engines { get; set; } = [];
}

public sealed class EngineMemoryStatus
{
    /// <summary>Slot key: <c>chat</c>, <c>chat.2</c>, <c>embedding</c> or <c>apps</c> for memory apps reserved.</summary>
    public string Slot { get; set; } = "";

    public string? ModelId { get; set; }

    public long RamBytes { get; set; }

    public long VramBytes { get; set; }

    /// <summary>True while the engine is still loading and the figures are a reservation.</summary>
    public bool Loading { get; set; }

    /// <summary>True when the engine runs on the processor only (the CPU-only preset or no graphics layers).</summary>
    public bool CpuOnly { get; set; }
}

// ---- /host/performance -------------------------------------------------------

/// <summary>POST /host/ease-off: an app asks the host to ease off while it does time-critical work.</summary>
public sealed class EaseOffRequest
{
    /// <summary>Short label such as live_audio. Never shown with content.</summary>
    public string Reason { get; set; } = "";

    /// <summary>How long the request lasts unless renewed, 1000 to 120000 ms.</summary>
    public int TtlMs { get; set; }

    /// <summary>Memory the app's own models hold, so the host plans around it.</summary>
    public long? ReservedBytes { get; set; }
}

/// <summary>GET /host/performance: the limits the host runs under. Read-only for apps.</summary>
public sealed class PerformanceInfo
{
    public string Preset { get; set; } = "balanced";
    public long MemoryLimitBytes { get; set; }
    public long KeepFreeBytes { get; set; }
    public string Compute { get; set; } = "auto";

    /// <summary>gpu or cpu: where the model really runs under these limits.</summary>
    public string ComputeEffective { get; set; } = "cpu";

    /// <summary>Graphics memory the AI may use; omitted when the PC has no supported graphics card.</summary>
    public long? GpuMemoryLimitBytes { get; set; }

    public int CpuThreads { get; set; }
    public string Priority { get; set; } = "low";

    /// <summary>The most of the processor the AI may use, 20 to 100.</summary>
    public int CpuCeilingPercent { get; set; } = 100;

    /// <summary>The ease-off flags that are on.</summary>
    public List<string> EaseOff { get; set; } = new();

    /// <summary>The reason the host is easing off now, or null; written as null so apps can rely on the key.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? EasedOffNow { get; set; }

    /// <summary>fits_fully, fits_partial, cpu_only, needs_smaller_model or too_big; omitted when no chat model is active.</summary>
    public string? Fit { get; set; }

    /// <summary>False when an engine could not start under the memory cap and was started once without it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool MemoryCapActive { get; set; } = true;

    /// <summary>Why the cap is not active, or omitted.</summary>
    public string? MemoryCapReason { get; set; }

    public PerformanceRanges Ranges { get; set; } = new();
    public string SettingsLink { get; set; } = "portal-ai-host:settings/performance";
}

public sealed class PerformanceRanges
{
    public NumberRange MemoryLimitBytes { get; set; } = new();
    public NumberRange KeepFreeBytes { get; set; } = new();
    public NumberRange? GpuMemoryLimitBytes { get; set; }
    public NumberRange CpuThreads { get; set; } = new();
    public bool GpuAvailable { get; set; }
}

public sealed class NumberRange
{
    public long Min { get; set; }
    public long Max { get; set; }

    /// <summary>Slider step; omitted for integer ranges that step by one.</summary>
    public long? Step { get; set; }
}

/// <summary>What a memory slider stop would fit, for the label "16 GB · fits Qwen3 14B fully".</summary>
public sealed class MemoryStop
{
    public long Bytes { get; set; }

    /// <summary>Display name of the largest installed or catalogue chat model that fits fully, or null.</summary>
    public string? FitsModel { get; set; }

    /// <summary>fits_fully or fits_partial for <see cref="FitsModel"/>, or null when nothing fits.</summary>
    public string? Fit { get; set; }
}

/// <summary>Answer to performance.get: the limits plus what the window needs to draw its controls.</summary>
public sealed class PerformanceView
{
    public PerformanceInfo Info { get; set; } = new();
    public long RamBytes { get; set; }
    public int LogicalCores { get; set; }
    public string? GpuName { get; set; }
    public long GpuVramBytes { get; set; }

    /// <summary>Why "Graphics card" is not offered, or null when it is.</summary>
    public string? GpuUnavailableReason { get; set; }

    /// <summary>Display name of the chat model the fit refers to, or null.</summary>
    public string? FitModel { get; set; }

    /// <summary>Smaller model that would fit, when Fit is needs_smaller_model.</summary>
    public string? SmallerModel { get; set; }

    /// <summary>One entry per memory slider stop.</summary>
    public List<MemoryStop> MemoryStops { get; set; } = new();

    /// <summary>The values of each preset on this PC, so the window shows them at once.</summary>
    public Dictionary<string, PerformancePresetValues> Presets { get; set; } = new();
}

public sealed class PerformancePresetValues
{
    public long MemoryLimitBytes { get; set; }
    public int CpuThreads { get; set; }
    public string Priority { get; set; } = "low";
}

// ---- /host/health ------------------------------------------------------------

/// <summary>Values of <see cref="HealthCheck.Status"/>.</summary>
public static class HealthStatuses
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

/// <summary>Values of <see cref="HealthCheck.Id"/>. New ids may appear; clients ignore ids they do not know.</summary>
public static class HealthIds
{
    public const string Service = "service";
    public const string Engine = "engine_files";
    public const string Graphics = "graphics";
    public const string Model = "model";
    public const string Disk = "disk";
    public const string Memory = "memory";
    public const string EaseOff = "ease_off";
    public const string MemoryCap = "memory_cap";
}

/// <summary>
/// Values of <see cref="HealthCheck.FixAction"/>. Each one is an operation the host already has: nothing here
/// deletes or overwrites anything.
/// </summary>
public static class HealthFixes
{
    /// <summary>Load the active model now (engine.load).</summary>
    public const string LoadModel = "load_model";

    /// <summary>Stop the engine and load the model again (engine.unload, then engine.load).</summary>
    public const string RestartEngine = "restart_engine";

    /// <summary>Re-check the files of the model in <see cref="HealthCheck.FixTarget"/> (models.verify).</summary>
    public const string CheckFiles = "check_files";

    /// <summary>Open Settings, Performance (the link in <see cref="HealthCheck.FixTarget"/>).</summary>
    public const string OpenPerformance = "open_performance";

    /// <summary>Open the Models page.</summary>
    public const string OpenModels = "open_models";
}

/// <summary>One line of the health report.</summary>
public sealed class HealthCheck
{
    /// <summary>One of <see cref="HealthIds"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary>ok, warn or fail (<see cref="HealthStatuses"/>).</summary>
    public string Status { get; set; } = HealthStatuses.Ok;

    /// <summary>Short plain-language name, for example "Graphics card".</summary>
    public string Title { get; set; } = "";

    /// <summary>One or two plain sentences on what was found.</summary>
    public string Detail { get; set; } = "";

    /// <summary>One of <see cref="HealthFixes"/>, or omitted when nothing the host can do fixes it.</summary>
    public string? FixAction { get; set; }

    /// <summary>What the fix applies to: a model id for check_files, a portal-ai-host: link for open_performance.</summary>
    public string? FixTarget { get; set; }
}

/// <summary>GET /host/health: whether the host is working, one entry per check. Read-only; changes nothing.</summary>
public sealed class HealthReport
{
    /// <summary>The worst status of the checks.</summary>
    public string Status { get; set; } = HealthStatuses.Ok;

    /// <summary>How many checks are not ok.</summary>
    public int NeedsAttention { get; set; }

    public List<HealthCheck> Checks { get; set; } = new();
}
