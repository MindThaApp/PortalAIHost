using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client.Tests;

public class RetryTests
{
    private static Task<bool> Done(Task t) => t.ContinueWith(_ => true, TaskScheduler.Default);

    private static StubHost HostFailingThenOk(string code, int failures)
    {
        var host = new StubHost();
        var n = 0;
        host.Handler = (ctx, _) => Interlocked.Increment(ref n) <= failures
            ? Done(StubHost.WriteErrorAsync(ctx, code, retryAfterMs: 5))
            : Done(StubHost.WriteJsonAsync(ctx, 200, TestKit.ChatJson("ok")));
        return host;
    }

    [Theory]
    [InlineData(ErrorCodes.QueueFull)]
    [InlineData(ErrorCodes.ModelLoading)]
    [InlineData(ErrorCodes.EngineRestarting)]
    public async Task Retryable_errors_are_retried_up_to_three_tries(string code)
    {
        using var host = HostFailingThenOk(code, failures: 2);
        using var client = TestKit.Client(host);
        var result = await client.ChatAsync(TestKit.Chat());
        Assert.Equal("ok", result.Text);
        Assert.Equal(3, host.Count("/v1/chat/completions"));
    }

    [Fact]
    public async Task Retries_stop_after_the_limit_and_throw_without_fallback()
    {
        using var host = HostFailingThenOk(ErrorCodes.QueueFull, failures: 99);
        using var client = TestKit.Client(host);
        var ex = await Assert.ThrowsAsync<AiHostException>(() => client.ChatAsync(TestKit.Chat()));
        Assert.Equal(ErrorCodes.QueueFull, ex.Code);
        Assert.Equal(3, host.Count("/v1/chat/completions"));
    }

    [Theory]
    [InlineData(ErrorCodes.InvalidRequest)]
    [InlineData(ErrorCodes.RequestTooLarge)]
    [InlineData(ErrorCodes.ContextLengthExceeded)]
    public async Task Client_errors_are_never_retried_and_never_fall_back(string code)
    {
        using var host = HostFailingThenOk(code, failures: 99);
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        var ex = await Assert.ThrowsAsync<AiHostException>(() => client.ChatAsync(TestKit.Chat()));
        Assert.Equal(code, ex.Code);
        Assert.Equal(1, host.Count("/v1/chat/completions"));
        Assert.Equal(0, fallback.ChatCalls);
    }

    [Fact]
    public async Task Engine_error_is_retried_once_only()
    {
        using var host = HostFailingThenOk(ErrorCodes.EngineError, failures: 99);
        using var client = TestKit.Client(host);
        await Assert.ThrowsAsync<AiHostException>(() => client.ChatAsync(TestKit.Chat()));
        Assert.Equal(2, host.Count("/v1/chat/completions"));
    }

    [Fact]
    public async Task Retry_after_longer_than_the_deadline_gives_up_immediately()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.QueueFull, retryAfterMs: 60_000));
        using var client = TestKit.Client(host);
        await Assert.ThrowsAsync<AiHostException>(() =>
            client.ChatAsync(TestKit.Chat(), new RequestOptions { Deadline = TimeSpan.FromSeconds(2) }));
        Assert.Equal(1, host.Count("/v1/chat/completions"));
    }

    [Fact]
    public async Task Connection_failure_rediscovers_once_and_finds_the_restarted_host()
    {
        var first = new StubHost();
        var port = first.Port;
        var options = TestKit.Options(first);
        using var client = AiHostClient.Create(options);
        Assert.Equal(AiHostState.Ready, (await ReadyAfterStatusAsync(first, client)));
        first.Dispose();

        using var second = new StubHost(port);
        second.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200, TestKit.ChatJson("again")));
        var result = await client.ChatAsync(TestKit.Chat());
        Assert.Equal("again", result.Text);
    }

    private static async Task<AiHostState> ReadyAfterStatusAsync(StubHost host, AiHostClient client)
    {
        host.Handler = (ctx, _) => Done(StubHost.WriteJsonAsync(ctx, 200,
            AiHostJson.Serialize(new HostStatus { State = HostStates.Ready }, AiHostJsonContext.Default.HostStatus)));
        return await client.CheckAsync();
    }
}

