using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Runtime;

public sealed partial class AiHostRuntime
{
    private volatile IInferenceBackend? _backend;
    private int _localInFlight;

    /// <summary>The backend behind the model routes: this machine's engine unless client mode swapped it.</summary>
    public IInferenceBackend Backend => _backend ?? _local;

    /// <summary>This machine's own engine as a backend, for the laptop-model fallback in client mode.</summary>
    public IInferenceBackend LocalInferenceBackend => _local;

    /// <summary>True while a client-mode backend (not the local engine) answers the model routes.</summary>
    public bool UsesRemoteBackend => _backend is not null;

    /// <summary>Requests the local engine is answering right now (the mode switch waits for them before it unloads).</summary>
    public int LocalInFlightRequests => Volatile.Read(ref _localInFlight);

    /// <summary>Points the model routes at <paramref name="backend"/>; null goes back to the local engine. Bumps the revision apps watch.</summary>
    public void SetBackend(IInferenceBackend? backend)
    {
        _backend = backend;
        BumpRevision();
    }

    /// <summary>Changes <see cref="CapabilitiesRevision"/> so apps re-read the capabilities.</summary>
    public void BumpRevision() => Interlocked.Increment(ref _revision);

    private IInferenceBackend _local => _localBackend ??= new LocalBackend(this);

    private IInferenceBackend? _localBackend;

    private sealed class LocalBackend(AiHostRuntime runtime) : IInferenceBackend
    {
        public async Task HandleAsync(InferenceRoute route, ApiContext context)
        {
            Interlocked.Increment(ref runtime._localInFlight);
            try
            {
                await (route switch
                {
                    InferenceRoute.Capabilities => context.WriteJsonAsync(200, runtime.BuildCapabilities(),
                        Portal.AiHost.Contracts.AiHostJsonContext.Default.HostCapabilities),
                    InferenceRoute.Models => runtime.ListModelsAsync(context),
                    InferenceRoute.Performance => runtime.PerformanceEndpointAsync(context),
                    InferenceRoute.Health => runtime.HealthEndpointAsync(context),
                    InferenceRoute.EaseOffSet => runtime.EaseOffSetAsync(context),
                    InferenceRoute.EaseOffClear => runtime.EaseOffClearAsync(context),
                    InferenceRoute.Warmup => runtime.WarmupAsync(context),
                    InferenceRoute.Tokenize => runtime.TokenizeAsync(context),
                    InferenceRoute.ChatCompletions => runtime.ChatAsync(context),
                    InferenceRoute.Embeddings => runtime.EmbeddingsAsync(context),
                    _ => throw new ArgumentOutOfRangeException(nameof(route)),
                }).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref runtime._localInFlight);
            }
        }
    }
}
