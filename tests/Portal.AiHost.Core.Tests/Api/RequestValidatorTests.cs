using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Tests.Api;

public class RequestValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static ChatCompletionRequest Chat(string json) =>
        JsonSerializer.Deserialize(json, AiHostJsonContext.Default.ChatCompletionRequest)!;

    private static string? ParamOf(Action action) => Assert.Throws<ApiException>(action).Param;

    [Fact]
    public void DefaultsFollowTheApiTable()
    {
        var options = RequestValidator.ParseOptions(new Dictionary<string, string>(), Now);
        Assert.Equal(RequestPriority.Interactive, options.Priority);
        Assert.Equal(Now.AddMilliseconds(ApiLimits.DefaultInteractiveDeadlineMs), options.Deadline);
        Assert.False(options.FailIfLoading);

        var batch = RequestValidator.ParseOptions(new Dictionary<string, string>
        {
            [HeaderNames.Priority] = "batch",
            [HeaderNames.Load] = "fail",
        }, Now);
        Assert.Equal(ApiLimits.DefaultBatchDeadlineMs, batch.DeadlineMs);
        Assert.True(batch.FailIfLoading);

        var explicitDeadline = RequestValidator.ParseOptions(new Dictionary<string, string>
        {
            [HeaderNames.DeadlineMs] = "3600000",
        }, Now);
        Assert.Equal(Now.AddHours(1), explicitDeadline.Deadline);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("3600001")]
    [InlineData("12.5")]
    [InlineData("-1000")]
    [InlineData("abc")]
    public void DeadlineOutOfRangeIsRefused(string value) =>
        Assert.Equal(HeaderNames.DeadlineMs, ParamOf(() => RequestValidator.ParseOptions(
            new Dictionary<string, string> { [HeaderNames.DeadlineMs] = value }, Now)));

    [Fact]
    public void ValidChatPasses() =>
        RequestValidator.ValidateChat(Chat("""
            {"model":"default","messages":[{"role":"system","content":"x"},{"role":"user","content":[{"type":"text","text":"hi"}]}],
             "n":1,"temperature":0.7,"response_format":{"type":"json_schema","json_schema":{"schema":{"type":"object"}}},
             "reasoning_effort":"none","stop":["a","b"]}
            """));

    [Theory]
    [InlineData("""{"messages":[]}""", "messages", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"robot","content":"x"}]}""", "messages[0].role", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"image_url"}]}]}""", "messages[0].content[0]", ErrorCodes.UnsupportedParameter)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"n":2}""", "n", ErrorCodes.UnsupportedParameter)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"logprobs":true}""", "logprobs", ErrorCodes.UnsupportedParameter)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"max_tokens":0}""", "max_tokens", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"temperature":3}""", "temperature", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"stop":["1","2","3","4","5"]}""", "stop", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"response_format":{"type":"xml"}}""", "response_format.type", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"response_format":{"type":"json_schema"}}""", "response_format.json_schema", ErrorCodes.InvalidRequest)]
    [InlineData("""{"messages":[{"role":"user","content":"x"}],"reasoning_effort":"max"}""", "reasoning_effort", ErrorCodes.InvalidRequest)]
    public void InvalidChatIsRefused(string json, string param, string code)
    {
        var ex = Assert.Throws<ApiException>(() => RequestValidator.ValidateChat(Chat(json)));
        Assert.Equal(param, ex.Param);
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void EmbeddingsRules()
    {
        RequestValidator.ValidateEmbeddings(new EmbeddingsRequest { Input = new StringOrList("a", "b") }, 2);
        Assert.Equal("input", ParamOf(() => RequestValidator.ValidateEmbeddings(
            new EmbeddingsRequest { Input = new StringOrList("a", "b", "c") }, 2)));
        Assert.Equal("input", ParamOf(() => RequestValidator.ValidateEmbeddings(new EmbeddingsRequest(), 2)));
        Assert.Equal("input[1]", ParamOf(() => RequestValidator.ValidateEmbeddings(
            new EmbeddingsRequest { Input = new StringOrList("a", "") }, 2)));
        Assert.Equal("encoding_format", ParamOf(() => RequestValidator.ValidateEmbeddings(
            new EmbeddingsRequest { Input = new StringOrList("a"), EncodingFormat = "int8" }, 2)));
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("Application/JSON; charset=utf-8", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/json-patch+json", false)]
    [InlineData(null, false)]
    public void JsonContentType(string? value, bool ok) => Assert.Equal(ok, RequestValidator.IsJsonContentType(value));
}
