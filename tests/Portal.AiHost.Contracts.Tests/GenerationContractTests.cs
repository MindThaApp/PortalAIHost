using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Contracts.Tests;

/// <summary>The image and video job DTOs, read from the JSON shown in api-v1.md and written again.</summary>
public sealed class GenerationContractTests
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
    public void ImageRequest() => RoundTrip("""
        { "model": "sd-1.5-q8", "prompt": "a lighthouse at dusk, oil painting", "negative_prompt": "blurry",
          "width": 512, "height": 512, "steps": 20, "seed": 42, "cfg_scale": 7.5, "sampler": "euler_a" }
        """, Ctx.GenerationJobRequest);

    [Fact]
    public void VideoRequest() => RoundTrip("""
        { "model": "wan2.1-t2v-1.3b", "prompt": "waves on a beach at sunset", "width": 832, "height": 480,
          "frames": 33, "fps": 16, "steps": 20 }
        """, Ctx.GenerationJobRequest);

    [Fact]
    public void RunningJob() => RoundTrip("""
        { "id": "gen_6f1c2a9e4b7d4c0f9a51d3e8b2c47a10", "object": "generation.job", "kind": "video",
          "model": "wan2.1-t2v-1.3b", "state": "running", "stage": "sampling", "progress": 0.42, "step": 9, "steps": 20,
          "created_at": "2026-11-27T14:03:22+00:00", "started_at": "2026-11-27T14:03:25+00:00",
          "preview_url": "/host/video/jobs/gen_6f1c2a9e4b7d4c0f9a51d3e8b2c47a10/preview",
          "plan": { "backend": "vulkan", "gpu_bytes": 4047483904, "cpu_bytes": 6979321856, "on_cpu": ["text_encoder"] } }
        """, Ctx.GenerationJob);

    [Fact]
    public void FinishedJob() => RoundTrip("""
        { "id": "gen_0c4b8e2f9a1d4c7b8e3f2a1b0c9d8e7f", "object": "generation.job", "kind": "image", "model": "sd-1.5-q8",
          "state": "succeeded", "progress": 1, "created_at": "2026-11-27T14:03:22+00:00",
          "started_at": "2026-11-27T14:03:23+00:00", "finished_at": "2026-11-27T14:03:41+00:00",
          "plan": { "backend": "vulkan", "gpu_bytes": 3221225472, "cpu_bytes": 536870912, "on_cpu": [] },
          "result": { "url": "/host/image/jobs/gen_0c4b8e2f9a1d4c7b8e3f2a1b0c9d8e7f/result", "content_type": "image/png",
                      "file_name": "20261127-140322-gen_0c4b8e2f9a1d4c7b8e3f2a1b0c9d8e7f.png", "bytes": 412345,
                      "width": 512, "height": 512, "seed": 42 } }
        """, Ctx.GenerationJob);

    [Fact]
    public void FailedJobCarriesTheErrorBody() => RoundTrip("""
        { "id": "gen_1a2b3c4d5e6f708192a3b4c5d6e7f809", "object": "generation.job", "kind": "video", "model": "wan2.1-t2v-1.3b",
          "state": "failed", "progress": 0.08, "created_at": "2026-11-27T14:03:22+00:00",
          "finished_at": "2026-11-27T14:03:30+00:00",
          "error": { "message": "The model cannot run in the memory available now.", "type": "unavailable",
                     "code": "insufficient_memory", "param": null } }
        """, Ctx.GenerationJob);

    [Fact]
    public void CodesStatesAndRoutes()
    {
        Assert.Equal(503, ErrorCatalog.Get(ErrorCodes.EngineMissing).HttpStatus);
        Assert.False(ErrorCatalog.Get(ErrorCodes.EngineMissing).Retryable);
        Assert.Equal(404, ErrorCatalog.Get(ErrorCodes.ModelNotInstalled).HttpStatus);
        Assert.True(GenerationJobStates.IsFinal(GenerationJobStates.Cancelled));
        Assert.False(GenerationJobStates.IsFinal(GenerationJobStates.Running));
        Assert.Equal("/host/image/jobs", Routes.JobsFor(ModelKinds.Image));
        Assert.Equal("/host/video/jobs", Routes.JobsFor(ModelKinds.Video));
        Assert.Null(Routes.JobsFor(ModelKinds.Chat));
    }
}
