using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.FakeEngine;

/// <summary>Behaviour switches for <see cref="FakeEngineSupervisor"/>.</summary>
public sealed class FakeEngineOptions
{
    /// <summary>Time the simulated model load takes (slow-load mode: set to seconds).</summary>
    public TimeSpan LoadDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Delay between streamed tokens (slow-tokens mode).</summary>
    public TimeSpan TokenDelay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, loading fails with this API error code (for example insufficient_memory).</summary>
    public string? FailLoadWithCode { get; set; }

    /// <summary>Fixed reply text. When null the reply is "Echo: " plus the last user message.</summary>
    public string? Reply { get; set; }

    /// <summary>When set, non-streamed replies carry this as reasoning_content next to the text.</summary>
    public string? Reasoning { get; set; }

    public int EmbeddingDimensions { get; set; } = 8;
    public int ContextTokens { get; set; } = 4096;
}

/// <summary>
/// In-memory stand-in for a supervised engine, for other packages' unit tests. No process, no
/// model, no network. Supports load, unload, streamed chat tokens, embeddings, cancellation, a
/// simulated crash and a slow load. Tokens are whitespace-separated words.
/// </summary>
public sealed class FakeEngineSupervisor : IEngineSupervisor
{
    private readonly object _gate = new();
    private readonly string _kind;
    private CancellationTokenSource _lifetime = new();
    private EngineState _state = EngineState.Stopped;
    private EngineLaunchSpec? _current;
    private int _active;
    private int _cancelled;
    private int _loadCount;
    private int _crashCount;

    public FakeEngineSupervisor(string kind = ModelKinds.Chat, FakeEngineOptions? options = null)
    {
        _kind = kind;
        Options = options ?? new FakeEngineOptions();
    }

    public FakeEngineOptions Options { get; }

    public string Kind => _kind;

    public EngineState State
    {
        get { lock (_gate) { return _state; } }
    }

    public EngineLaunchSpec? Current
    {
        get { lock (_gate) { return _current; } }
    }

    public event EventHandler<EngineStateChange>? StateChanged;

    /// <summary>Requests currently running.</summary>
    public int ActiveRequests => Volatile.Read(ref _active);

    /// <summary>Requests that ended because the caller cancelled.</summary>
    public int CancelledRequests => Volatile.Read(ref _cancelled);

    /// <summary>How many times a load completed.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    public int CrashCount => Volatile.Read(ref _crashCount);

