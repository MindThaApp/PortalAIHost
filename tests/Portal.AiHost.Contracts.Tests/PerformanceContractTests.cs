using System.Text.Json;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Contracts.Tests;

public class PerformanceContractTests
{
    private static readonly AiHostJsonContext Ctx = AiHostJsonContext.Default;

    [Fact]
    public void OlderSettingsFilesKeepWorkingAndGetTheNewDefaults()
    {
        var s = JsonSerializer.Deserialize("""{ "max_memory_percent": 60, "backend": "cpu" }""", Ctx.HostSettings)!;
        Assert.Equal(60, s.MaxMemoryPercent);
        Assert.Equal("balanced", s.Preset);
        Assert.Null(s.MemoryLimitBytes);
        Assert.Null(s.KeepFreeBytes);
        Assert.Equal("auto", s.Compute);
        Assert.Null(s.GpuMemoryLimitBytes);
        Assert.Null(s.CpuThreads);
        Assert.Equal("low", s.Priority);
    }

    [Fact]
    public void NewSettingsRoundTripUnderTheSharedKeyNames()
    {
        var json = """
            { "preset": "custom", "memory_limit_bytes": 21474836480, "keep_free_bytes": 8589934592, "compute": "gpu",
              "gpu_memory_limit_bytes": 6442450944, "cpu_threads": 6, "priority": "normal" }
            """;
        var s = JsonSerializer.Deserialize(json, Ctx.HostSettings)!;
        Assert.Equal("custom", s.Preset);
        Assert.Equal(21474836480, s.MemoryLimitBytes);
        Assert.Equal(8589934592, s.KeepFreeBytes);
        Assert.Equal("gpu", s.Compute);
        Assert.Equal(6442450944, s.GpuMemoryLimitBytes);
        Assert.Equal(6, s.CpuThreads);
        Assert.Equal("normal", s.Priority);

        var back = JsonNode.Parse(JsonSerializer.Serialize(s, Ctx.HostSettings))!.AsObject();
        foreach (var key in new[] { "preset", "memory_limit_bytes", "keep_free_bytes", "compute", "gpu_memory_limit_bytes", "cpu_threads", "priority" })
        {
            Assert.True(back.ContainsKey(key), key);
        }
    }

    [Fact]
    public void PerformanceInfoMatchesTheDocumentedShape()
    {
        var json = """
            {
              "preset": "balanced", "memory_limit_bytes": 54975581388, "keep_free_bytes": 8589934592,
              "compute": "auto", "compute_effective": "gpu", "gpu_memory_limit_bytes": 8053063680,
              "cpu_threads": 10, "priority": "low", "cpu_ceiling_percent": 100, "ease_off": [],
              "eased_off_now": null, "fit": "fits_partial", "memory_cap_active": true,
              "ranges": {
                "memory_limit_bytes": { "min": 2147483648, "max": 60129542144, "step": 1073741824 },
                "keep_free_bytes": { "min": 2147483648, "max": 34359738368, "step": 1073741824 },
                "cpu_threads": { "min": 1, "max": 11 },
                "gpu_available": true
              },
              "settings_link": "portal-ai-host:settings/performance"
            }
            """;
        var value = JsonSerializer.Deserialize(json, Ctx.PerformanceInfo)!;
        var written = JsonNode.Parse(JsonSerializer.Serialize(value, Ctx.PerformanceInfo))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), written), written.ToJsonString());
    }

    [Fact]
    public void ControlAndHeaderNamesAreStable()
    {
        Assert.Equal("performance.get", ControlTypes.GetPerformance);
        Assert.Equal("X-Portal-Context-Tokens", HeaderNames.ContextTokens);
        Assert.Equal("/host/performance", Routes.Performance);
        Assert.Equal("performance", EndpointNames.Performance);
    }
}
