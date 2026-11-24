using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Server.Tests;

/// <summary>The ease-off rules, the lease endpoints and the processor ceiling on the fake engines.</summary>
public sealed class EaseOffTests
{
    private sealed class Sensors : IEaseOffSensors
    {
        public volatile bool Fullscreen;
        public volatile bool Gpu;
        public volatile bool Battery;
        public bool IsFullscreen() => Fullscreen;
        public bool IsGpuBusy() => Gpu;
        public bool IsOnBattery() => Battery;
    }

    private static async Task<JsonElement> PerfAsync(TestHost host, string token)
    {
        using var r = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Performance, token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task A_lease_eases_the_engines_off_and_delete_puts_them_back()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        var token = await host.GetTokenAsync();
        Assert.Equal(ProcessPriorityClass.BelowNormal, host.Chat.RequestedPriority);
        Assert.Equal(100, host.Chat.RequestedCpuCeiling);

        using var set = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.EaseOff, token,
            """{ "reason": "live_audio", "ttl_ms": 30000, "reserved_bytes": 1073741824 }"""));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("app_busy", (await PerfAsync(host, token)).GetProperty("eased_off_now").GetString());
        Assert.Equal(ProcessPriorityClass.Idle, host.Chat.RequestedPriority);
        Assert.Equal(ProcessPriorityClass.Idle, host.Embed.RequestedPriority);
        Assert.Equal(PerformanceModel.EasedOffCeilingPercent, host.Chat.RequestedCpuCeiling);

        using var del = await host.Http.SendAsync(host.Authed(HttpMethod.Delete, Routes.EaseOff, token));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var root = await PerfAsync(host, token);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eased_off_now").ValueKind);
        Assert.Equal(ProcessPriorityClass.BelowNormal, host.Chat.RequestedPriority);
        Assert.Equal(100, host.Chat.RequestedCpuCeiling);
    }

    [Fact]
    public async Task A_lease_that_is_not_renewed_ends_on_its_own()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        var token = await host.GetTokenAsync();
        using var set = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.EaseOff, token,
            """{ "reason": "live_audio", "ttl_ms": 1000 }"""));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("app_busy", (await PerfAsync(host, token)).GetProperty("eased_off_now").GetString());
        await Task.Delay(1300);
        Assert.Equal(JsonValueKind.Null, (await PerfAsync(host, token)).GetProperty("eased_off_now").ValueKind);
        Assert.Equal(ProcessPriorityClass.BelowNormal, host.Chat.RequestedPriority);
    }

    [Fact]
    public async Task Delete_only_ends_the_callers_own_request()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        var a = await host.GetTokenAsync("com.mindthaapp.voiceportal");
        var b = await host.GetTokenAsync("com.mindthaapp.otherapp");
        using var set = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.EaseOff, a,
            """{ "reason": "live_audio", "ttl_ms": 30000 }"""));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        using var del = await host.Http.SendAsync(host.Authed(HttpMethod.Delete, Routes.EaseOff, b));
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal("app_busy", (await PerfAsync(host, a)).GetProperty("eased_off_now").GetString());
    }

    [Fact]
    public async Task The_request_needs_a_token_and_a_time_to_live()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        using var anonymous = await host.Http.PostAsync(Routes.EaseOff, TestHost.Json("""{ "ttl_ms": 1000 }"""));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await host.GetTokenAsync();
        using var noTtl = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.EaseOff, token, """{ "reason": "x" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, noTtl.StatusCode);

        using var caps = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Capabilities, token));
        var endpoints = JsonDocument.Parse(await caps.Content.ReadAsStringAsync()).RootElement.GetProperty("endpoints")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("ease-off", endpoints);
    }

    [Fact]
    public async Task A_lease_does_nothing_when_app_busy_is_switched_off()
    {
        var settings = new HostSettings { EaseOff = [EaseOffReasons.Battery] };
        await using var host = await TestHost.StartAsync(settings: settings);
        var token = await host.GetTokenAsync();
        using var set = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.EaseOff, token,
            """{ "reason": "live_audio", "ttl_ms": 30000 }"""));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        var root = await PerfAsync(host, token);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("eased_off_now").ValueKind);
        Assert.Equal(["battery"], root.GetProperty("ease_off").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Screen_and_power_rules_follow_their_flags()
    {
        var sensors = new Sensors();
        await using var host = await TestHost.StartAsync(settings: new HostSettings(), sensors: sensors);
        var token = await host.GetTokenAsync();

        sensors.Fullscreen = true;
        Assert.Equal("fullscreen", host.Runtime.ReevaluateEaseOff());
        Assert.Equal(ProcessPriorityClass.Idle, host.Chat.RequestedPriority);
        sensors.Fullscreen = false;
        Assert.Null(host.Runtime.ReevaluateEaseOff());

        sensors.Battery = true;
        Assert.Equal("battery", (await PerfAsync(host, token)).GetProperty("eased_off_now").GetString());
        sensors.Battery = false;

        sensors.Gpu = true; // gpu_busy is not on by default
        Assert.Null(host.Runtime.ReevaluateEaseOff());
    }

    [Fact]
    public async Task The_ceiling_is_applied_to_the_engines_and_lowered_while_easing_off()
    {
        var sensors = new Sensors();
        var settings = new HostSettings { CpuCeilingPercent = 70 };
        await using var host = await TestHost.StartAsync(settings: settings, sensors: sensors);
        Assert.Equal(70, host.Chat.RequestedCpuCeiling);
        Assert.Equal(70, host.Embed.RequestedCpuCeiling);

        sensors.Fullscreen = true;
        host.Runtime.ReevaluateEaseOff();
        Assert.Equal(50, host.Chat.RequestedCpuCeiling);
        sensors.Fullscreen = false;
        host.Runtime.ReevaluateEaseOff();
        Assert.Equal(70, host.Chat.RequestedCpuCeiling);

        settings.CpuCeilingPercent = 30; // below the eased-off ceiling: easing off never raises it
        sensors.Fullscreen = true;
        await host.Runtime.ApplyPerformanceAsync(default);
        Assert.Equal(30, host.Chat.RequestedCpuCeiling);
        settings.CpuCeilingPercent = 5;  // out of range: clamped
        sensors.Fullscreen = false;
        await host.Runtime.ApplyPerformanceAsync(default);
        Assert.Equal(20, host.Chat.RequestedCpuCeiling);
    }
}