public class FallbackTests
{
    private static Task<bool> Done(Task t) => t.ContinueWith(_ => true, TaskScheduler.Default);

    [Fact]
    public async Task Host_down_uses_the_fallback_raises_the_event_and_does_not_throw()
    {
        var fallback = new FakeFallback();
        using var client = TestKit.Client(null, fallback: fallback);
        FallbackActivatedEventArgs? seen = null;
        client.FallbackActivated += (_, e) => seen = e;

        var result = await client.ChatAsync(TestKit.Chat());

        Assert.Equal("local answer", result.Text);
        Assert.Equal(AiSource.Fallback, result.Source);
        Assert.Equal("local-model", result.Model);
        Assert.NotNull(result.FallbackReason);
        Assert.NotNull(seen);
        Assert.Equal(ClientErrorCodes.HostUnavailable, seen.Code);
        Assert.Contains("Home host offline", seen.Message);
        Assert.Contains("this PC's model", seen.Message);
    }

    [Fact]
    public async Task Host_down_without_a_fallback_throws_unavailable()
    {
        using var client = TestKit.Client(null);
        await Assert.ThrowsAsync<AiHostUnavailableException>(() => client.ChatAsync(TestKit.Chat()));
        Assert.Equal(AiHostState.NotInstalled, client.State);
    }

    [Fact]
    public async Task Allow_fallback_false_throws_instead()
    {
        var fallback = new FakeFallback();
        using var client = TestKit.Client(null, fallback: fallback);
        await Assert.ThrowsAsync<AiHostUnavailableException>(() =>
            client.ChatAsync(TestKit.Chat(), new RequestOptions { AllowFallback = false }));
        Assert.Equal(0, fallback.ChatCalls);
    }

