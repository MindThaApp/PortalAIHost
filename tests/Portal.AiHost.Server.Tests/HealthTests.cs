using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Server.Tests;

/// <summary>GET /host/health on the fake engines: needs a token, answers the documented shape, changes nothing.</summary>
public sealed class HealthTests
{
    [Fact]
    public async Task Health_needs_a_token()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        using var r = await host.Http.GetAsync(Routes.Health);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Health_answers_status_count_and_checks_with_snake_case_fields()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        var token = await host.GetTokenAsync();
        using var r = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Health, token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        Assert.Contains(root.GetProperty("status").GetString(), new[] { "ok", "warn", "fail" });
        var checks = root.GetProperty("checks");
        Assert.Equal(8, checks.GetArrayLength());
        var ids = checks.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Equal(["service", "engine_files", "graphics", "model", "disk", "memory", "ease_off", "memory_cap"], ids);
        var notOk = 0;
        foreach (var c in checks.EnumerateArray())
        {
            Assert.Contains(c.GetProperty("status").GetString(), new[] { "ok", "warn", "fail" });
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("detail").GetString()));
            notOk += c.GetProperty("status").GetString() == "ok" ? 0 : 1;
        }

        Assert.Equal(notOk, root.GetProperty("needs_attention").GetInt32());
        Assert.Equal("ok", checks[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Capabilities_list_the_health_endpoint()
    {
        await using var host = await TestHost.StartAsync(settings: new HostSettings());
        var token = await host.GetTokenAsync();
        using var r = await host.Http.SendAsync(host.Authed(HttpMethod.Get, Routes.Capabilities, token));
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("health", root.GetProperty("endpoints").EnumerateArray().Select(e => e.GetString()));
    }
}
