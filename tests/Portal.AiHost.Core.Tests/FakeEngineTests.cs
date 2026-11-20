using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Core.Tests;

public class FakeEngineTests
{
    private static EngineLaunchSpec Spec(string kind = ModelKinds.Chat) =>
        new() { Kind = kind, ModelId = "fake-" + kind, DisplayName = "Fake" };

    private static ChatCompletionRequest Chat(string text, bool include = false, int? max = null) => new()
    {
        Model = "default",
        Messages = { new ChatMessage { Role = ChatRoles.User, Content = text } },
        StreamOptions = include ? new StreamOptions { IncludeUsage = true } : null,
        MaxTokens = max,
    };

    [Fact]
    public async Task LoadAndUnloadRaiseStateChanges()
    {
        await using var engine = new FakeEngineSupervisor();
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.State);

        await engine.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(EngineState.Ready, engine.State);
        await engine.StopAsync(CancellationToken.None);

        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Equal(EngineState.Starting, states[0]);
        Assert.Contains(EngineState.Loading, states);
        Assert.Contains(EngineState.Ready, states);
        Assert.Equal(EngineState.Stopped, states[^1]);
        Assert.Null(engine.Current);
    }

    [Fact]
    public async Task ChatBeforeLoadFailsWithNoModel()
    {
        await using var engine = new FakeEngineSupervisor();
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ChatAsync(Chat("hi"), CancellationToken.None));
        Assert.Equal(ErrorCodes.NoModel, ex.ErrorCode);
    }

    [Fact]
    public async Task StreamsTokensThenFinishAndUsage()
    {
        await using var engine = new FakeEngineSupervisor();
        await engine.StartAsync(Spec(), CancellationToken.None);

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var c in engine.StreamChatAsync(Chat("hello big world", include: true), CancellationToken.None))
        {
            chunks.Add(c);
        }

        var text = string.Concat(chunks.SelectMany(c => c.Choices).Select(c => c.Delta.Content));
        Assert.Equal("Echo: hello big world", text);
        Assert.Equal(FinishReasons.Stop, chunks[^2].Choices[0].FinishReason);
        Assert.Equal(4, chunks[^1].Usage?.CompletionTokens);
    }

    [Fact]
    public async Task NonStreamingChatHonoursMaxTokens()
    {
        await using var engine = new FakeEngineSupervisor();
        await engine.StartAsync(Spec(), CancellationToken.None);
        var response = await engine.ChatAsync(Chat("one two three", max: 2), CancellationToken.None);
        Assert.Equal("Echo: one", response.Choices[0].Message.Content);
        Assert.Equal(FinishReasons.Length, response.Choices[0].FinishReason);
    }

    [Fact]
    public async Task CancellationStopsStreamAndFreesSlot()
    {
        await using var engine = new FakeEngineSupervisor(options: new FakeEngineOptions
        {
            TokenDelay = TimeSpan.FromMilliseconds(50),
            Reply = string.Join(' ', Enumerable.Repeat("word", 200)),
        });
        await engine.StartAsync(Spec(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in engine.StreamChatAsync(Chat("x"), cts.Token))
            {
                if (++seen == 3)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Equal(0, engine.ActiveRequests);
        Assert.Equal(1, engine.CancelledRequests);
    }

    [Fact]
    public async Task SimulatedCrashAbortsInFlightRequestAndRestartRecovers()
    {
        await using var engine = new FakeEngineSupervisor(options: new FakeEngineOptions
        {
            TokenDelay = TimeSpan.FromMilliseconds(30),
            Reply = string.Join(' ', Enumerable.Repeat("word", 200)),
        });
        await engine.StartAsync(Spec(), CancellationToken.None);

        var task = Task.Run(async () =>
        {
            await foreach (var _ in engine.StreamChatAsync(Chat("x"), CancellationToken.None))
            {
            }
        });
        await Task.Delay(100);
        engine.SimulateCrash();

        await Assert.ThrowsAsync<EngineCrashedException>(() => task);
        Assert.Equal(EngineState.Failed, engine.State);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.ChatAsync(Chat("hi"), CancellationToken.None));
        Assert.Equal(ErrorCodes.EngineRestarting, ex.ErrorCode);

        await engine.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(EngineState.Ready, engine.State);
        Assert.Equal(1, engine.CrashCount);
        engine.Options.Reply = null;
        var ok = await engine.ChatAsync(Chat("again"), CancellationToken.None);
        Assert.Equal("Echo: again", ok.Choices[0].Message.Content);
    }

    [Fact]
    public async Task SlowLoadCanBeCancelled()
    {
        await using var engine = new FakeEngineSupervisor(options: new FakeEngineOptions { LoadDelay = TimeSpan.FromSeconds(30) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(Spec(), cts.Token));
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task LoadFailureSurfacesApiErrorCode()
    {
        await using var engine = new FakeEngineSupervisor(options: new FakeEngineOptions
        {
            FailLoadWithCode = ErrorCodes.InsufficientMemory,
        });
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Spec(), CancellationToken.None));
        Assert.Equal(ErrorCodes.InsufficientMemory, ex.ErrorCode);
        Assert.Equal(EngineState.Failed, engine.State);
    }

    [Fact]
    public async Task EmbeddingsAreNormalisedDeterministicAndSupportBase64()
    {
        await using var engine = new FakeEngineSupervisor(ModelKinds.Embedding);
        await engine.StartAsync(Spec(ModelKinds.Embedding), CancellationToken.None);

        var request = new EmbeddingsRequest { Input = new StringOrList("alpha", "beta") };
        var a = await engine.EmbedAsync(request, CancellationToken.None);
        var b = await engine.EmbedAsync(request, CancellationToken.None);
        Assert.Equal(2, a.Data.Count);
        Assert.Equal(a.Data[0].Embedding.ToFloats(), b.Data[0].Embedding.ToFloats());
        Assert.NotEqual(a.Data[0].Embedding.ToFloats(), a.Data[1].Embedding.ToFloats());
        var norm = a.Data[0].Embedding.ToFloats().Sum(x => x * x);
        Assert.InRange(norm, 0.999, 1.001);

        var b64 = await engine.EmbedAsync(
            new EmbeddingsRequest { Input = "alpha", EncodingFormat = "base64" }, CancellationToken.None);
        Assert.NotNull(b64.Data[0].Embedding.Base64);
        Assert.Equal(a.Data[0].Embedding.ToFloats(), b64.Data[0].Embedding.ToFloats());
    }

    [Fact]
    public async Task CountsTokens()
    {
        await using var engine = new FakeEngineSupervisor();
        await engine.StartAsync(Spec(), CancellationToken.None);
        Assert.Equal(4, await engine.CountTokensAsync(new TokenizeRequest { Text = "a b c d" }, CancellationToken.None));
    }
}
