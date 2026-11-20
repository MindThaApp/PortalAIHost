using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portal.AiHost.Contracts;

// OpenAI-compatible chat and embeddings shapes (api-v1.md section 6). Only the fields the
// host supports are modelled; unknown fields are ignored on read.

// ---- chat request -----------------------------------------------------------

/// <summary>POST /v1/chat/completions body.</summary>
public sealed class ChatCompletionRequest
{
    public string? Model { get; init; }
    public List<ChatMessage> Messages { get; init; } = new();
    public bool? Stream { get; init; }
    public StreamOptions? StreamOptions { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxCompletionTokens { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public StringOrList? Stop { get; init; }
    public long? Seed { get; init; }
    public double? PresencePenalty { get; init; }
    public double? FrequencyPenalty { get; init; }
    public ResponseFormat? ResponseFormat { get; init; }

    /// <summary>none (default), low, medium or high.</summary>
    public string? ReasoningEffort { get; init; }

    public List<ToolDefinition>? Tools { get; init; }

    /// <summary>"auto", "none", "required" or a named-function object; kept as raw JSON.</summary>
    public JsonElement? ToolChoice { get; init; }

    public bool? ParallelToolCalls { get; init; }

    /// <summary>Parsed only so n greater than 1 can be refused with unsupported_parameter.</summary>
    public int? N { get; init; }

    /// <summary>Parsed only so logprobs can be refused with unsupported_parameter.</summary>
    public JsonElement? Logprobs { get; init; }

    public string? User { get; init; }
}

public sealed class StreamOptions
{
    public bool? IncludeUsage { get; init; }
}

public sealed class ResponseFormat
{
    /// <summary>text, json_object or json_schema.</summary>
    public string Type { get; init; } = "text";

    public JsonSchemaFormat? JsonSchema { get; init; }
}

public sealed class JsonSchemaFormat
{
    public string? Name { get; init; }
    public JsonElement? Schema { get; init; }
    public bool? Strict { get; init; }
}

public sealed class ChatMessage
{
    public string Role { get; init; } = ChatRoles.User;
    public ChatContent? Content { get; init; }
    public string? Name { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ReasoningContent { get; init; }
}

/// <summary>Message content: a plain string or an array of text parts.</summary>
[JsonConverter(typeof(ChatContentConverter))]
public sealed class ChatContent
{
    public ChatContent() { }

    public ChatContent(string text) => Text = text;

    /// <summary>Set when the wire form is a string.</summary>
    public string? Text { get; init; }

    /// <summary>Set when the wire form is an array of parts.</summary>
    public List<ContentPart>? Parts { get; init; }

    /// <summary>All text, with parts concatenated.</summary>
    public string AsText() => Text ?? (Parts is null ? "" : string.Concat(Parts.Select(p => p.Text)));

    public static implicit operator ChatContent(string text) => new(text);
}

public sealed class ContentPart
{
    /// <summary>Only "text" is supported in v1; image and audio parts are refused.</summary>
    public string Type { get; init; } = "text";

    public string? Text { get; init; }
}

public sealed class ToolDefinition
{
    public string Type { get; init; } = "function";
    public FunctionDefinition Function { get; init; } = new();
}

public sealed class FunctionDefinition
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>JSON-schema object, passed through untouched.</summary>
    public JsonElement? Parameters { get; init; }

    public bool? Strict { get; init; }
}

public sealed class ToolCall
{
    /// <summary>Present in streamed fragments only.</summary>
    public int? Index { get; init; }

    public string? Id { get; init; }
    public string? Type { get; init; }
    public FunctionCall? Function { get; init; }
}

public sealed class FunctionCall
{
    public string? Name { get; init; }
    public string? Arguments { get; init; }
}

// ---- chat response ----------------------------------------------------------

/// <summary>Non-streaming chat.completion response.</summary>
public sealed class ChatCompletionResponse
{
    public string Id { get; init; } = "";
    public string Object { get; init; } = "chat.completion";
    public long Created { get; init; }
    public string Model { get; init; } = "";
    public List<ChatChoice> Choices { get; init; } = new();
    public Usage? Usage { get; init; }
}

public sealed class ChatChoice
{
    public int Index { get; init; }
    public ChatResponseMessage Message { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; init; }
}

public sealed class ChatResponseMessage
{
    public string Role { get; init; } = ChatRoles.Assistant;
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
}

public sealed class Usage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>One streamed chat.completion.chunk event.</summary>
public sealed class ChatCompletionChunk
{
    public string Id { get; init; } = "";
    public string Object { get; init; } = "chat.completion.chunk";
    public long Created { get; init; }
    public string Model { get; init; } = "";
    public List<ChatChunkChoice> Choices { get; init; } = new();

    /// <summary>Only on the final chunk when stream_options.include_usage is set.</summary>
    public Usage? Usage { get; init; }
}

public sealed class ChatChunkChoice
{
    public int Index { get; init; }
    public ChatDeltaMessage Delta { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? FinishReason { get; init; }
}

public sealed class ChatDeltaMessage
{
    public string? Role { get; init; }
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
}

// ---- embeddings -------------------------------------------------------------

/// <summary>POST /v1/embeddings body.</summary>
public sealed class EmbeddingsRequest
{
    public string? Model { get; init; }
    public StringOrList Input { get; init; } = new();

    /// <summary>float (default) or base64.</summary>
    public string? EncodingFormat { get; init; }
}

public sealed class EmbeddingsResponse
{
    public string Object { get; init; } = "list";
    public List<EmbeddingData> Data { get; init; } = new();
    public string Model { get; init; } = "";
    public EmbeddingsUsage? Usage { get; init; }
}

public sealed class EmbeddingData
{
    public string Object { get; init; } = "embedding";
    public int Index { get; init; }
    public EmbeddingVector Embedding { get; init; } = new();
}

public sealed class EmbeddingsUsage
{
    public int PromptTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>A vector as a float array, or a base64 string of little-endian float32 values.</summary>
[JsonConverter(typeof(EmbeddingVectorConverter))]
public sealed class EmbeddingVector
{
    public float[]? Values { get; init; }
    public string? Base64 { get; init; }

    public static EmbeddingVector FromFloats(float[] values) => new() { Values = values };

    public static EmbeddingVector FromFloatsAsBase64(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            for (var i = 0; i < bytes.Length; i += 4)
            {
                Array.Reverse(bytes, i, 4);
            }
        }

        return new EmbeddingVector { Base64 = Convert.ToBase64String(bytes) };
    }

    /// <summary>Returns the floats whichever wire form was used.</summary>
    public float[] ToFloats()
    {
        if (Values is not null)
        {
            return Values;
        }

        var bytes = Convert.FromBase64String(Base64 ?? "");
        if (!BitConverter.IsLittleEndian)
        {
            for (var i = 0; i + 4 <= bytes.Length; i += 4)
            {
                Array.Reverse(bytes, i, 4);
            }
        }

        var result = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, result, 0, result.Length * sizeof(float));
        return result;
    }
}

/// <summary>A JSON string or an array of strings (the stop field, the embeddings input).</summary>
[JsonConverter(typeof(StringOrListConverter))]
public sealed class StringOrList
{
    public StringOrList() { }

    public StringOrList(params string[] values) => Values = new List<string>(values);

    public List<string> Values { get; init; } = new();

    /// <summary>True when the wire form is a bare string rather than an array.</summary>
    public bool IsSingle { get; init; }

    public static implicit operator StringOrList(string value) => new(value) { IsSingle = true };
}

// ---- converters ------------------------------------------------------------

internal sealed class ChatContentConverter : JsonConverter<ChatContent>
{
    public override ChatContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var element = JsonElement.ParseValue(ref reader);
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return new ChatContent { Text = element.GetString() };
            case JsonValueKind.Array:
                var parts = new List<ContentPart>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("Content parts must be objects.");
                    }

                    var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? "text"
                        : "text";
                    var text = item.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String
                        ? x.GetString()
                        : null;
                    parts.Add(new ContentPart { Type = type, Text = text });
                }