    public async Task StartAsync(EngineLaunchSpec spec, CancellationToken ct)
    {
        CancellationToken life;
        lock (_gate)
        {
            if (_state is EngineState.Ready or EngineState.Loading or EngineState.Starting)
            {
                if (_current?.ModelId == spec.ModelId)
                {
                    return;
                }
            }

            if (_lifetime.IsCancellationRequested)
            {
                _lifetime.Dispose();
                _lifetime = new CancellationTokenSource();
            }

            life = _lifetime.Token;
            _current = spec;
        }

        Set(EngineState.Starting);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
            Set(EngineState.Loading, 0);
            var steps = 4;
            for (var i = 1; i <= steps; i++)
            {
                if (Options.LoadDelay > TimeSpan.Zero)
                {
                    await Task.Delay(Options.LoadDelay / steps, linked.Token).ConfigureAwait(false);
                }

                Set(EngineState.Loading, (double)i / steps);
            }

            if (Options.FailLoadWithCode is { } code)
            {
                Set(EngineState.Failed, 0, "load failed");
                throw new EngineException(code, "Simulated load failure.");
            }

            Interlocked.Increment(ref _loadCount);
            Set(EngineState.Ready, 1);
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _current = null; }
            Set(EngineState.Stopped);
            throw;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state == EngineState.Stopped)
            {
                return Task.CompletedTask;
            }
        }

        Set(EngineState.Stopping);
        _lifetime.Cancel();
        lock (_gate) { _current = null; }
        Set(EngineState.Stopped);
        return Task.CompletedTask;
    }

    /// <summary>Simulates the engine process dying: in-flight requests throw EngineCrashedException.</summary>
    public void SimulateCrash()
    {
        Interlocked.Increment(ref _crashCount);
        Set(EngineState.Failed, 0, "engine crashed");
        _lifetime.Cancel();
    }

    public async Task<ChatCompletionResponse> ChatAsync(ChatCompletionRequest request, CancellationToken ct)
    {
        EnsureReady();
        using var guard = Begin(ct, out var token);
        var words = ReplyWords(request, out var finish);
        var text = new StringBuilder();
        try
        {
            for (var i = 0; i < words.Length; i++)
            {
                await DelayTokenAsync(token).ConfigureAwait(false);
                text.Append(i == 0 ? words[i] : " " + words[i]);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Crashed();
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }

        var prompt = PromptTokens(request);
        return new ChatCompletionResponse
        {
            Id = NewId(),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = ModelId,
            Choices =
            {
                new ChatChoice
                {
                    Index = 0,
                    Message = new ChatResponseMessage { Content = text.ToString(), ReasoningContent = Options.Reasoning },
                    FinishReason = finish,
                },
            },
            Usage = new Usage { PromptTokens = prompt, CompletionTokens = words.Length, TotalTokens = prompt + words.Length },
        };
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatAsync(
        ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        using var guard = Begin(ct, out var token);
        var words = ReplyWords(request, out var finish);
        var id = NewId();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        ChatCompletionChunk Chunk(ChatDeltaMessage delta, string? reason = null) => new()
        {
            Id = id,
            Created = created,
            Model = ModelId,
            Choices = { new ChatChunkChoice { Index = 0, Delta = delta, FinishReason = reason } },
        };

        yield return Chunk(new ChatDeltaMessage { Role = ChatRoles.Assistant, Content = "" });
        for (var i = 0; i < words.Length; i++)
        {
            try
            {
                await DelayTokenAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Crashed();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            yield return Chunk(new ChatDeltaMessage { Content = i == 0 ? words[i] : " " + words[i] });
        }

        yield return Chunk(new ChatDeltaMessage(), finish);
        if (request.StreamOptions?.IncludeUsage == true)
        {
            var prompt = PromptTokens(request);
            yield return new ChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = ModelId,
                Usage = new Usage { PromptTokens = prompt, CompletionTokens = words.Length, TotalTokens = prompt + words.Length },
            };
        }
    }

    public Task<EmbeddingsResponse> EmbedAsync(EmbeddingsRequest request, CancellationToken ct)
    {
        EnsureReady();
        ct.ThrowIfCancellationRequested();
        var base64 = string.Equals(request.EncodingFormat, "base64", StringComparison.Ordinal);
        var response = new EmbeddingsResponse { Model = ModelId };
        var total = 0;
        for (var i = 0; i < request.Input.Values.Count; i++)
        {
            var text = request.Input.Values[i];
            var vector = Vectorise(text, Options.EmbeddingDimensions);
            total += CountWords(text);
            response.Data.Add(new EmbeddingData
            {
                Index = i,
                Embedding = base64 ? EmbeddingVector.FromFloatsAsBase64(vector) : EmbeddingVector.FromFloats(vector),
            });
        }

        return Task.FromResult(new EmbeddingsResponse
        {
            Data = response.Data,
            Model = ModelId,
            Usage = new EmbeddingsUsage { PromptTokens = total, TotalTokens = total },
        });
    }

    public Task<int> CountTokensAsync(TokenizeRequest request, CancellationToken ct)
    {
        EnsureReady();
        ct.ThrowIfCancellationRequested();
        var count = request.Text is not null
            ? CountWords(request.Text)
            : request.Messages?.Sum(m => CountWords(m.Content?.AsText() ?? "") + 3) ?? 0;
        return Task.FromResult(count);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private string ModelId => Current?.ModelId ?? "fake-model";

    private static string NewId() => "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];

    private void Set(EngineState state, double progress = 0, string? message = null)
    {
        lock (_gate) { _state = state; }
        StateChanged?.Invoke(this, new EngineStateChange(_kind, state, progress, message));
    }

    private void EnsureReady()
    {
        if (State != EngineState.Ready)
        {
            throw new EngineException(
                State is EngineState.Failed or EngineState.Starting or EngineState.Loading
                    ? ErrorCodes.EngineRestarting
                    : ErrorCodes.NoModel,
                "The fake engine is not ready.");
        }
    }

    private static EngineCrashedException Crashed() => new("The fake engine crashed.");

    private ActiveGuard Begin(CancellationToken ct, out CancellationToken token)
    {
        var life = _lifetime.Token;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, life);
        token = linked.Token;
        Interlocked.Increment(ref _active);
        return new ActiveGuard(this, linked);
    }

    private async Task DelayTokenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Options.TokenDelay > TimeSpan.Zero)
        {
            await Task.Delay(Options.TokenDelay, token).ConfigureAwait(false);
        }
    }

    private string[] ReplyWords(ChatCompletionRequest request, out string finish)
    {
        var text = Options.Reply
            ?? "Echo: " + (request.Messages.LastOrDefault(m => m.Role == ChatRoles.User)?.Content?.AsText() ?? "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var max = request.MaxCompletionTokens ?? request.MaxTokens;
        finish = FinishReasons.Stop;
        if (max is { } m && m < words.Length)
        {
            finish = FinishReasons.Length;
            return words[..Math.Max(0, m)];
        }

        return words;
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int PromptTokens(ChatCompletionRequest request) =>
        request.Messages.Sum(m => CountWords(m.Content?.AsText() ?? "") + 3);

    /// <summary>Deterministic unit-length vector derived from a hash of the text.</summary>
    private static float[] Vectorise(string text, int dimensions)
    {
        var vector = new float[dimensions];
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        double sum = 0;
        for (var i = 0; i < dimensions; i++)
        {
            var b = seed[i % seed.Length] ^ (i * 31 & 0xFF);
            vector[i] = (b / 127.5f) - 1f;
            sum += vector[i] * vector[i];
        }

        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
        {
            for (var i = 0; i < dimensions; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private sealed class ActiveGuard : IDisposable
    {
        private readonly FakeEngineSupervisor _owner;
        private readonly CancellationTokenSource _linked;

        public ActiveGuard(FakeEngineSupervisor owner, CancellationTokenSource linked)
        {
            _owner = owner;
            _linked = linked;
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _owner._active);
            _linked.Dispose();
        }
    }
}