    [Fact]
    public async Task No_token_falls_back_with_needs_access_and_never_contacts_models()
    {
        using var host = new StubHost();
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, new MemoryTokenStore(), fallback);
        var result = await client.ChatAsync(TestKit.Chat());
        Assert.Equal(AiSource.Fallback, result.Source);
        Assert.Equal(AiHostState.NeedsAccess, client.State);
        Assert.Equal(0, host.Count("/v1/chat/completions"));
    }

    [Theory]
    [InlineData(ErrorCodes.NoModel)]
    [InlineData(ErrorCodes.InsufficientMemory)]
    [InlineData(ErrorCodes.HostStopping)]
    [InlineData(ErrorCodes.DeadlineExceeded)]
    public async Task Fallback_error_codes_use_the_fallback_without_retrying(string code)
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, code));
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        var result = await client.ChatAsync(TestKit.Chat());
        Assert.Equal(AiSource.Fallback, result.Source);
        Assert.Equal(1, host.Count("/v1/chat/completions"));
    }

    [Fact]
    public async Task Exhausted_retries_fall_back()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.QueueFull, 2));
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        var result = await client.ChatAsync(TestKit.Chat());
        Assert.Equal(AiSource.Fallback, result.Source);
        Assert.Equal(3, host.Count("/v1/chat/completions"));
    }

    [Fact]
    public async Task Stream_with_host_down_yields_fallback_pieces_marked_as_fallback()
    {
        using var client = TestKit.Client(null, fallback: new FakeFallback());
        var deltas = new List<ChatDelta>();
        await foreach (var d in client.StreamChatAsync(TestKit.Chat()))
        {
            deltas.Add(d);
        }

        Assert.Equal("local stream", string.Concat(deltas.Select(d => d.Text)));
        Assert.All(deltas, d =>
        {
            Assert.Equal(AiSource.Fallback, d.Source);
            Assert.NotNull(d.FallbackReason);
        });
    }

    [Fact]
    public async Task Stream_error_before_the_first_token_falls_back()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteLinesAsync(ctx,
            ": portal {\"event\":\"queued\",\"position\":1}",
            "data: {\"error\":{\"message\":\"late\",\"type\":\"timeout\",\"code\":\"deadline_exceeded\"}}"));
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        var texts = new List<string>();
        await foreach (var d in client.StreamChatAsync(TestKit.Chat()))
        {
            if (d.Text is not null)
            {
                texts.Add(d.Text);
            }
        }

        Assert.Equal("local stream", string.Concat(texts));
        Assert.Equal(1, fallback.ChatCalls);
    }

    [Fact]
    public async Task Stream_error_after_the_first_token_throws_and_never_falls_back()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteLinesAsync(ctx,
            TestKit.Chunk("partial"),
            "data: {\"error\":{\"message\":\"boom\",\"type\":\"server_error\",\"code\":\"engine_error\"}}"));
        var fallback = new FakeFallback();
        using var client = TestKit.Client(host, fallback: fallback);
        var texts = new List<string>();
        var ex = await Assert.ThrowsAsync<AiHostException>(async () =>
        {
            await foreach (var d in client.StreamChatAsync(TestKit.Chat()))
            {
                if (d.Text is not null)
                {
                    texts.Add(d.Text);
                }
            }
        });
        Assert.Equal(ErrorCodes.EngineError, ex.Code);
        Assert.Equal(new[] { "partial" }, texts);
        Assert.Equal(0, fallback.ChatCalls);
        Assert.Equal(1, host.Count("/v1/chat/completions")); // no retry after the first token
    }

    [Fact]
    public async Task Embeddings_do_not_fall_back_by_default()
    {
        var fallback = new FakeFallback { SupportsEmbeddings = true };
        using var client = TestKit.Client(null, fallback: fallback);
        await Assert.ThrowsAsync<AiHostUnavailableException>(() => client.EmbedAsync(["a"]));
        Assert.Equal(0, fallback.EmbedCalls);
    }

    [Fact]
    public async Task Embeddings_fall_back_only_when_enabled_and_supported()
    {
        var fallback = new FakeFallback { SupportsEmbeddings = true };
        using var client = TestKit.Client(null, fallback: fallback, embedFallback: true);
        var result = await client.EmbedAsync(["a", "b"]);
        Assert.Equal(AiSource.Fallback, result.Source);
        Assert.Equal("local-model", result.ModelId);
        Assert.Equal(2, result.Vectors.Count);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Revoked_token_is_deleted_state_goes_revoked_and_the_call_falls_back()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.TokenRevoked));
        var folder = Path.Combine(Path.GetTempPath(), "pah-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var mine = new DpapiTokenStore(folder, "com.test.app");
            var other = new DpapiTokenStore(folder, "com.other.app");
            await mine.SaveAsync(TestKit.Token, default);
            await other.SaveAsync("pah_other", default);
            var stranger = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(stranger, "keep me");

            var fallback = new FakeFallback();
            using var client = TestKit.Client(host, mine, fallback);
            var result = await client.ChatAsync(TestKit.Chat());

            Assert.Equal(AiSource.Fallback, result.Source);
            Assert.Equal(AiHostState.Revoked, client.State);
            Assert.False(File.Exists(mine.FilePath));
            Assert.Null(client.GetBearerToken());
            Assert.True(File.Exists(other.FilePath));
            Assert.Equal("pah_other", await other.LoadAsync(default));
            Assert.Equal("keep me", await File.ReadAllTextAsync(stranger));

            // the next call does not contact the model endpoint again until access is requested
            await client.ChatAsync(TestKit.Chat());
            Assert.Equal(1, host.Count("/v1/chat/completions"));
            Assert.Equal(AiHostState.Revoked, client.State);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Token_invalid_is_handled_like_revoked()
    {
        using var host = new StubHost();
        host.Handler = (ctx, _) => Done(StubHost.WriteErrorAsync(ctx, ErrorCodes.TokenInvalid));
        var store = new MemoryTokenStore(TestKit.Token);
        using var client = TestKit.Client(host, store);
        var ex = await Assert.ThrowsAsync<AiHostException>(() => client.ChatAsync(TestKit.Chat()));
        Assert.Equal(ErrorCodes.TokenInvalid, ex.Code);
        Assert.Null(await store.LoadAsync(default));
        Assert.Equal(AiHostState.Revoked, client.State);
    }
}
