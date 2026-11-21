using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Scheduling;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Api;

public sealed class ApiPipelineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ClientRegistry _registry;
    private readonly FairScheduler _scheduler;
    private readonly ApiPipeline _pipeline;

    public ApiPipelineTests()
    {
        _registry = new ClientRegistry(new ClientRegistryOptions { DataDirectory = _dir.Path }, new FakeKeyProtector(), _time);
        _scheduler = new FairScheduler(time: _time);
        _pipeline = new ApiPipeline(_registry, new ApiPipelineOptions { MaxRequestBytes = 1024 }, _time);
        _pipeline.Map("GET", "/test/echo", ctx => ctx.WriteJsonAsync(200, ctx.Principal!.AppId, AiHostJsonContext.Default.String));
        _pipeline.Map("POST", "/test/slot", async ctx =>
        {
            await using var lease = await _scheduler.AcquireAsync(ctx.CreateSchedulingRequest(ModelKinds.Chat), null, ctx.Aborted);
            ctx.SetQueueWait(lease.QueueWait);
            await ctx.WriteJsonAsync(200, ctx.Options.Priority.ToString(), AiHostJsonContext.Default.String);
        });
        _pipeline.Map("POST", "/test/stream-fails", async ctx =>
        {
            await ctx.StartStreamAsync();
            await ctx.WriteProgressAsync(new PortalProgressEvent { Event = ProgressEvents.Started });
            throw new EngineCrashedException("engine gone with secret prompt text");
        });
        _pipeline.Map("GET", "/test/throws", _ => throw new InvalidOperationException("C:\\secret\\path prompt text"));
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private static ApiRequest Req(string method, string path, string? body = null, string? token = null,
        CallerIdentity? caller = null, Dictionary<string, string>? headers = null, Dictionary<string, string>? query = null)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body is not null)
        {
            h["Content-Type"] = "application/json; charset=utf-8";
        }

        if (token is not null)
        {
            h["Authorization"] = "Bearer " + token;
        }

        foreach (var (k, v) in headers ?? new())
        {
            h[k] = v;
        }

        return new ApiRequest
        {
            Method = method,
            Path = path,
            Headers = h,
            Query = query ?? new Dictionary<string, string>(),
            Body = body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body),
            Caller = caller,
        };
    }

    private async Task<RecordingSink> Send(ApiRequest request)
    {
        var sink = new RecordingSink();
        await _pipeline.HandleAsync(request, sink);
        Assert.True(sink.Completed);
        return sink;
    }

    private static (string Code, string Type, string? Param) Error(RecordingSink sink)
    {
        var error = JsonSerializer.Deserialize(sink.BodyText, AiHostJsonContext.Default.ApiError)!.Error;
        Assert.Equal(ErrorCatalog.Get(error.Code).Type, error.Type);
        Assert.Equal(ErrorCatalog.Get(error.Code).HttpStatus, sink.Status);
        return (error.Code, error.Type, error.Param);
    }

    private async Task<string> GetTokenAsync(string appId = "com.mindthaapp.voiceportal")
    {
        var created = await Send(Req("POST", Routes.AuthRequests,
            $"{{\"app_id\":\"{appId}\",\"app_name\":\"Voice Portal\"}}", caller: Callers.Packaged()));
        Assert.Equal(202, created.Status);
        var id = JsonSerializer.Deserialize(created.BodyText, AiHostJsonContext.Default.AuthRequestCreated)!.RequestId;
        await _registry.ApproveAsync(id, CancellationToken.None);
        var poll = await Send(Req("GET", Routes.AuthRequests + "/" + id, caller: Callers.Packaged()));
        var status = JsonSerializer.Deserialize(poll.BodyText, AiHostJsonContext.Default.AuthRequestStatus)!;
        Assert.Equal(AuthStatuses.Approved, status.Status);
        return status.Token!;
    }

    [Fact]
    public async Task AuthFlowOverThePipeline()
    {
        var token = await GetTokenAsync();
        var echo = await Send(Req("GET", "/test/echo", token: token));
        Assert.Equal(200, echo.Status);
        Assert.Equal("\"com.mindthaapp.voiceportal\"", echo.BodyText);
        Assert.Matches("^req_[0-9a-f]{24}$", echo.Headers[HeaderNames.RequestId]);

        var signOut = await Send(Req("DELETE", Routes.AuthToken, token: token));
        Assert.Equal(204, signOut.Status);
        Assert.Equal(ErrorCodes.TokenRevoked, Error(await Send(Req("GET", "/test/echo", token: token))).Code);
    }

    [Fact]
    public async Task PollReplayReturnsClaimedWithoutToken()
    {
        var created = await Send(Req("POST", Routes.AuthRequests,
            "{\"app_id\":\"com.example.app\",\"app_name\":\"App\"}", caller: Callers.Packaged()));
        var id = JsonSerializer.Deserialize(created.BodyText, AiHostJsonContext.Default.AuthRequestCreated)!.RequestId;
        await _registry.ApproveAsync(id, CancellationToken.None);
        await Send(Req("GET", Routes.AuthRequests + "/" + id, caller: Callers.Packaged()));

        var replay = await Send(Req("GET", Routes.AuthRequests + "/" + id, caller: Callers.Packaged()));
        Assert.Equal(200, replay.Status);
        Assert.Contains("\"status\":\"claimed\"", replay.BodyText);
        Assert.DoesNotContain("\"token\"", replay.BodyText);
    }

    [Fact]
    public async Task AccessRequestNeedsAnIdentifiedSameUserCaller()
    {
        const string body = "{\"app_id\":\"com.example.app\",\"app_name\":\"App\"}";
        Assert.Equal(ErrorCodes.AccessDenied, Error(await Send(Req("POST", Routes.AuthRequests, body))).Code);
        Assert.Equal(ErrorCodes.AccessDenied,
            Error(await Send(Req("POST", Routes.AuthRequests, body, caller: Callers.OtherUser()))).Code);

        var created = await Send(Req("POST", Routes.AuthRequests, body, caller: Callers.Packaged()));
        var id = JsonSerializer.Deserialize(created.BodyText, AiHostJsonContext.Default.AuthRequestCreated)!.RequestId;
        await _registry.ApproveAsync(id, CancellationToken.None);

        // Another process cannot collect the token.
        var stolen = await Send(Req("GET", Routes.AuthRequests + "/" + id, caller: Callers.Unpackaged()));
        Assert.Equal(ErrorCodes.NotFound, Error(stolen).Code);
        Assert.DoesNotContain("pah_", stolen.BodyText);
    }

    [Fact]
    public async Task MissingOrBadTokensAreRefused()
    {
        Assert.Equal(ErrorCodes.AuthRequired, Error(await Send(Req("GET", "/test/echo"))).Code);
        Assert.Equal(ErrorCodes.AuthRequired, Error(await Send(Req("GET", "/test/echo",
            headers: new() { ["Authorization"] = "Basic abc" }))).Code);
        Assert.Equal(ErrorCodes.TokenInvalid, Error(await Send(Req("GET", "/test/echo", token: TokenFormat.NewToken()))).Code);
        Assert.Equal(ErrorCodes.TokenInvalid, Error(await Send(Req("GET", "/test/echo", token: "pah_x"))).Code);
    }

    [Fact]
    public async Task RequestRulesMapToCatalogCodes()
    {
        var token = await GetTokenAsync();
        Assert.Equal(ErrorCodes.NotFound, Error(await Send(Req("GET", "/nope", token: token))).Code);
        Assert.Equal(ErrorCodes.NotFound, Error(await Send(Req("PUT", "/test/echo", "{}", token: token))).Code);
        Assert.Equal(ErrorCodes.FeatureDisabled, Error(await Send(Req("POST", Routes.AudioTranscriptions, "{}"))).Code);
        Assert.Equal(ErrorCodes.ForbiddenOrigin, Error(await Send(Req("GET", "/test/echo", token: token,
            headers: new() { ["Origin"] = "https://evil.example" }))).Code);
        Assert.Equal(ErrorCodes.RequestTooLarge, Error(await Send(Req("POST", "/test/slot",
            "{\"x\":\"" + new string('a', 2000) + "\"}", token: token))).Code);

        var noJson = Req("POST", "/test/slot", "{}", token: token,
            headers: new() { ["Content-Type"] = "text/plain" });
        Assert.Equal(("invalid_request", "Content-Type"), Pick(Error(await Send(noJson))));

        Assert.Equal((ErrorCodes.InvalidRequest, HeaderNames.Priority), Pick(Error(await Send(Req("POST", "/test/slot", "{}",
            token: token, headers: new() { [HeaderNames.Priority] = "urgent" })))));
        Assert.Equal((ErrorCodes.InvalidRequest, HeaderNames.DeadlineMs), Pick(Error(await Send(Req("POST", "/test/slot", "{}",
            token: token, headers: new() { [HeaderNames.DeadlineMs] = "999" })))));
        Assert.Equal((ErrorCodes.InvalidRequest, HeaderNames.Load), Pick(Error(await Send(Req("POST", "/test/slot", "{}",
            token: token, headers: new() { [HeaderNames.Load] = "maybe" })))));
        Assert.Equal((ErrorCodes.InvalidRequest, HeaderNames.ClientRequestId), Pick(Error(await Send(Req("GET", "/test/echo",
            token: token, headers: new() { [HeaderNames.ClientRequestId] = "bad id!" })))));
        Assert.Equal((ErrorCodes.InvalidRequest, "app_name"), Pick(Error(await Send(Req("POST", Routes.AuthRequests,
            "{\"app_id\":\"com.example.app\",\"app_name\":7}", caller: Callers.Packaged())))));
        Assert.Equal((ErrorCodes.InvalidRequest, "wait_ms"), Pick(Error(await Send(Req("GET", Routes.AuthRequests + "/ar_x",
            caller: Callers.Packaged(), query: new() { ["wait_ms"] = "-5" })))));
        Assert.Equal(ErrorCodes.NotFound, Error(await Send(Req("GET", Routes.AuthRequests + "/ar_unknown",
            caller: Callers.Packaged()))).Code);
    }

    private static (string, string?) Pick((string Code, string Type, string? Param) e) => (e.Code, e.Param);

    [Fact]
    public async Task HeadersSetPriorityAndEchoClientRequestId()
    {
        var token = await GetTokenAsync();
        var sink = await Send(Req("POST", "/test/slot", "{}", token: token, headers: new()
        {
            [HeaderNames.Priority] = "batch",
            [HeaderNames.ClientRequestId] = "job-42.a_b",
        }));
        Assert.Equal(200, sink.Status);
        Assert.Equal("\"Batch\"", sink.BodyText);
        Assert.Equal("job-42.a_b", sink.Headers[HeaderNames.ClientRequestId]);
        Assert.Equal("0", sink.Headers[HeaderNames.QueueMs]);
    }

    [Fact]
    public async Task QueueFullBecomes429WithRetryAfter()
    {
        var token = await GetTokenAsync();
        var blocker = await _scheduler.AcquireAsync(new SchedulingRequest
        {
            AppId = "other.app", Deadline = _time.GetUtcNow().AddHours(1),
        }, null, default);

        var waiting = Enumerable.Range(0, 8).Select(_ => Send(Req("POST", "/test/slot", "{}", token: token))).ToList();
        var full = await Send(Req("POST", "/test/slot", "{}", token: token));
        Assert.Equal(ErrorCodes.QueueFull, Error(full).Code);
        Assert.Equal("2", full.Headers[HeaderNames.RetryAfter]);

        await blocker.DisposeAsync();
        foreach (var w in waiting)
        {
            Assert.Equal(200, (await w).Status);
        }
    }

    [Fact]
    public async Task ErrorAfterStreamStartIsSentInStreamWithoutDone()
    {
        var token = await GetTokenAsync();
        var sink = await Send(Req("POST", "/test/stream-fails", "{}", token: token));
        Assert.Equal(200, sink.Status);
        Assert.Single(sink.SseComments);
        var last = Assert.Single(sink.SseData);
        Assert.Contains("\"code\":\"engine_restarting\"", last);
        Assert.DoesNotContain("[DONE]", sink.SseData);
        Assert.DoesNotContain("secret", last);
    }

    [Fact]
    public async Task UnexpectedErrorsDoNotLeakDetails()
    {
        var token = await GetTokenAsync();
        var sink = await Send(Req("GET", "/test/throws", token: token));
        Assert.Equal(ErrorCodes.InternalError, Error(sink).Code);
        Assert.DoesNotContain("secret", sink.BodyText);
        Assert.DoesNotContain("prompt", sink.BodyText);
    }

    [Fact]
    public async Task ClientAbortWritesNothing()
    {
        var token = await GetTokenAsync();
        var blocker = await _scheduler.AcquireAsync(new SchedulingRequest
        {
            AppId = "other.app", Deadline = _time.GetUtcNow().AddHours(1),
        }, null, default);
        var sink = new RecordingSink();
        var call = _pipeline.HandleAsync(Req("POST", "/test/slot", "{}", token: token), sink);
        Assert.Equal(1, _scheduler.Snapshot().Waiting);

        sink.Abort();
        await call;
        Assert.Null(sink.Status);
        Assert.Equal(0, _scheduler.Snapshot().Waiting);
        await blocker.DisposeAsync();
    }

    [Fact]
    public void ApiDocListsTheCatalogTypeForEveryCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortalAIHost.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir.FullName, "docs", "api-v1.md"));
        foreach (var code in ErrorCatalog.AllCodes)
        {
            var info = ErrorCatalog.Get(code);
            var row = new Regex($@"^\|\s*{info.HttpStatus}\s*\|\s*`{code}`[^|]*\|\s*`{info.Type}`\s*\|", RegexOptions.Multiline);
            Assert.True(row.IsMatch(doc), $"api-v1.md has no row '| {info.HttpStatus} | `{code}` | `{info.Type}` |'");
        }
    }
}
