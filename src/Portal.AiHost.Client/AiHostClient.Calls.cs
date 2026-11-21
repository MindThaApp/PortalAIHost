using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

public sealed partial class AiHostClient
{
    /// <summary>
    /// Sends a chat request and returns the whole reply. When the host cannot answer and a fallback is configured,
    /// the fallback answers instead (see <see cref="FallbackActivated"/>); otherwise this throws
    /// <see cref="AiHostException"/>. <c>model</c> defaults to <c>default</c>.
    /// </summary>
    public async Task<ChatResult> ChatAsync(ChatCompletionRequest request, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var resp = await SendCoreAsync(HttpMethod.Post, "v1/chat/completions", BuildChatBody(request, false),
                options, true, false, cancellationToken).ConfigureAwait(false);
            var parsed = await ReadJsonAsync(resp, AiHostJsonContext.Default.ChatCompletionResponse, cancellationToken)
                .ConfigureAwait(false);
            SetState(AiHostState.Ready);
            return new ChatResult
            {
                Text = parsed.Choices.FirstOrDefault()?.Message.Content ?? "",
                Response = parsed,
                Model = ModelHeader(resp) ?? parsed.Model,
                Source = AiSource.Host,
            };
        }
        catch (AiHostException ex) when (CanFallback(ex, options))
        {
            var reason = RaiseFallback(ex);
            var fallback = _options.Fallback!;
            var text = await fallback.ChatAsync(request, cancellationToken).ConfigureAwait(false);
            return new ChatResult { Text = text, Model = fallback.ModelId, Source = AiSource.Fallback, FallbackReason = reason };
        }
    }

    /// <summary>
    /// Sends a streaming chat request. Queue and load progress arrive as pieces with <see cref="ChatDelta.Progress"/>.
    /// If the host fails before the first reply text, the fallback (if any) answers; after the first text,
    /// nothing is retried and an <see cref="AiHostException"/> is thrown. Cancel the token to stop generation.
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> StreamChatAsync(ChatCompletionRequest request, RequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AiHostException? failure = null;
        HttpResponseMessage? response = null;
        try
        {
            response = await SendCoreAsync(HttpMethod.Post, "v1/chat/completions", BuildChatBody(request, true),
                options, true, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AiHostException ex)
        {
            failure = ex;
        }

        if (response is not null)
        {
            using (response)
            {
                var model = ModelHeader(response);
                var gotText = false;
                using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(body, Encoding.UTF8);
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        failure = new AiHostException(ClientErrorCodes.StreamInterrupted, "The reply stream was interrupted.");
                        break;
                    }
                    catch (HttpRequestException)
                    {
                        failure = new AiHostException(ClientErrorCodes.StreamInterrupted, "The reply stream was interrupted.");
                        break;
                    }

                    if (line is null)
                    {
                        if (!gotText)
                        {
                            failure = new AiHostException(ClientErrorCodes.StreamInterrupted, "The reply ended without text.");
                        }

                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (line[0] == ':')
                    {
                        if (AiHostJson.TryParseProgressComment(line) is { } progress)
                        {
                            yield return new ChatDelta { Progress = progress, Model = model, Source = AiSource.Host };
                        }

                        continue;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var data = line.AsSpan(5).TrimStart().ToString();
                    if (data == "[DONE]")
                    {
                        break;
                    }

                    if (TryParseStreamError(data) is { } streamError)
                    {
                        failure = streamError;
                        break;
                    }

                    var delta = ToDelta(data, model);
                    if (delta is null)
                    {
                        continue;
                    }

                    if (delta.Text is { Length: > 0 } || delta.ReasoningText is { Length: > 0 } || delta.ToolCalls is not null)
                    {
                        gotText = true;
                    }

                    yield return delta;
                }

                if (failure is null)
                {
                    SetState(AiHostState.Ready);
                    yield break;
                }

                if (gotText)
                {
                    await ApplyFailureAsync(failure, cancellationToken).ConfigureAwait(false);
                    throw failure; // nothing is retried or sent to fallback after the first streamed text
                }
            }
        }

        if (failure is null)
        {
            yield break;
        }

        if (response is not null)
        {
            await ApplyFailureAsync(failure, cancellationToken).ConfigureAwait(false);
        }

        if (!CanFallback(failure, options))
        {
            throw failure;
        }

        var reason = RaiseFallback(failure);
        var fallback = _options.Fallback!;
        await foreach (var piece in fallback.StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return new ChatDelta { Text = piece, Model = fallback.ModelId, Source = AiSource.Fallback, FallbackReason = reason };
        }
    }

    /// <summary>
    /// Embeds one or more texts. Embeddings fall back only if <see cref="AiHostClientOptions.AllowEmbeddingFallback"/>
    /// is set and the fallback supports them; otherwise failures throw <see cref="AiHostException"/>.
    /// Store <see cref="EmbedResult.ModelId"/> with the vectors.
    /// </summary>
    public async Task<EmbedResult> EmbedAsync(IReadOnlyList<string> inputs, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var request = new EmbeddingsRequest
        {
            Model = "default-embedding",
            Input = new StringOrList { Values = inputs.ToList(), IsSingle = false },
            EncodingFormat = "float",
        };
        var body = AiHostJson.SerializeToUtf8(request, AiHostJsonContext.Default.EmbeddingsRequest);
        try
        {
            using var resp = await SendCoreAsync(HttpMethod.Post, "v1/embeddings", body, options, true, false, cancellationToken)
                .ConfigureAwait(false);
            var parsed = await ReadJsonAsync(resp, AiHostJsonContext.Default.EmbeddingsResponse, cancellationToken)
                .ConfigureAwait(false);
            SetState(AiHostState.Ready);
            return new EmbedResult
            {
                Vectors = parsed.Data.OrderBy(d => d.Index).Select(d => d.Embedding.Values ?? []).ToList(),
                ModelId = ModelHeader(resp) ?? parsed.Model,
                Source = AiSource.Host,
            };
        }
        catch (AiHostException ex) when (_options.AllowEmbeddingFallback && _options.Fallback is { SupportsEmbeddings: true }
            && CanFallback(ex, options))
        {
            var reason = RaiseFallback(ex);
            var fallback = _options.Fallback;
            var vectors = await fallback.EmbedAsync(inputs, cancellationToken).ConfigureAwait(false);
            return new EmbedResult { Vectors = vectors, ModelId = fallback.ModelId, Source = AiSource.Fallback, FallbackReason = reason };
        }
    }

    private static byte[] BuildChatBody(ChatCompletionRequest request, bool stream)
    {
        var node = JsonSerializer.SerializeToNode(request, AiHostJsonContext.Default.ChatCompletionRequest)!.AsObject();
        node["stream"] = stream;
        if (!node.ContainsKey("model"))
        {
            node["model"] = "default";
        }

        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private static string? ModelHeader(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues(HeaderNames.Model, out var values) ? values.FirstOrDefault() : null;

    private static AiHostException? TryParseStreamError(string data)
    {
        if (!data.Contains("\"error\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("error", out _))
            {
                return null;
            }

            var body = AiHostJson.Deserialize(data, AiHostJsonContext.Default.ApiError)?.Error;
            return new AiHostException(body?.Code ?? ErrorCodes.InternalError, body?.Message ?? "The host reported an error.",
                200, body?.RetryAfterMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatDelta? ToDelta(string data, string? model)
    {
        ChatCompletionChunk? chunk;
        try
        {
            chunk = AiHostJson.Deserialize(data, AiHostJsonContext.Default.ChatCompletionChunk);
        }
        catch (JsonException)
        {
            return null;
        }

        if (chunk is null)
        {
            return null;
        }

        var choice = chunk.Choices.FirstOrDefault();
        if (choice is null)
        {
            return chunk.Usage is null ? null : new ChatDelta { Usage = chunk.Usage, Model = model ?? chunk.Model, Source = AiSource.Host };
        }

        if (choice.Delta.Content is null && choice.Delta.ReasoningContent is null && choice.Delta.ToolCalls is null
            && choice.FinishReason is null && chunk.Usage is null)
        {
            return null;
        }

        return new ChatDelta
        {
            Text = choice.Delta.Content,
            ReasoningText = choice.Delta.ReasoningContent,
            ToolCalls = choice.Delta.ToolCalls,
            FinishReason = choice.FinishReason,
            Usage = chunk.Usage,
            Model = model ?? chunk.Model,
            Source = AiSource.Host,
        };
    }
}
