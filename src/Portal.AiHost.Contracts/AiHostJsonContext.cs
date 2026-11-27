using System.Text.Json;
using System.Text.Json.Serialization;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Contracts;

/// <summary>
/// Source-generated serializer metadata for every contract type. NativeAOT-safe: no reflection
/// serialisation anywhere. snake_case wire names, nulls omitted unless a property opts in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DiscoveryFile))]
[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(AuthRequestCreate))]
[JsonSerializable(typeof(AuthRequestCreated))]
[JsonSerializable(typeof(AuthRequestStatus))]
[JsonSerializable(typeof(HostStatus))]
[JsonSerializable(typeof(StatusRemote))]
[JsonSerializable(typeof(HostUsage))]
[JsonSerializable(typeof(MemoryBudgetStatus))]
[JsonSerializable(typeof(EngineMemoryStatus))]
[JsonSerializable(typeof(HostCapabilities))]
[JsonSerializable(typeof(PerformanceInfo))]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(EaseOffRequest))]
[JsonSerializable(typeof(PerformanceView))]
[JsonSerializable(typeof(ModelList))]
[JsonSerializable(typeof(WarmupRequest))]
[JsonSerializable(typeof(WarmupResponse))]
[JsonSerializable(typeof(TokenizeRequest))]
[JsonSerializable(typeof(TokenizeResponse))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(PortalProgressEvent))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(EmbeddingsRequest))]
[JsonSerializable(typeof(EmbeddingsResponse))]
[JsonSerializable(typeof(VoiceCreateRequest))]
[JsonSerializable(typeof(VoiceRenameRequest))]
[JsonSerializable(typeof(VoiceInfo))]
[JsonSerializable(typeof(VoiceList))]
[JsonSerializable(typeof(SpeechJobRequest))]
[JsonSerializable(typeof(SpeechJobInfo))]
[JsonSerializable(typeof(ControlEnvelope))]
[JsonSerializable(typeof(PairStartRequest))]
[JsonSerializable(typeof(PairStartResponse))]
[JsonSerializable(typeof(PairFinishRequest))]
[JsonSerializable(typeof(PairFinishResponse))]
[JsonSerializable(typeof(RemoteStatus))]
[JsonSerializable(typeof(PairingOfferInfo))]
[JsonSerializable(typeof(RemoveDeviceCommand))]
[JsonSerializable(typeof(SetModelControlCommand))]
[JsonSerializable(typeof(TakeBackCommand))]
[JsonSerializable(typeof(PauseDeviceCommand))]
[JsonSerializable(typeof(SetDeviceSettingsCommand))]
[JsonSerializable(typeof(RenameDeviceCommand))]
[JsonSerializable(typeof(DeviceAlert))]
[JsonSerializable(typeof(DeviceModelList))]
[JsonSerializable(typeof(DeviceLoadRequest))]
[JsonSerializable(typeof(DeviceUnloadRequest))]
[JsonSerializable(typeof(DeviceModelState))]
[JsonSerializable(typeof(DevicePlacementRequest))]
[JsonSerializable(typeof(DevicePlacementList))]
[JsonSerializable(typeof(SetModeCommand))]
[JsonSerializable(typeof(SetFallbackCommand))]
[JsonSerializable(typeof(SetRelayModeCommand))]
[JsonSerializable(typeof(UseRelayCommand))]
[JsonSerializable(typeof(ClientModeStatus))]
[JsonSerializable(typeof(PairCommand))]
[JsonSerializable(typeof(PairResult))]
[JsonSerializable(typeof(RemotePlacementCommand))]
[JsonSerializable(typeof(RemoteModelsAnswer))]
[JsonSerializable(typeof(AppModelCommand))]
[JsonSerializable(typeof(AppModelList))]
[JsonSerializable(typeof(ShowRequest))]
[JsonSerializable(typeof(ControlError))]
[JsonSerializable(typeof(ApprovalList))]
[JsonSerializable(typeof(ApprovalDecision))]
[JsonSerializable(typeof(ClientList))]
[JsonSerializable(typeof(ClientRevoke))]
[JsonSerializable(typeof(AppControlCommand))]
[JsonSerializable(typeof(ModelListing))]
[JsonSerializable(typeof(DownloadCommand))]
[JsonSerializable(typeof(RemoveModelCommand))]
[JsonSerializable(typeof(SettingsSaved))]
[JsonSerializable(typeof(PerformanceReloadInfo))]
[JsonSerializable(typeof(ActivateModelCommand))]
[JsonSerializable(typeof(VerifyModelCommand))]
[JsonSerializable(typeof(ImportModelCommand))]
[JsonSerializable(typeof(MoveStoreCommand))]
[JsonSerializable(typeof(StoreFolderInfo))]
[JsonSerializable(typeof(CleanupScan))]
[JsonSerializable(typeof(CleanupCommand))]
[JsonSerializable(typeof(CleanupResult))]
[JsonSerializable(typeof(StoreProgressEvent))]
[JsonSerializable(typeof(DownloadProgressEvent))]
[JsonSerializable(typeof(EngineStateEvent))]
[JsonSerializable(typeof(HostSettings))]
[JsonSerializable(typeof(TryItRequest))]
[JsonSerializable(typeof(TryItChunkEvent))]
[JsonSerializable(typeof(TryItDone))]
[JsonSerializable(typeof(ControlStatus))]
[JsonSerializable(typeof(EngineCommand))]
[JsonSerializable(typeof(ActivityReport))]
[JsonSerializable(typeof(TrayPopupEvent))]
public sealed partial class AiHostJsonContext : JsonSerializerContext
{
}

/// <summary>Serialisation helpers over <see cref="AiHostJsonContext"/>.</summary>
public static class AiHostJson
{
    public static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Serialize(value, info);

    public static byte[] SerializeToUtf8<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToUtf8Bytes(value, info);

    public static T? Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(utf8, info);

    /// <summary>Builds an SSE progress comment line, for example ": portal {"event":"queued","position":2}".</summary>
    public static string ProgressComment(PortalProgressEvent e) =>
        ProgressEvents.CommentPrefix + JsonSerializer.Serialize(e, AiHostJsonContext.Default.PortalProgressEvent);

    /// <summary>Parses a progress comment line; returns null for any other comment.</summary>
    public static PortalProgressEvent? TryParseProgressComment(string line)
    {
        if (!line.StartsWith(ProgressEvents.CommentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line.AsSpan(ProgressEvents.CommentPrefix.Length),
                AiHostJsonContext.Default.PortalProgressEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
