using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

/// <summary>
/// Each api-v1.md example, checked against what the real HTTP adapter returns: every documented field is present
/// with the same JSON kind (values come from the fake engine, so they differ).
/// </summary>
public sealed class GoldenTests
{
    private const string Ping = """
        { "service": "portal-ai-host", "api_version": "1", "host_version": "1.0.0",
          "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10" }
        """;

    private const string Status = """
        { "api_version": "1", "host_version": "1.0.0", "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
          "state": "ready",
          "client": { "app_id": "com.mindthaapp.voiceportal", "token_id": "tk_51c0e2", "approved_at": "2026-11-20T21:05:02Z" },
          "models": {
            "chat": { "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B", "state": "loaded",
                      "loaded_since": "2026-11-20T21:06:40Z", "load_progress": 1.0, "keep_loaded": false,
                      "idle_unload_seconds": 600 },
            "embedding": { "id": "embed-small-q8", "display_name": "Small embeddings", "state": "unloaded",
                           "load_progress": 0.0, "keep_loaded": false, "idle_unload_seconds": 600 } },
          "queue": { "running": 1, "waiting": 3, "waiting_interactive": 1, "waiting_batch": 2, "yours_waiting": 0 },
          "hardware": { "os": "windows", "arch": "x64", "cpu_cores": 12, "ram_bytes": 68719476736,
            "gpus": [ { "name": "AMD Radeon RX 6600", "vendor": "amd", "vram_bytes": 8589934592, "backend": "vulkan" } ] },
          "upgrade_available": false, "sharing": { "lan": false, "relay": false } }
        """;

    private const string Capabilities = """
        { "api_version": "1", "capabilities_revision": 7,
          "chat": { "available": true,
            "model": { "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B", "family": "qwen3",
                       "params_total_b": 30.5, "params_active_b": 3.3, "quant": "Q4_K_M", "size_bytes": 18600000000 },
            "context_tokens": 32768, "context_tokens_max": 131072, "max_output_tokens": 8192, "parallel_slots": 2,
            "features": { "streaming": true, "system_prompt": true, "json_object": true, "json_schema": true,
                          "tools": true, "reasoning": true },
            "placement": { "backend": "vulkan", "gpu_offload": "partial", "gpu_layers": 48, "total_layers": 48,
                           "moe_expert_layers_on_cpu": 30 },
            "speed_estimate": { "prompt_tokens_per_second": 350, "generation_tokens_per_second": 22 } },
          "embeddings": { "available": true, "model": { "id": "embed-small-q8", "display_name": "Small embeddings" },
            "dimensions": 768, "max_input_tokens": 512, "max_batch_inputs": 64, "normalized": true },
          "limits": { "max_request_bytes": 4194304, "max_queued_per_app": 8,
            "default_deadline_ms": { "interactive": 120000, "batch": 1800000 }, "max_deadline_ms": 3600000 },
          "endpoints": ["chat.completions", "embeddings", "models", "tokenize", "warmup"] }
        """;

    private const string Models = """
        { "object": "list", "data": [
          { "id": "qwen3-30b-a3b-q4km", "object": "model", "owned_by": "portal-ai-host", "kind": "chat",
            "active": true, "loaded": true, "context_tokens": 32768 },
          { "id": "embed-small-q8", "object": "model", "owned_by": "portal-ai-host", "kind": "embedding",
            "active": true, "loaded": false } ] }
        """;

    private const string AuthCreated = """
        { "request_id": "ar_9d2f0c7b61e44a3f8b0e5c1a7d93e2f4", "status": "pending",
          "expires_at": "2026-11-20T21:14:11Z", "poll_wait_ms": 25000 }
        """;

    private const string AuthApproved = """
        { "status": "approved", "token": "pah_3hY", "token_id": "tk_51c0e2", "app_id": "com.mindthaapp.voiceportal" }
        """;

    private const string Error = """
        { "error": { "message": "The model is loading. Try again shortly.", "type": "unavailable",
                     "code": "model_loading", "param": null, "retry_after_ms": 3000, "details": { "progress": 0.42 } } }
        """;

    private const string Tokenize = """{ "tokens": 1534, "context_tokens": 32768 }""";

    private const string ChatResponse = """
        { "id": "x", "object": "chat.completion", "created": 1, "model": "m",
          "choices": [ { "index": 0, "message": { "role": "assistant", "content": "hi" }, "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 } }
        """;

    private const string EmbeddingsResponse = """
        { "object": "list", "data": [ { "object": "embedding", "index": 0, "embedding": [0.1] } ], "model": "m",
          "usage": { "prompt_tokens": 1, "total_tokens": 1 } }
        """;

    private const string Discovery = """
        { "schema": 1, "service": "portal-ai-host", "api_version": "1", "base_url": "http://127.0.0.1:41870",
          "port": 41870, "pid": 18244, "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10", "host_version": "1.0.0",
          "started_at": "2026-11-20T21:04:11Z" }
        """;

    // Documented fields the fake engine has no value for (no planner and no GPU in tests).
    private static readonly HashSet<string> NotFromFakeEngine = new(StringComparer.Ordinal)
    {
        "$.chat.model.family", "$.chat.model.params_total_b", "$.chat.model.params_active_b", "$.chat.model.quant",
        "$.chat.model.size_bytes", "$.chat.placement.moe_expert_layers_on_cpu", "$.chat.speed_estimate",
        
    };

