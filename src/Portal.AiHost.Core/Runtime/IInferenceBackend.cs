using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Runtime;

/// <summary>The endpoints that run on whatever machine does the AI work (docs/client-mode-v1.md section 4.2).</summary>
public enum InferenceRoute
{
    Capabilities,
    Models,
    Performance,
    Health,
    EaseOffSet,
    EaseOffClear,
    Warmup,
    Tokenize,
    ChatCompletions,
    Embeddings,
}

/// <summary>
/// Where the model-related endpoints are answered: on this machine (<see cref="AiHostRuntime"/>'s own engine) or, in
/// client mode, on the paired PC. The loopback pipeline in front (host, origin, body limit, bearer token, queue
/// limits) is the same for both; only the handler behind the route changes.
/// </summary>
public interface IInferenceBackend
{
    /// <summary>Answers the request through <paramref name="context"/>, which is already authenticated for the local app.</summary>
    Task HandleAsync(InferenceRoute route, ApiContext context);
}
