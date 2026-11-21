using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

/// <summary>
/// The app's own built-in model, used when Portal AI Host cannot answer. Implementations must not call back into
/// the <see cref="AiHostClient"/>.
/// </summary>
public interface IAiFallback
{
    /// <summary>A name for the fallback model, reported as the result's model.</summary>
    string ModelId { get; }

    /// <summary>Whether <see cref="EmbedAsync"/> is implemented. Also needs <c>AllowEmbeddingFallback</c>.</summary>
    bool SupportsEmbeddings => false;

    /// <summary>Answers a chat request and returns the full reply text.</summary>
    Task<string> ChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Answers a chat request as a stream of text pieces.</summary>
    IAsyncEnumerable<string> StreamChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Embeds the inputs. Vectors from different models must not be mixed.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fallback does not support embeddings.");
}
