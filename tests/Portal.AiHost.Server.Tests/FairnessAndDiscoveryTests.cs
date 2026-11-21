using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

public sealed class FairnessTests
{
    /// <summary>
    /// Acceptance (architecture section 7): app A floods batch work, app B sends one interactive request. B is
    /// served at once on the slot batch may not take, long before A's backlog is done.
    /// </summary>
    [Fact]
    public async Task Interactive_app_is_not_starved_by_another_apps_batch_backlog()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions
        {
            TokenDelay = TimeSpan.FromMilliseconds(100),
            Reply = "one two three",
        }, parallelSlots: 2);
        var tokenA = await host.GetTokenAsync("com.example.batchapp");
        var tokenB = await host.GetTokenAsync("com.mindthaapp.voiceportal");
        const string body = """{ "messages": [ { "role": "user", "content": "Hi" } ] }""";

        // Warm the model so load time does not count.
        (await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, tokenB, body))).Dispose();

        var sw = Stopwatch.StartNew();
        var batch = Enumerable.Range(0, 6).Select(async _ =>
        {
            var msg = host.Authed(HttpMethod.Post, Routes.ChatCompletions, tokenA, body);
            msg.Headers.Add(HeaderNames.Priority, PriorityValues.Batch);
            using var r = await host.Http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return sw.Elapsed;
        }).ToList();
        await StreamingTests.WaitUntil(() => host.Scheduler.Snapshot().WaitingBatch >= 4);

        using var interactive = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, tokenB, body));
        var interactiveDone = sw.Elapsed;
        Assert.Equal(HttpStatusCode.OK, interactive.StatusCode);
        var queueMs = long.Parse(interactive.Headers.GetValues(HeaderNames.QueueMs).Single(), System.Globalization.CultureInfo.InvariantCulture);
        var batchDone = (await Task.WhenAll(batch)).Max();

        Assert.True(queueMs < 250, $"interactive waited {queueMs} ms behind batch work");
        Assert.True(interactiveDone < batchDone, $"interactive {interactiveDone} vs batch {batchDone}");
    }

    [Fact]
    public async Task Queue_full_is_429_with_retry_after()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { TokenDelay = TimeSpan.FromMilliseconds(300) });
        var token = await host.GetTokenAsync();
        const string body = """{ "messages": [ { "role": "user", "content": "Hi" } ] }""";
        var load = Enumerable.Range(0, 10).Select(_ => host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, body))).ToList();
        var responses = await Task.WhenAll(load);
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        var full = responses.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Contains("queue_full", await full.Content.ReadAsStringAsync());
        Assert.True(full.Headers.Contains(HeaderNames.RetryAfter));
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }
}

public sealed class DiscoveryTests
{
    [Fact]
    public async Task Discovery_file_is_written_in_the_test_root_and_removed_on_stop()
    {
        string path;
        string port;
        var host = await TestHost.StartAsync();
        try
        {
            path = host.DiscoveryPath;
            Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(host.RootPath, TestRoot.MarkerName)));
            var file = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            Assert.Equal("portal-ai-host", file.GetProperty("service").GetString());
            Assert.Equal("6f1c2a9e4b7d4c0f9a51d3e8b2c47a10", file.GetProperty("instance_id").GetString());
            Assert.Equal(host.Server.Port, file.GetProperty("port").GetInt32());
            Assert.Equal(Environment.ProcessId, file.GetProperty("pid").GetInt32());
            port = host.Server.BaseUrl;
            Assert.Equal(port, file.GetProperty("base_url").GetString());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + Core.Settings.AtomicFile.TempSuffix));
            await host.Server.StopAsync();
            Assert.False(File.Exists(path));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void Delete_leaves_another_hosts_file_alone()
    {
        using var root = new TestRoot();
        var path = DiscoveryFileWriter.PathFor(root.Path);
        DiscoveryFileWriter.Write(path, new DiscoveryFile { InstanceId = "newer-host", Port = 41870 });
        Assert.False(DiscoveryFileWriter.DeleteIfOwn(path, "old-host"));
        Assert.True(File.Exists(path));
        Assert.True(DiscoveryFileWriter.DeleteIfOwn(path, "newer-host"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_never_touches_a_file_that_is_not_a_discovery_file()
    {
        using var root = new TestRoot();
        var dir = Path.Combine(root.Path, DiscoveryFileWriter.FolderName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DiscoveryFileWriter.FileName);
        File.WriteAllText(path, "user notes, not json");
        Assert.False(DiscoveryFileWriter.DeleteIfOwn(path, "x"));
        File.WriteAllText(path, """{"service":"something-else","instance_id":"x"}""");
        Assert.False(DiscoveryFileWriter.DeleteIfOwn(path, "x"));
        Assert.True(File.Exists(path));
        var other = Path.Combine(dir, "notes.txt");
        File.WriteAllText(other, """{"service":"portal-ai-host","instance_id":"x"}""");
        Assert.Throws<ArgumentException>(() => DiscoveryFileWriter.DeleteIfOwn(other, "x"));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task Busy_port_falls_back_to_the_next_one()
    {
        await using var first = await TestHost.StartAsync();
        await using var second = await TestHost.StartAsync();
        Assert.NotEqual(first.Server.Port, second.Server.Port);
        using var ping = await second.Http.GetAsync(Routes.Ping);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
    }
}
