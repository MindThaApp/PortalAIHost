using System.Diagnostics;
using System.Net;
using Portal.AiHost.Contracts;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Server.Tests;

public sealed class StreamingTests
{
    private const string StreamBody =
        """{ "messages": [ { "role": "user", "content": "Hi" } ], "stream": true, "stream_options": { "include_usage": true } }""";

    [Fact]
    public async Task Stream_sends_progress_comments_chunks_and_done()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { LoadDelay = TimeSpan.FromMilliseconds(600) });
        var token = await host.GetTokenAsync();
        using var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, StreamBody),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var lines = await ReadAllLinesAsync(response);
        Assert.Contains(lines, l => l.StartsWith(": portal {\"event\":\"loading\"", StringComparison.Ordinal));
        Assert.Contains(": portal {\"event\":\"started\"}", lines);
        Assert.Contains(lines, l => l.StartsWith("data: {", StringComparison.Ordinal) && l.Contains("chat.completion.chunk", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("data: {", StringComparison.Ordinal) && l.Contains("\"usage\"", StringComparison.Ordinal));
        Assert.Equal("data: [DONE]", lines.Last(l => l.Length > 0));
    }

    [Fact]
    public async Task Queued_stream_gets_keep_alive_comments_with_position()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { TokenDelay = TimeSpan.FromMilliseconds(150) });
        var token = await host.GetTokenAsync();
        // Occupy the single slot.
        var busy = host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token,
            """{ "messages": [ { "role": "user", "content": "Hi" } ] }"""));
        await WaitUntil(() => host.Chat.ActiveRequests == 1);
        using var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, StreamBody),
            HttpCompletionOption.ResponseHeadersRead);
        var lines = await ReadAllLinesAsync(response);
        var queued = lines.Where(l => l.StartsWith(": portal {\"event\":\"queued\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(queued);
        Assert.Contains(queued, l => l.Contains("\"position\":1", StringComparison.Ordinal));
        Assert.True(queued.Count >= 2, "the keep-alive repeats the queue position while waiting");
        Assert.Equal("data: [DONE]", lines.Last(l => l.Length > 0));
        (await busy).Dispose();
    }

    [Fact]
    public async Task Closing_the_stream_cancels_the_engine_request_and_frees_the_slot()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions
        {
            TokenDelay = TimeSpan.FromMilliseconds(200),
            Reply = string.Join(' ', Enumerable.Repeat("word", 200)),
        });
        var token = await host.GetTokenAsync();
        using (var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, StreamBody),
            HttpCompletionOption.ResponseHeadersRead))
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null && !line.Contains("\"content\":\"word", StringComparison.Ordinal))
            {
            }

            Assert.Equal(1, host.Chat.ActiveRequests);
        }

        var sw = Stopwatch.StartNew();
        await WaitUntil(() => host.Chat.ActiveRequests == 0 && host.Chat.CancelledRequests == 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {sw.Elapsed}");
        await WaitUntil(() => host.Scheduler.Snapshot().Running == 0);
    }

    [Fact]
    public async Task Error_after_stream_start_is_one_data_event_without_done()
    {
        await using var host = await TestHost.StartAsync(new FakeEngineOptions { FailLoadWithCode = ErrorCodes.InsufficientMemory });
        var token = await host.GetTokenAsync();
        using var response = await host.Http.SendAsync(host.Authed(HttpMethod.Post, Routes.ChatCompletions, token, StreamBody),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = await ReadAllLinesAsync(response);
        var data = lines.Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).ToList();
        Assert.Single(data);
        Assert.Contains("\"insufficient_memory\"", data[0], StringComparison.Ordinal);
        Assert.DoesNotContain("data: [DONE]", lines);
    }

    internal static async Task<List<string>> ReadAllLinesAsync(HttpResponseMessage response)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cts.Token) is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "condition not reached in time");
            await Task.Delay(20);
        }
    }
}
