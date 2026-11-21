using System.Net;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

public sealed class ShutdownTests
{
    [Fact]
    public async Task Request_cut_by_quit_answers_host_stopping()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { TokenDelay = TimeSpan.FromSeconds(20) });
        var token = await host.GetTokenAsync();
        var call = host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token,
            """{ "messages": [ { "role": "user", "content": "Hi" } ] }"""));
        for (var i = 0; i < 100 && host.Runtime.InFlightRequests == 0; i++)
        {
            await Task.Delay(20);
        }

        await host.Runtime.StopAsync(TimeSpan.FromMilliseconds(100));
        using var response = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"host_stopping\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_long_poll_ends_when_the_host_quits()
    {
        await using var host = await TestHost.StartAsync();
        using var create = await host.Http.PostAsync(Routes.AuthRequests,
            TestHost.Json("""{"app_id":"com.mindthaapp.voiceportal","app_name":"Voice Portal"}"""));
        var body = await create.Content.ReadAsStringAsync();
        var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("request_id").GetString();
        var poll = host.Http.GetAsync(Routes.AuthRequests + "/" + id + "?wait_ms=25000");
        await Task.Delay(300);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await host.Runtime.StopAsync(TimeSpan.Zero);
        using var response = await poll.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Failed_keep_loaded_engine_is_reloaded_on_resume_and_checks_do_not_overlap()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { FailLoadWithCode = ErrorCodes.EngineError });
        await host.Runtime.AutoLoadAsync([ModelKinds.Chat], CancellationToken.None);
        Assert.Equal(EngineState.Failed, host.Chat.State);

        host.Chat.Options.FailLoadWithCode = null;
        var loadsBefore = host.Chat.LoadCount;
        await Task.WhenAll(host.Runtime.CheckEnginesAfterResumeAsync(CancellationToken.None),
            host.Runtime.CheckEnginesAfterResumeAsync(CancellationToken.None));
        Assert.Equal(EngineState.Ready, host.Chat.State);
        Assert.Equal(loadsBefore + 1, host.Chat.LoadCount);
    }

    [Fact]
    public async Task Server_stop_does_not_wait_for_a_long_request()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { TokenDelay = TimeSpan.FromSeconds(30) });
        var token = await host.GetTokenAsync();
        var call = host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token,
            """{ "messages": [ { "role": "user", "content": "Hi" } ] }"""));
        for (var i = 0; i < 100 && host.Runtime.InFlightRequests == 0; i++)
        {
            await Task.Delay(20);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await host.Server.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), watch.Elapsed.ToString());
        await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
