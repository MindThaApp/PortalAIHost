using System.Net;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Tests;

public class CallTests
{
    private static Task<bool> Done(Task t) => t.ContinueWith(_ => true, TaskScheduler.Default);

    [Fact]
    public async Task Chat_sends_bearer_and_control_headers_and_parses_reply()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200, TestKit.ChatJson("hello"),
            new Dictionary<string, string> { [HeaderNames.Model] = "served-model" }));
        using var client = TestKit.Client(host);

        var result = await client.ChatAsync(TestKit.Chat(), new RequestOptions
        {
            Priority = AiPriority.Batch,
            Deadline = TimeSpan.FromSeconds(30),
            LoadBehavior = AiLoadBehavior.Fail,
            ClientRequestId = "req-1",
        });

        Assert.Equal("hello", result.Text);
        Assert.Equal(AiSource.Host, result.Source);
        Assert.Equal("served-model", result.Model);
        Assert.Equal(AiHostState.Ready, client.State);
        var seen = host.Requests.Single(r => r.PathAndQuery == "/v1/chat/completions");
        Assert.Equal("Bearer " + TestKit.Token, seen.Headers["Authorization"]);
        Assert.Equal("batch", seen.Headers[HeaderNames.Priority]);
        Assert.Equal("fail", seen.Headers[HeaderNames.Load]);
        Assert.Equal("req-1", seen.Headers[HeaderNames.ClientRequestId]);
        var deadline = int.Parse(seen.Headers[HeaderNames.DeadlineMs]);
        Assert.InRange(deadline, 25_000, 30_000);
        Assert.Contains("\"model\":\"default\"", seen.Body);
        Assert.Contains("\"stream\":false", seen.Body);
        Assert.StartsWith("application/json", seen.Headers["Content-Type"]);
    }

    [Fact]
    public async Task Default_priority_is_sent_and_no_deadline_header_without_a_deadline()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200, TestKit.ChatJson("x")));
        using var client = TestKit.Client(host);
        await client.ChatAsync(TestKit.Chat());
        var seen = host.Requests.Single(r => r.PathAndQuery == "/v1/chat/completions");
        Assert.Equal("interactive", seen.Headers[HeaderNames.Priority]);
        Assert.False(seen.Headers.ContainsKey(HeaderNames.DeadlineMs));
    }

    [Fact]
    public async Task Stream_yields_text_progress_and_ends_at_done()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteLinesAsync(ctx,
            ": portal {\"event\":\"queued\",\"position\":2}",
            ": portal {\"event\":\"loading\",\"progress\":0.42}",
            ": some other comment",
            ": portal {\"event\":\"started\"}",
            TestKit.Chunk("Hel"),
            TestKit.Chunk("lo"),
            TestKit.Chunk(null, "stop"),
            "data: [DONE]"));
        using var client = TestKit.Client(host);

        var deltas = new List<ChatDelta>();
        await foreach (var d in client.StreamChatAsync(TestKit.Chat()))
        {
            deltas.Add(d);
        }

        Assert.Equal("Hello", string.Concat(deltas.Select(d => d.Text)));
        var progress = deltas.Where(d => d.Progress is not null).Select(d => d.Progress!).ToList();
        Assert.Equal(3, progress.Count);
        Assert.Equal(ProgressEvents.Queued, progress[0].Event);
        Assert.Equal(2, progress[0].Position);
        Assert.Equal(0.42, progress[1].Progress);
        Assert.Equal("stop", deltas.Last().FinishReason);
        Assert.All(deltas, d => Assert.Equal(AiSource.Host, d.Source));
        Assert.Contains("\"stream\":true", host.Requests.Single(r => r.PathAndQuery == "/v1/chat/completions").Body);
    }

    [Fact]
    public async Task Cancelling_a_stream_closes_the_connection()
    {
        using var host = new StubHost();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Handler = async (ctx, _) =>
        {
            try
            {
                await StubHost.WriteLinesAsync(ctx, TestKit.Chunk("first"));
                while (true)
                {
                    await Task.Delay(20);
                    await StubHost.WriteMoreAsync(ctx, ": portal {\"event\":\"started\"}");
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                disconnected.TrySetResult();
                return true;
            }
        };
        using var client = TestKit.Client(host);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var d in client.StreamChatAsync(TestKit.Chat(), null, cts.Token))
            {
                if (d.Text == "first")
                {
                    cts.Cancel();
                }
            }
        });

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancelling_a_non_stream_call_throws_operation_canceled_not_fallback()
    {
        using var host = new StubHost();
        host.Handler = async (ctx, _) =>
        {
            await Task.Delay(5000);
            return false;
        };
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(TestKit.Chat(), null, cts.Token));
        Assert.Equal(0, fallback.ChatCalls);
    }

    [Fact]
    public async Task Embeddings_return_vectors_and_the_model_id()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200, AiHostJson.Serialize(new EmbeddingsResponse
        {
            Model = "embed-small-q8",
            Data =
            [
                new EmbeddingData { Index = 1, Embedding = new EmbeddingVector { Values = [3f, 4f] } },
                new EmbeddingData { Index = 0, Embedding = new EmbeddingVector { Values = [1f, 2f] } },
            ],
        }, AiHostJsonContext.Default.EmbeddingsResponse)));
        using var client = TestKit.Client(host);

        var result = await client.EmbedAsync(["a", "b"]);

        Assert.Equal("embed-small-q8", result.ModelId);
        Assert.Equal(new[] { 1f, 2f }, result.Vectors[0]);
        Assert.Equal(new[] { 3f, 4f }, result.Vectors[1]);
        Assert.Contains("\"model\":\"default-embedding\"", host.Requests.Single(r => r.PathAndQuery == "/v1/embeddings").Body);
    }

    [Fact]
    public async Task Status_capabilities_models_warmup_and_tokenize_helpers_work()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            string? json = path switch
            {
                "/host/status" => AiHostJson.Serialize(new HostStatus { State = HostStates.Idle }, AiHostJsonContext.Default.HostStatus),
                "/host/capabilities" => AiHostJson.Serialize(new HostCapabilities { CapabilitiesRevision = 7 }, AiHostJsonContext.Default.HostCapabilities),
                "/v1/models" => AiHostJson.Serialize(new ModelList { Data = [new ModelEntry { Id = "m1" }] }, AiHostJsonContext.Default.ModelList),
                "/host/warmup" => AiHostJson.Serialize(new WarmupResponse(), AiHostJsonContext.Default.WarmupResponse),
                "/host/tokenize" => AiHostJson.Serialize(new TokenizeResponse { Tokens = 12, ContextTokens = 4096 }, AiHostJsonContext.Default.TokenizeResponse),
                _ => null,
            };
            return json is null ? Task.FromResult(false) : Done(StubHost.WriteJsonAsync(ctx, 200, json));
        };
        using var client = TestKit.Client(host);

        Assert.Equal(HostStates.Idle, (await client.GetStatusAsync()).State);
        Assert.Equal(7, (await client.GetCapabilitiesAsync()).CapabilitiesRevision);
        Assert.Equal("m1", (await client.GetModelsAsync()).Data.Single().Id);
        Assert.Equal(ModelStates.Loading, (await client.WarmUpAsync()).State);
        Assert.Equal(12, (await client.CountTokensAsync("hello")).Tokens);
        Assert.Equal(12, (await client.CountTokensAsync(TestKit.Chat().Messages)).Tokens);
        Assert.Equal(new Uri(host.BaseUri, "v1/"), client.OpenAiBaseAddress);
    }

    [Fact]
    public async Task Check_reports_no_model_when_the_host_has_none()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200,
            AiHostJson.Serialize(new HostStatus { State = HostStates.NoModel }, AiHostJsonContext.Default.HostStatus)));
        using var client = TestKit.Client(host);
        Assert.Equal(AiHostState.NoModel, await client.CheckAsync());
    }
}
