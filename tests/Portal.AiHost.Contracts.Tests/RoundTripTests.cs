using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Contracts.Tests;

/// <summary>
/// Every DTO is read from the JSON shown in api-v1.md (or a representative control message),
/// written again, and compared as JSON. A mismatch means the contract drifted from the document.
/// </summary>
public class RoundTripTests
{
    private static readonly AiHostJsonContext Ctx = AiHostJsonContext.Default;

    private static void RoundTrip<T>(string json, JsonTypeInfo<T> info)
    {
        var value = JsonSerializer.Deserialize(json, info);
        Assert.NotNull(value);
        var written = JsonSerializer.Serialize(value, info);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(written)),
            $"Round trip changed the JSON.\nExpected: {json}\nActual:   {written}");
    }

    [Fact]
    public void DiscoveryFile() => RoundTrip("""
        { "schema": 1, "service": "portal-ai-host", "api_version": "1", "base_url": "http://127.0.0.1:41870",
          "port": 41870, "pid": 18244, "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
          "host_version": "1.0.0", "started_at": "2026-11-20T21:04:11+00:00" }
        """, Ctx.DiscoveryFile);

    [Fact]
    public void Ping() => RoundTrip("""
        { "service": "portal-ai-host", "api_version": "1", "host_version": "1.0.0",
          "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10" }
        """, Ctx.PingResponse);

    [Fact]
    public void AuthRequestCreate() => RoundTrip("""
        { "app_id": "com.mindthaapp.voiceportal", "app_name": "Voice Portal", "app_version": "1.3.0",
          "reason": "Meeting minutes and summaries" }
        """, Ctx.AuthRequestCreate);

    [Fact]
    public void AuthRequestCreated() => RoundTrip("""
        { "request_id": "ar_9d2f0c7b61e44a3f8b0e5c1a7d93e2f4", "status": "pending",
          "expires_at": "2026-11-20T21:14:11+00:00", "poll_wait_ms": 25000 }
        """, Ctx.AuthRequestCreated);

    [Fact]
    public void AuthRequestStatusApproved() => RoundTrip("""
        { "status": "approved", "token": "pah_3hYabc", "token_id": "tk_51c0e2", "app_id": "com.mindthaapp.voiceportal" }
        """, Ctx.AuthRequestStatus);

    [Fact]
    public void AuthRequestStatusPending() => RoundTrip("""{ "status": "pending" }""", Ctx.AuthRequestStatus);

    [Fact]
    public void HostStatus() => RoundTrip("""
        {
          "api_version": "1", "host_version": "1.0.0", "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
          "state": "ready",
          "client": { "app_id": "com.mindthaapp.voiceportal", "token_id": "tk_51c0e2", "approved_at": "2026-11-20T21:05:02+00:00", "paused": false, "priority": "normal" },
          "models": {
            "chat": { "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B", "state": "loaded",
                      "loaded_since": "2026-11-20T21:06:40+00:00", "load_progress": 1, "keep_loaded": false, "idle_unload_seconds": 600 },
            "embedding": { "id": "embed-small-q8", "display_name": "Small embeddings", "state": "unloaded",
                           "load_progress": 0, "keep_loaded": false, "idle_unload_seconds": 600 }
          },
          "queue": { "running": 1, "waiting": 3, "waiting_interactive": 1, "waiting_batch": 2, "yours_waiting": 0 },
          "hardware": { "os": "windows", "arch": "x64", "cpu_cores": 12, "ram_bytes": 68719476736,
            "gpus": [ { "name": "AMD Radeon RX 6600", "vendor": "amd", "vram_bytes": 8589934592, "backend": "vulkan" } ] },
          "upgrade_available": false,
          "sharing": { "lan": false, "relay": false }
        }
        """, Ctx.HostStatus);

    [Fact]
    public void HostStatusIgnoresUnknownModelKindsAndFields()
    {
        var status = JsonSerializer.Deserialize("""
            { "state": "idle", "future_field": 1,
              "models": { "transcription": { "state": "unloaded", "extra": true } } }
            """, Ctx.HostStatus);
        Assert.NotNull(status);
        Assert.Equal("unloaded", status.Models["transcription"].State);
    }

    [Fact]
    public void HostCapabilities() => RoundTrip("""
        {
          "api_version": "1", "capabilities_revision": 7,
          "chat": {
            "available": true,
            "model": { "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B", "family": "qwen3",
                       "params_total_b": 30.5, "params_active_b": 3.3, "quant": "Q4_K_M", "size_bytes": 18600000000 },
            "context_tokens": 32768, "context_tokens_max": 131072, "max_output_tokens": 8192, "parallel_slots": 2,
            "features": { "streaming": true, "system_prompt": true, "json_object": true, "json_schema": true,
                          "tools": true, "reasoning": true },
            "placement": { "backend": "vulkan", "gpu_offload": "partial", "gpu_layers": 48, "total_layers": 48,
                           "moe_expert_layers_on_cpu": 30 },
            "speed_estimate": { "prompt_tokens_per_second": 350, "generation_tokens_per_second": 22 }
          },
          "embeddings": {
            "available": true,
            "model": { "id": "embed-small-q8", "display_name": "Small embeddings" },
            "dimensions": 768, "max_input_tokens": 512, "max_batch_inputs": 64, "normalized": true
          },
          "limits": { "max_request_bytes": 4194304, "max_queued_per_app": 8,
                      "default_deadline_ms": { "interactive": 120000, "batch": 1800000 }, "max_deadline_ms": 3600000 },
          "endpoints": ["chat.completions", "embeddings", "models", "tokenize", "warmup"]
        }
        """, Ctx.HostCapabilities);

    [Fact]
    public void CapabilitiesUnavailableSection()
    {
        RoundTrip("""
            { "api_version": "1", "capabilities_revision": 1,
              "chat": { "available": false, "reason": "no_model" },
              "embeddings": { "available": false, "reason": "insufficient_memory" },
              "limits": { "max_request_bytes": 4194304, "max_queued_per_app": 8,
                          "default_deadline_ms": { "interactive": 120000, "batch": 1800000 }, "max_deadline_ms": 3600000 },
              "endpoints": [] }
            """, Ctx.HostCapabilities);
    }

    [Fact]
    public void ModelList() => RoundTrip("""
        { "object": "list", "data": [
          { "id": "qwen3-30b-a3b-q4km", "object": "model", "owned_by": "portal-ai-host", "kind": "chat",
            "active": true, "loaded": true, "context_tokens": 32768 },
          { "id": "embed-small-q8", "object": "model", "owned_by": "portal-ai-host", "kind": "embedding",
            "active": true, "loaded": false } ] }
        """, Ctx.ModelList);

    [Fact]
    public void Warmup()
    {
        RoundTrip("""{ "kind": "chat" }""", Ctx.WarmupRequest);
        RoundTrip("""{ "state": "loading", "load_progress": 0 }""", Ctx.WarmupResponse);
        RoundTrip("""{ "state": "loaded" }""", Ctx.WarmupResponse);
    }

    [Fact]
    public void Tokenize()
    {
        RoundTrip("""
            { "model": "default", "messages": [ { "role": "system", "content": "a" }, { "role": "user", "content": "b" } ] }
            """, Ctx.TokenizeRequest);
        RoundTrip("""{ "model": "default", "text": "plain text to count" }""", Ctx.TokenizeRequest);
        RoundTrip("""{ "tokens": 1534, "context_tokens": 32768 }""", Ctx.TokenizeResponse);
    }

    [Fact]
    public void ErrorEnvelopeMatchesDocumentExactly()
    {
        // The example from api-v1.md section 5, byte for byte apart from whitespace.
        const string doc = """
            { "error": { "message": "The model is loading. Try again shortly.", "type": "unavailable",
                         "code": "model_loading", "param": null,
                         "retry_after_ms": 3000, "details": { "progress": 0.42 } } }
            """;
        RoundTrip(doc, Ctx.ApiError);

        var built = ErrorCatalog.Create(
            ErrorCodes.ModelLoading, "The model is loading. Try again shortly.", retryAfterMs: 3000,
            details: new Dictionary<string, JsonElement> { ["progress"] = JsonSerializer.SerializeToElement(0.42) });
        Assert.Equal(
            """{"error":{"message":"The model is loading. Try again shortly.","type":"unavailable","code":"model_loading","param":null,"retry_after_ms":3000,"details":{"progress":0.42}}}""",
            JsonSerializer.Serialize(built, Ctx.ApiError));
    }

    [Fact]
    public void ErrorEnvelopeWithoutExtrasStillWritesParamNull()
    {
        var json = JsonSerializer.Serialize(
            ErrorCatalog.Create(ErrorCodes.NotFound, "Unknown route."), Ctx.ApiError);
        Assert.Equal(
            """{"error":{"message":"Unknown route.","type":"not_found_error","code":"not_found","param":null}}""", json);
    }

    [Fact]
    public void ProgressComments()
    {
        var line = AiHostJson.ProgressComment(new PortalProgressEvent { Event = ProgressEvents.Queued, Position = 2 });
        Assert.Equal("""
            : portal {"event":"queued","position":2}
            """.Trim(), line);
        var parsed = AiHostJson.TryParseProgressComment(line);
        Assert.Equal(2, parsed?.Position);
        Assert.Equal(0.42, AiHostJson.TryParseProgressComment(""": portal {"event":"loading","progress":0.42}""")?.Progress);
        Assert.Null(AiHostJson.TryParseProgressComment(": keep-alive"));
        Assert.Null(AiHostJson.TryParseProgressComment(": portal {not json"));
    }

    [Fact]
    public void ChatRequestFull() => RoundTrip("""
        { "model": "default",
          "messages": [
            { "role": "system", "content": "Be brief." },
            { "role": "user", "content": [ { "type": "text", "text": "Hello" }, { "type": "text", "text": "there" } ] },
            { "role": "assistant", "tool_calls": [
                { "id": "call_1", "type": "function", "function": { "name": "lookup", "arguments": "{\"q\":1}" } } ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "42" } ],
          "stream": true, "stream_options": { "include_usage": true },
          "max_tokens": 100, "max_completion_tokens": 90, "temperature": 0.2, "top_p": 0.9,
          "stop": ["END", "STOP"], "seed": 7, "presence_penalty": 0.1, "frequency_penalty": 0.2,
          "response_format": { "type": "json_schema", "json_schema": { "name": "r", "schema": { "type": "object" }, "strict": true } },
          "reasoning_effort": "low",
          "tools": [ { "type": "function", "function": { "name": "lookup", "description": "d",
                       "parameters": { "type": "object", "properties": { "q": { "type": "number" } } } } } ],
          "tool_choice": { "type": "function", "function": { "name": "lookup" } },
          "parallel_tool_calls": false, "n": 1, "user": "u1" }
        """, Ctx.ChatCompletionRequest);

    [Fact]
    public void ChatRequestStopAsSingleStringAndToolChoiceString() => RoundTrip("""
        { "model": "default", "messages": [ { "role": "user", "content": "hi" } ],
          "stop": "END", "tool_choice": "auto", "response_format": { "type": "json_object" } }
        """, Ctx.ChatCompletionRequest);

    [Fact]
    public void ChatContentAcceptsPartsAndText()
    {
        var req = JsonSerializer.Deserialize("""
            { "messages": [ { "role": "user", "content": [ { "type": "text", "text": "a" }, { "type": "text", "text": "b" } ] },
                            { "role": "user", "content": "c" } ] }
            """, Ctx.ChatCompletionRequest)!;
        Assert.Equal("ab", req.Messages[0].Content!.AsText());
        Assert.Equal("c", req.Messages[1].Content!.AsText());
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{ "messages": [ { "role": "user", "content": 5 } ] }""", Ctx.ChatCompletionRequest));
    }

    [Fact]
    public void ChatResponse() => RoundTrip("""
        { "id": "chatcmpl-1", "object": "chat.completion", "created": 1700000000, "model": "qwen3-30b-a3b-q4km",
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "Hi", "reasoning_content": "think" },
                         "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 5, "completion_tokens": 2, "total_tokens": 7 } }
        """, Ctx.ChatCompletionResponse);

    [Fact]
    public void ChatResponseToolCalls() => RoundTrip("""
        { "id": "chatcmpl-2", "object": "chat.completion", "created": 1, "model": "m",
          "choices": [ { "index": 0, "message": { "role": "assistant",
              "tool_calls": [ { "id": "c1", "type": "function", "function": { "name": "f", "arguments": "{}" } } ] },
              "finish_reason": "tool_calls" } ] }
        """, Ctx.ChatCompletionResponse);

    [Fact]
    public void ChatChunks()
    {
        RoundTrip("""
            { "id": "c", "object": "chat.completion.chunk", "created": 1, "model": "m",
              "choices": [ { "index": 0, "delta": { "role": "assistant", "content": "" }, "finish_reason": null } ] }
            """, Ctx.ChatCompletionChunk);
        RoundTrip("""
            { "id": "c", "object": "chat.completion.chunk", "created": 1, "model": "m",
              "choices": [ { "index": 0, "delta": { "tool_calls": [ { "index": 0, "id": "c1", "type": "function",
                  "function": { "name": "f", "arguments": "{\"a\"" } } ] }, "finish_reason": null } ] }
            """, Ctx.ChatCompletionChunk);
        RoundTrip("""
            { "id": "c", "object": "chat.completion.chunk", "created": 1, "model": "m",
              "choices": [ { "index": 0, "delta": {}, "finish_reason": "stop" } ] }
            """, Ctx.ChatCompletionChunk);
        RoundTrip("""
            { "id": "c", "object": "chat.completion.chunk", "created": 1, "model": "m", "choices": [],
              "usage": { "prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3 } }
            """, Ctx.ChatCompletionChunk);
    }

    [Fact]
    public void Embeddings()
    {
        RoundTrip("""{ "model": "default-embedding", "input": "text", "encoding_format": "float" }""", Ctx.EmbeddingsRequest);
        RoundTrip("""{ "model": "default-embedding", "input": ["a", "b"], "encoding_format": "base64" }""", Ctx.EmbeddingsRequest);
        RoundTrip("""
            { "object": "list", "model": "embed-small-q8",
              "data": [ { "object": "embedding", "index": 0, "embedding": [0.5, -0.25, 0.125] } ],
              "usage": { "prompt_tokens": 3, "total_tokens": 3 } }
            """, Ctx.EmbeddingsResponse);
        RoundTrip("""
            { "object": "list", "model": "m",
              "data": [ { "object": "embedding", "index": 0, "embedding": "AAAAPwAAgL4=" } ] }
            """, Ctx.EmbeddingsResponse);
    }

    [Fact]
    public void EmbeddingVectorBase64MatchesFloats()
    {
        var floats = new[] { 0.5f, -0.25f, 3f };
        var b64 = EmbeddingVector.FromFloatsAsBase64(floats);
        Assert.Equal(floats, b64.ToFloats());
        Assert.Equal(floats, EmbeddingVector.FromFloats(floats).ToFloats());
    }

    [Fact]
    public void ControlEnvelopeAndPayloads()
    {
        RoundTrip("""{ "id": "1", "type": "approvals.approve", "payload": { "request_id": "ar_1" } }""", Ctx.ControlEnvelope);
        RoundTrip("""{ "type": "event.engine_state", "payload": { "kind": "chat", "state": "loading", "progress": 0.5 } }""", Ctx.ControlEnvelope);
        RoundTrip("""{ "page": "models" }""", Ctx.ShowRequest);
        RoundTrip("""{ "code": "internal_error", "message": "x" }""", Ctx.ControlError);
        RoundTrip("""
            { "pending": [ { "request_id": "ar_1", "app_id": "a", "app_name": "A", "reason": "r",
              "caller_identity": "MindThaApp.VoicePortal", "caller_verified": true, "caller_portal_app": true, "expires_at": "2026-11-20T21:14:11+00:00" } ] }
            """, Ctx.ApprovalList);
        RoundTrip("""{ "request_id": "ar_1" }""", Ctx.ApprovalDecision);
        RoundTrip("""
            { "clients": [ { "token_id": "tk_1", "app_id": "a", "app_name": "A", "device_id": "local",
              "created_at": "2026-11-20T21:14:11+00:00", "revoked": false, "paused": false, "priority": "normal" } ] }
            """, Ctx.ClientList);
        RoundTrip("""{ "token_id": "tk_1" }""", Ctx.ClientRevoke);
        RoundTrip("""{ "app_id": "a.b", "paused": true, "priority": "low" }""", Ctx.AppControlCommand);
        RoundTrip("""
            { "models": [ { "id": "m", "display_name": "M", "kind": "chat", "engine": "llama-server", "size_bytes": 10,
              "quality": 4.5, "recommended": true, "installed": false, "active": false, "fits_this_pc": true,
              "existing_copy": false, "download_state": "paused", "bytes_done": 4, "reason": "fits" } ],
              "models_folder": "D:/Models/Portal AI Host Models", "free_bytes": 1000, "catalogue_available": true }
            """, Ctx.ModelListing);
        // A listing without the field (an older host) reads as "catalogue available".
        Assert.True(System.Text.Json.JsonSerializer.Deserialize("""{ "models": [] }""", Ctx.ModelListing)!.CatalogueAvailable);
        RoundTrip("""{ "catalogue_id": "m" }""", Ctx.DownloadCommand);
        RoundTrip("""{ "catalogue_id": "m" }""", Ctx.ActivateModelCommand);
        RoundTrip("""{ "catalogue_id": "m", "state": "downloading", "bytes_done": 5, "bytes_total": 10, "eta_seconds": 3.5 }""", Ctx.DownloadProgressEvent);
        RoundTrip("""{ "kind": "chat", "state": "ready", "progress": 1 }""", Ctx.EngineStateEvent);
        RoundTrip("""
            { "mode": "server", "fallback_prompt_shown": false, "relay_mode": "auto", "start_with_windows": true, "backend": "auto", "max_memory_percent": 80, "preset": "balanced", "compute": "auto", "priority": "low", "cpu_ceiling_percent": 100, "ease_off": ["fullscreen", "battery", "app_busy"], "prevent_sleep_while_downloading": true, "send_crash_reports": true,
              "remote": { "enabled": false, "port": 48123, "relay": true, "relay_admission": false, "allow_replace_models": false, "gaming_mode": false },
              "kinds": { "chat": { "keep_loaded": false, "idle_unload_seconds": 600 } } }
            """, Ctx.HostSettings);
        RoundTrip("""{ "enabled": true, "listening": true, "port": 48123, "host_name": "Desk PC", "addresses": ["192.168.1.20"], "devices": [ { "device_id": "dev_a", "name": "Laptop", "created_at": "2026-11-24T10:00:00+00:00", "model_control": false, "paused": false, "paused_apps": [], "priority_equal": false, "allowed_models": [], "status": "offline", "apps": [] } ], "relay": "off", "allow_replace_models": false, "relay_admission": false, "gaming_busy": false, "loaded_models": [] }""", Ctx.RemoteStatus);
        RoundTrip("""{ "code": "042917", "expires_at": "2026-11-24T10:05:00+00:00", "qr_text": "portalai://pair?h=1", "link_text": "", "host_name": "Desk PC", "address": "192.168.1.20", "port": 48123, "fingerprint": "abc" }""", Ctx.PairingOfferInfo);
        RoundTrip("""{ "client_nonce": "a", "device_name": "Laptop", "device_public_key": "b", "mac": "c" }""", Ctx.PairStartRequest);
        RoundTrip("""{ "host_nonce": "a", "fingerprint": "b", "host_name": "Desk PC", "mac": "c" }""", Ctx.PairStartResponse);
        RoundTrip("""{ "host_nonce": "a", "mac": "c" }""", Ctx.PairFinishRequest);
        RoundTrip("""{ "device_id": "dev_a", "mac": "c" }""", Ctx.PairFinishResponse);
        RoundTrip("""{ "request": { "model": "default", "messages": [ { "role": "user", "content": "hi" } ] } }""", Ctx.TryItRequest);
    }

    [Fact]
    public void ModelManagementMessages()
    {
        RoundTrip("""{ "catalogue_id": "m", "switch_to_id": "n" }""", Ctx.RemoveModelCommand);
        RoundTrip("""{ "catalogue_id": "m" }""", Ctx.RemoveModelCommand);
        RoundTrip("""{ "catalogue_id": "m" }""", Ctx.VerifyModelCommand);
        RoundTrip("""{ "path": "C:/Models/my.gguf" }""", Ctx.ImportModelCommand);
        RoundTrip("""{ "parent_folder": "D:/Models" }""", Ctx.MoveStoreCommand);
        RoundTrip("""{ "path": "D:/Models/Portal AI Host Models" }""", Ctx.StoreFolderInfo);
        RoundTrip("""
            { "items": [ { "id": "p1", "kind": "partial", "label": "m.gguf", "detail": "d", "bytes": 5, "file_count": 1 } ], "total_bytes": 5, "busy_reason": "b" }
            """, Ctx.CleanupScan);
        RoundTrip("""{ "item_ids": [ "p1", "p2" ] }""", Ctx.CleanupCommand);
        RoundTrip("""{ "freed_bytes": 5, "removed": [ "m.gguf" ], "skipped": [ { "label": "x", "reason": "y" } ] }""", Ctx.CleanupResult);
        RoundTrip("""
            { "operation": "move", "state": "done", "fraction": 1, "bytes_done": 10, "bytes_total": 10,
              "message": "Your models were moved.", "left_behind": [ "D:/old/a.gguf" ] }
            """, Ctx.StoreProgressEvent);
        RoundTrip("""{ "operation": "verify", "state": "damaged", "catalogue_id": "m", "fraction": 1, "bytes_done": 0, "bytes_total": 0, "message": "x does not match the model list." }""",
            Ctx.StoreProgressEvent);
        RoundTrip("""
            { "models": [ { "id": "m", "display_name": "M", "kind": "chat", "engine": "llama-server", "size_bytes": 10,
              "quality": 4.5, "recommended": false, "installed": true, "active": false, "fits_this_pc": true,
              "existing_copy": false, "bytes_done": 0, "last_used_at": "2026-11-20T10:00:00+00:00", "damaged": true,
              "damaged_reason": "m.gguf does not match the model list.", "advice_summary": "Good all-rounder.",
              "good_for": [ "everyday chat" ], "caveats": [ "slow without a graphics card" ], "memory_needed_bytes": 5000,
              "licence_name": "apache-2.0", "licence_url": "https://example.invalid/licence" } ],
              "models_folder": "D:/Models", "free_bytes": 1000, "models_bytes": 10, "catalogue_available": true }
            """, Ctx.ModelListing);
        Assert.Equal("store.open_folder", ControlTypes.OpenStoreFolder);
        Assert.Equal("store.move", ControlTypes.MoveStore);
        Assert.Equal("store.cancel", ControlTypes.CancelStoreOperation);
        Assert.Equal("models.verify", ControlTypes.VerifyModel);
        Assert.Equal("event.store_progress", ControlTypes.EventStoreProgress);
    }

    [Fact]
    public void MissingFieldsKeepTheirDefaults()
    {
        var s = JsonSerializer.Deserialize("""{ "start_with_windows": true }""", Ctx.HostSettings)!;
        Assert.Equal(80, s.MaxMemoryPercent);
        Assert.True(s.PreventSleepWhileDownloading);
        Assert.Equal("auto", s.Backend);
        Assert.NotNull(s.Kinds);
        var k = JsonSerializer.Deserialize("""{ "keep_loaded": true }""", Ctx.KindSettings)!;
        Assert.Equal(600, k.IdleUnloadSeconds);
        var l = JsonSerializer.Deserialize("""{ }""", Ctx.ModelListing)!;
        Assert.NotNull(l.Models);
    }

    [Fact]
    public void EveryErrorCodeInTheDocumentIsCatalogued()
    {
        string[] documented =
        [
            "invalid_request", "unsupported_parameter", "context_length_exceeded", "auth_required", "token_invalid",
            "token_revoked", "access_denied", "app_paused", "forbidden_origin", "not_found", "feature_disabled", "request_too_large",
            "queue_full", "rate_limited", "no_model", "model_loading", "engine_restarting", "insufficient_memory",
            "host_stopping", "auth_unavailable", "remote_unreachable", "device_unauthorized", "pairing_failed", "model_control_required", "model_conflict", "model_not_found", "device_paused", "device_quiet_hours", "host_busy_gaming", "device_limit_reached", "deadline_exceeded", "consent_required", "unsupported_audio", "voice_not_found", "voice_in_use", "engine_error", "internal_error", "engine_missing", "model_not_installed",
        ];
        Assert.Equal(documented.Order(), ErrorCatalog.AllCodes.Order());
        Assert.Equal(400, ErrorCatalog.Get(ErrorCodes.InvalidRequest).HttpStatus);
        Assert.Equal(401, ErrorCatalog.Get(ErrorCodes.TokenRevoked).HttpStatus);
        Assert.Equal(403, ErrorCatalog.Get(ErrorCodes.ForbiddenOrigin).HttpStatus);
        Assert.Equal(404, ErrorCatalog.Get(ErrorCodes.FeatureDisabled).HttpStatus);
        Assert.Equal(413, ErrorCatalog.Get(ErrorCodes.RequestTooLarge).HttpStatus);
        Assert.Equal(429, ErrorCatalog.Get(ErrorCodes.QueueFull).HttpStatus);
        Assert.Equal(503, ErrorCatalog.Get(ErrorCodes.HostStopping).HttpStatus);
        Assert.Equal(504, ErrorCatalog.Get(ErrorCodes.DeadlineExceeded).HttpStatus);
        Assert.Equal(500, ErrorCatalog.Get(ErrorCodes.EngineError).HttpStatus);
        Assert.True(ErrorCatalog.Get(ErrorCodes.QueueFull).Retryable);
        Assert.False(ErrorCatalog.Get(ErrorCodes.NoModel).Retryable);
    }

    [Fact]
    public void HeaderNamesMatchDocument()
    {
        Assert.Equal("X-Portal-Priority", HeaderNames.Priority);
        Assert.Equal("X-Portal-Deadline-Ms", HeaderNames.DeadlineMs);
        Assert.Equal("X-Portal-Load", HeaderNames.Load);
        Assert.Equal("X-Portal-Client-Request-Id", HeaderNames.ClientRequestId);
        Assert.Equal("X-Portal-Request-Id", HeaderNames.RequestId);
        Assert.Equal("X-Portal-Queue-Ms", HeaderNames.QueueMs);
        Assert.Equal("X-Portal-Model", HeaderNames.Model);
        Assert.Equal("X-Portal-Capabilities-Revision", HeaderNames.CapabilitiesRevision);
    }

    [Fact]
    public void ModelControlMessages()
    {
        RoundTrip("""{ "device_id": "dev_a", "enabled": true }""", Ctx.SetModelControlCommand);
        RoundTrip("""{ "kind": "chat" }""", Ctx.TakeBackCommand);
        RoundTrip("""{ "device_id": "dev_a", "app": "Notes", "paused": true }""", Ctx.PauseDeviceCommand);
        RoundTrip("""{ "device_id": "dev_a", "priority_equal": true, "max_requests_per_day": 5, "max_tokens_per_day": 9, "quiet_start": "22:00", "quiet_end": "07:00", "allowed_models": ["m"] }""", Ctx.SetDeviceSettingsCommand);
        RoundTrip("""{ "device_id": "dev_a", "name": "Den" }""", Ctx.RenameDeviceCommand);
        RoundTrip("""{ "kind": "paired", "device_id": "dev_a", "device_name": "Den" }""", Ctx.DeviceAlert);
        RoundTrip("""{ "model_id": "m", "placement": "gpu" }""", Ctx.DeviceLoadRequest);
        RoundTrip("""{ "model_id": "m" }""", Ctx.DeviceUnloadRequest);
        RoundTrip("""{ "model_id": "m", "compute": "cpu", "gpu_memory_limit_bytes": 1, "memory_limit_bytes": 2, "cpu_threads": 3 }""", Ctx.DevicePlacementRequest);
        RoundTrip("""{ "models": [ { "id": "m", "display_name": "M", "kind": "chat", "size_bytes": 5, "fits_this_pc": true, "state": "loaded", "placement": "auto", "loaded_by": "pc" } ] }""", Ctx.DeviceModelList);
        Assert.Equal(403, ErrorCatalog.Get(ErrorCodes.ModelControlRequired).HttpStatus);
        Assert.Equal(409, ErrorCatalog.Get(ErrorCodes.ModelConflict).HttpStatus);
    }
}