                return new ChatContent { Parts = parts };
            default:
                throw new JsonException("Message content must be a string or an array of parts.");
        }
    }

    public override void Write(Utf8JsonWriter writer, ChatContent value, JsonSerializerOptions options)
    {
        if (value.Parts is null)
        {
            writer.WriteStringValue(value.Text ?? "");
            return;
        }

        writer.WriteStartArray();
        foreach (var part in value.Parts)
        {
            writer.WriteStartObject();
            writer.WriteString("type", part.Type);
            if (part.Text is not null)
            {
                writer.WriteString("text", part.Text);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

internal sealed class StringOrListConverter : JsonConverter<StringOrList>
{
    public override StringOrList? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new StringOrList { Values = new List<string> { reader.GetString() ?? "" }, IsSingle = true };
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a string or an array of strings.");
        }

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected an array of strings.");
            }

            list.Add(reader.GetString() ?? "");
        }

        return new StringOrList { Values = list };
    }

    public override void Write(Utf8JsonWriter writer, StringOrList value, JsonSerializerOptions options)
    {
        if (value.IsSingle && value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var v in value.Values)
        {
            writer.WriteStringValue(v);
        }

        writer.WriteEndArray();
    }
}

internal sealed class EmbeddingVectorConverter : JsonConverter<EmbeddingVector>
{
    public override EmbeddingVector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new EmbeddingVector { Base64 = reader.GetString() };
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a float array or a base64 string.");
        }

        var values = new List<float>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetSingle());
        }

        return new EmbeddingVector { Values = values.ToArray() };
    }

    public override void Write(Utf8JsonWriter writer, EmbeddingVector value, JsonSerializerOptions options)
    {
        if (value.Values is null)
        {
            writer.WriteStringValue(value.Base64 ?? "");
            return;
        }

        writer.WriteStartArray();
        foreach (var v in value.Values)
        {
            writer.WriteNumberValue(v);
        }

        writer.WriteEndArray();
    }
}
