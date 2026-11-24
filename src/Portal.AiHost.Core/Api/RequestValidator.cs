using System.Globalization;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Api;

/// <summary>Header parsing and body validation for api-v1 requests. Errors are ApiExceptions with param set.</summary>
public static class RequestValidator
{
    public const int MaxStopSequences = 4;
    public const int MaxRouteValueLength = 64;

    private static readonly string[] ReasoningEfforts = ["none", "low", "medium", "high"];
    private static readonly string[] Roles = [ChatRoles.System, ChatRoles.User, ChatRoles.Assistant, ChatRoles.Tool];

    /// <summary>Parses X-Portal-Priority, X-Portal-Deadline-Ms, X-Portal-Load and X-Portal-Client-Request-Id.</summary>
    public static PortalRequestOptions ParseOptions(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var priority = RequestPriority.Interactive;
        if (headers.TryGetValue(HeaderNames.Priority, out var p))
        {
            priority = p.Trim() switch
            {
                PriorityValues.Interactive => RequestPriority.Interactive,
                PriorityValues.Batch => RequestPriority.Batch,
                _ => throw new ApiException(ErrorCodes.InvalidRequest,
                    "X-Portal-Priority must be interactive or batch.", HeaderNames.Priority),
            };
        }

        var deadlineMs = priority == RequestPriority.Batch
            ? ApiLimits.DefaultBatchDeadlineMs
            : ApiLimits.DefaultInteractiveDeadlineMs;
        if (headers.TryGetValue(HeaderNames.DeadlineMs, out var d))
        {
            if (!int.TryParse(d.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out deadlineMs) ||
                deadlineMs < ApiLimits.MinDeadlineMs || deadlineMs > ApiLimits.MaxDeadlineMs)
            {
                throw new ApiException(ErrorCodes.InvalidRequest,
                    $"X-Portal-Deadline-Ms must be a whole number from {ApiLimits.MinDeadlineMs} to {ApiLimits.MaxDeadlineMs}.",
                    HeaderNames.DeadlineMs);
            }
        }

        var failIfLoading = false;
        if (headers.TryGetValue(HeaderNames.Load, out var l))
        {
            failIfLoading = l.Trim() switch
            {
                LoadBehaviorValues.Wait => false,
                LoadBehaviorValues.Fail => true,
                _ => throw new ApiException(ErrorCodes.InvalidRequest, "X-Portal-Load must be wait or fail.",
                    HeaderNames.Load),
            };
        }

        // X-Portal-Context-Tokens is a hint: a value that is not a whole number is ignored, never an error.
        int? contextTokens = null;
        if (headers.TryGetValue(HeaderNames.ContextTokens, out var c) &&
            long.TryParse(c.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var asked))
        {
            contextTokens = (int)Math.Min(asked, int.MaxValue);
        }

        headers.TryGetValue(HeaderNames.ClientRequestId, out var clientId);
        return new PortalRequestOptions(priority, deadlineMs, now.AddMilliseconds(deadlineMs), failIfLoading,
            clientId is not null && IsValidClientRequestId(clientId) ? clientId : null, contextTokens);
    }

    public static bool IsValidClientRequestId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ApiLimits.MaxClientRequestIdLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One path segment for a {placeholder}: [A-Za-z0-9_-], 1 to 64 characters.</summary>
    public static bool IsRouteValue(string segment)
    {
        if (segment.Length is 0 or > MaxRouteValueLength)
        {
            return false;
        }

        foreach (var c in segment)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>application/json, optionally with parameters such as charset=utf-8.</summary>
    public static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var media = contentType.Split(';', 2)[0].Trim();
        return string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks a chat request against what v1 supports (api-v1 "POST /v1/chat/completions").</summary>
    public static void ValidateChat(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is not { Count: > 0 })
        {
            throw Invalid("messages must contain at least one message.", "messages");
        }

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var m = request.Messages[i] ?? throw Invalid("A message is null.", $"messages[{i}]");
            if (Array.IndexOf(Roles, m.Role) < 0)
            {
                throw Invalid("role must be system, user, assistant or tool.", $"messages[{i}].role");
            }

            if (m.Content?.Parts is { } parts)
            {
                for (var j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is null || !string.Equals(parts[j].Type, "text", StringComparison.Ordinal))
                    {
                        throw Unsupported("Only text content parts are supported.", $"messages[{i}].content[{j}]");
                    }
                }
            }
        }

        if (request.N is { } n)
        {
            if (n < 1)
            {
                throw Invalid("n must be 1.", "n");
            }

            if (n > 1)
            {
                throw Unsupported("n > 1 is not supported.", "n");
            }
        }

        if (request.Logprobs is { } lp && lp.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
        {
            throw Unsupported("logprobs is not supported.", "logprobs");
        }

        Positive(request.MaxTokens, "max_tokens");
        Positive(request.MaxCompletionTokens, "max_completion_tokens");
        Range(request.Temperature, 0, 2, "temperature");
        Range(request.TopP, 0, 1, "top_p");
        Range(request.PresencePenalty, -2, 2, "presence_penalty");
        Range(request.FrequencyPenalty, -2, 2, "frequency_penalty");

        if (request.Stop is { Values.Count: > MaxStopSequences })
        {
            throw Invalid($"stop allows at most {MaxStopSequences} sequences.", "stop");
        }

        if (request.ResponseFormat is { } rf)
        {
            switch (rf.Type)
            {
                case "text":
                case "json_object":
                    break;
                case "json_schema":
                    if (rf.JsonSchema?.Schema is not { ValueKind: JsonValueKind.Object })
                    {
                        throw Invalid("json_schema.schema must be a JSON object.", "response_format.json_schema");
                    }

                    break;
                default:
                    throw Invalid("response_format.type must be text, json_object or json_schema.",
                        "response_format.type");
            }
        }

        if (request.ReasoningEffort is { } effort && Array.IndexOf(ReasoningEfforts, effort) < 0)
        {
            throw Invalid("reasoning_effort must be none, low, medium or high.", "reasoning_effort");
        }
    }

    /// <summary>Checks an embeddings request (input count, empty items, encoding format).</summary>
    public static void ValidateEmbeddings(EmbeddingsRequest request, int maxBatchInputs)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inputs = request.Input?.Values;
        if (inputs is not { Count: > 0 })
        {
            throw Invalid("input must be a string or a non-empty array of strings.", "input");
        }

        if (inputs.Count > maxBatchInputs)
        {
            throw Invalid($"input allows at most {maxBatchInputs} items.", "input");
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            if (string.IsNullOrEmpty(inputs[i]))
            {
                throw Invalid("input items must not be empty.", $"input[{i}]");
            }
        }

        if (request.EncodingFormat is { } f && f is not ("float" or "base64"))
        {
            throw Invalid("encoding_format must be float or base64.", "encoding_format");
        }
    }

    private static void Positive(int? value, string param)
    {
        if (value is < 1)
        {
            throw Invalid($"{param} must be at least 1.", param);
        }
    }

    private static void Range(double? value, double min, double max, string param)
    {
        if (value is { } v && (double.IsNaN(v) || v < min || v > max))
        {
            throw Invalid(
                string.Create(CultureInfo.InvariantCulture, $"{param} must be between {min} and {max}."), param);
        }
    }

    private static ApiException Invalid(string message, string param) =>
        new(ErrorCodes.InvalidRequest, message, param);

    private static ApiException Unsupported(string message, string param) =>
        new(ErrorCodes.UnsupportedParameter, message, param);
}