    [Fact]
    public async Task Every_example_matches_the_live_response()
    {
        await using var host = await TestHost.StartAsync();
        var token = await host.GetTokenAsync();

        AssertShape(Ping, await GetJson(host, Routes.Ping, null));
        AssertShape(Discovery, File.ReadAllText(host.DiscoveryPath));

        // Load the chat model first so status and models show it loaded.
        using (var chat = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token,
            """{ "model": "default", "messages": [ { "role": "user", "content": "Hello" } ] }""")))
        {
            Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
            Assert.Equal("qwen3-30b-a3b-q4km", chat.Headers.GetValues(HeaderNames.Model).Single());
            Assert.True(chat.Headers.Contains(HeaderNames.RequestId));
            Assert.True(chat.Headers.Contains(HeaderNames.QueueMs));
            Assert.True(chat.Headers.Contains(HeaderNames.CapabilitiesRevision));
            AssertShape(ChatResponse, await chat.Content.ReadAsStringAsync());
        }

        AssertShape(Status, await GetJson(host, Routes.Status, token));
        AssertShape(Capabilities, await GetJson(host, Routes.Capabilities, token));
        AssertShape(Models, await GetJson(host, Routes.Models, token));

        using (var tok = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.Tokenize, token,
            """{ "model": "default", "text": "plain text to count" }""")))
        {
            AssertShape(Tokenize, await tok.Content.ReadAsStringAsync());
        }

        using (var emb = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.Embeddings, token,
            """{ "model": "default-embedding", "input": ["a", "b"] }""")))
        {
            Assert.Equal(HttpStatusCode.OK, emb.StatusCode);
            AssertShape(EmbeddingsResponse, await emb.Content.ReadAsStringAsync());
        }

        using (var warm = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.Warmup, token, """{ "kind": "chat" }""")))
        {
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            Assert.Equal("""{"state":"loaded"}""", await warm.Content.ReadAsStringAsync());
        }

        using (var bye = await host.Http.SendAsync(host.Authed(HttpMethod.Delete, Routes.AuthToken, token)))
        {
            Assert.Equal(HttpStatusCode.NoContent, bye.StatusCode);
        }

        using var revoked = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Status, token));
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Contains("token_revoked", await revoked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_examples_match()
    {
        await using var host = await TestHost.StartAsync();
        using var create = await host.Http.PostAsync(Routes.AuthRequests, TestHost.Json(
            """{ "app_id": "com.mindthaapp.voiceportal", "app_name": "Voice Portal" }"""));
        var created = await create.Content.ReadAsStringAsync();
        AssertShape(AuthCreated, created);
        var id = JsonDocument.Parse(created).RootElement.GetProperty("request_id").GetString()!;
        await host.Registry.ApproveAsync(id, default);
        using var poll = await host.Http.GetAsync($"{Routes.AuthRequests}/{id}?wait_ms=0");
        AssertShape(AuthApproved, await poll.Content.ReadAsStringAsync());
        Assert.True(host.Resolver.Calls >= 2, "Every request, /host/auth included, carries the caller identity.");
    }

    [Fact]
    public async Task Model_loading_error_matches_the_error_example()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { LoadDelay = TimeSpan.FromSeconds(3) });
        var token = await host.GetTokenAsync();
        var msg = host.Authed(HttpMethod.Post, Routes.ChatCompletions, token,
            """{ "messages": [ { "role": "user", "content": "Hi" } ] }""");
        msg.Headers.Add(HeaderNames.Load, "fail");
        using var response = await host.Http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues(HeaderNames.RetryAfter).Single());
        AssertShape(Error, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_model_is_503_with_kind()
    {
        await using var host = await TestHost.StartAsync(withModels: false);
        var token = await host.GetTokenAsync();
        using var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.Embeddings, token,
            """{ "input": "x" }"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("no_model", error.GetProperty("code").GetString());
        Assert.Equal("embedding", error.GetProperty("details").GetProperty("kind").GetString());
        var caps = JsonDocument.Parse(await GetJson(host, Routes.Capabilities, token)).RootElement;
        Assert.False(caps.GetProperty("chat").GetProperty("available").GetBoolean());
        Assert.Equal("no_model", caps.GetProperty("chat").GetProperty("reason").GetString());
    }

    private static async Task<string> GetJson(TestHost host, string path, string? token)
    {
        using var msg = token is null ? new HttpRequestMessage(HttpMethod.Get, path) : host.Authed(HttpMethod.Get, path, token);
        using var response = await host.Http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    internal static void AssertShape(string expected, string actual)
    {
        using var e = JsonDocument.Parse(expected);
        using var a = JsonDocument.Parse(actual);
        Compare(e.RootElement, a.RootElement, "$");
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var kindE = expected.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : expected.ValueKind;
        var kindA = actual.ValueKind is JsonValueKind.True or JsonValueKind.False ? JsonValueKind.True : actual.ValueKind;
        Assert.True(kindE == kindA, $"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in expected.EnumerateObject())
            {
                var child = path + "." + p.Name;
                if (!actual.TryGetProperty(p.Name, out var value))
                {
                    Assert.True(NotFromFakeEngine.Contains(child) || p.Value.ValueKind == JsonValueKind.Null,
                        $"{child} is missing");
                    continue;
                }

                Compare(p.Value, value, child);
            }
        }
        else if (expected.ValueKind == JsonValueKind.Array && expected.GetArrayLength() > 0 && actual.GetArrayLength() > 0)
        {
            Compare(expected[0], actual[0], path + "[0]");
        }
    }
}
