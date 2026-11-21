using System.Globalization;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;

namespace Portal.AiHost.Core.Api;

public sealed class ApiPipelineOptions
{
    public int MaxRequestBytes { get; init; } = ApiLimits.MaxRequestBytes;

    /// <summary>Longest wait_ms accepted on GET /host/auth/requests/{id}.</summary>
    public int MaxAuthWaitMs { get; init; } = 60_000;

    public int EngineRestartRetryAfterMs { get; init; } = 2000;
}

/// <summary>
/// Transport-independent request pipeline (architecture section 3, "transport seam"). A transport adapter
/// (Kestrel today, LAN TLS and the relay later) hands in a parsed <see cref="ApiRequest"/> and an
/// <see cref="IResponseSink"/>. The pipeline applies, in order: request id and client request id, the Origin
/// refusal, the body size limit, routing, the JSON content type rule, bearer authentication, the X-Portal-*
/// headers, and then the route handler. Every failure leaves as the api-v1 error envelope from
/// <see cref="ErrorCatalog"/>; after a stream has started it is sent as one in-stream error event.
/// The auth routes (/host/auth/*) are built in; model routes are mapped by the host.
/// </summary>
public sealed class ApiPipeline
{
    private readonly IClientRegistry _registry;
    private readonly ApiPipelineOptions _options;
    private readonly TimeProvider _time;
    private readonly List<Route> _routes = new();

    public ApiPipeline(IClientRegistry registry, ApiPipelineOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _options = options ?? new ApiPipelineOptions();
        _time = time ?? TimeProvider.System;

        Map("POST", Routes.AuthRequests, CreateAccessRequestAsync, requiresAuth: false);
        Map("GET", Routes.AuthRequests + "/{id}", PollAccessRequestAsync, requiresAuth: false);
        Map("DELETE", Routes.AuthToken, RevokeOwnTokenAsync);
        MapDisabled("POST", Routes.AudioTranscriptions);
    }

    /// <summary>
    /// Adds a route. A template segment "{name}" matches one segment of [A-Za-z0-9_-], at most 64 characters.
    /// Routes need a valid token unless <paramref name="requiresAuth"/> is false.
    /// </summary>
    public void Map(string method, string template, ApiHandler handler, bool requiresAuth = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _routes.RemoveAll(r => r.Method == method && r.Template == template);
        _routes.Add(new Route(method, template, template.Split('/'), handler, requiresAuth, Disabled: false));
    }

    /// <summary>A route that exists in the API but is switched off here (404 feature_disabled).</summary>
    public void MapDisabled(string method, string template)
    {
        _routes.RemoveAll(r => r.Method == method && r.Template == template);
        _routes.Add(new Route(method, template, template.Split('/'), static _ => Task.CompletedTask,
            RequiresAuth: false, Disabled: true));
    }

    public async Task HandleAsync(ApiRequest request, IResponseSink sink)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        var tracking = new TrackingSink(sink);
        var context = new ApiContext(request, tracking, TokenFormat.NewId("req_", 12), _time);
        context.ResponseHeaders[HeaderNames.RequestId] = context.RequestId;

        try
        {
            await RunAsync(context, request).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sink.ClientAborted.IsCancellationRequested)
        {
            // The client went away: nothing to write.
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, tracking, ToApiException(ex)).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ApiContext context, ApiRequest request)
    {
        // Echo a valid client request id; refuse a malformed one.
        if (request.Headers.TryGetValue(HeaderNames.ClientRequestId, out var clientId))
        {
            if (!RequestValidator.IsValidClientRequestId(clientId))
            {
                throw new ApiException(ErrorCodes.InvalidRequest,
                    "X-Portal-Client-Request-Id must be 1-64 characters of [A-Za-z0-9._-].",
                    HeaderNames.ClientRequestId);
            }

            context.ResponseHeaders[HeaderNames.ClientRequestId] = clientId;
        }

        // Browsers always send Origin on cross-site requests; no legitimate client does.
        if (request.Headers.ContainsKey("Origin"))
        {
            throw new ApiException(ErrorCodes.ForbiddenOrigin, "Browser requests are not accepted.");
        }

        if (request.Body.Length > _options.MaxRequestBytes)
        {
            throw new ApiException(ErrorCodes.RequestTooLarge,
                $"The request body is larger than {_options.MaxRequestBytes} bytes.");
        }

        var (route, values) = Match(request.Method, request.Path);
        if (route is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "Unknown route.");
        }

        if (route.Disabled)
        {
            throw new ApiException(ErrorCodes.FeatureDisabled, "This endpoint is not available on this host.");
        }

        context.RouteValues = values;

        if (request.Method is "POST" or "PUT" or "PATCH")
        {
            request.Headers.TryGetValue("Content-Type", out var contentType);
            if (!RequestValidator.IsJsonContentType(contentType))
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "Content-Type must be application/json.",
                    "Content-Type");
            }
        }

        if (route.RequiresAuth)
        {
            context.Principal = await AuthenticateAsync(request, context.Aborted).ConfigureAwait(false);
        }

        context.Options = RequestValidator.ParseOptions(request.Headers, _time.GetUtcNow());
        await route.Handler(context).ConfigureAwait(false);

        if (!context.ResponseStarted)
        {
            throw new InvalidOperationException("The handler wrote no response.");
        }
    }

    private async Task<ClientPrincipal> AuthenticateAsync(ApiRequest request, CancellationToken ct)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var auth) ||
            !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            auth.Length <= "Bearer ".Length)
        {
            throw new ApiException(ErrorCodes.AuthRequired, "Authorization: Bearer <token> is required.");
        }

        var result = await _registry.ValidateAsync(auth, ct).ConfigureAwait(false);
        if (result.Principal is null)
        {
            var code = result.ErrorCode == ErrorCodes.TokenRevoked ? ErrorCodes.TokenRevoked : ErrorCodes.TokenInvalid;
            throw new ApiException(code, code == ErrorCodes.TokenRevoked
                ? "Access to Portal AI Host was removed for this app."
                : "The token is not known to this host.");
        }

        return result.Principal;
    }

    private (Route? Route, IReadOnlyDictionary<string, string> Values) Match(string method, string path)
    {
        var segments = path.Split('/');
        foreach (var route in _routes)
        {
            if (!string.Equals(route.Method, method, StringComparison.Ordinal) ||
                route.Segments.Length != segments.Length)
            {
                continue;
            }

            Dictionary<string, string>? values = null;
            var ok = true;
            for (var i = 0; i < segments.Length && ok; i++)
            {
                var t = route.Segments[i];
                if (t.Length > 2 && t[0] == '{' && t[^1] == '}')
                {
                    ok = RequestValidator.IsRouteValue(segments[i]);
                    if (ok)
                    {
                        (values ??= new(StringComparer.Ordinal))[t[1..^1]] = segments[i];
                    }
                }
                else
                {
                    ok = string.Equals(t, segments[i], StringComparison.Ordinal);
                }
            }

            if (ok)
            {
                return (route, values ?? new Dictionary<string, string>(StringComparer.Ordinal));
            }
        }

        return (null, new Dictionary<string, string>());
    }

    // ---- built-in auth routes --------------------------------------------------------------------

    private async Task CreateAccessRequestAsync(ApiContext context)
    {
        var caller = RequireCaller(context);
        var body = context.ReadJson(AiHostJsonContext.Default.AuthRequestCreate);
        var info = await _registry.CreateRequestAsync(body, caller, context.Aborted).ConfigureAwait(false);
        await context.WriteJsonAsync(202, new AuthRequestCreated
        {
            RequestId = info.RequestId,
            Status = AuthStatuses.Pending,
            ExpiresAt = info.ExpiresAt,
            PollWaitMs = ApiLimits.AuthWaitMsDefault,
        }, AiHostJsonContext.Default.AuthRequestCreated).ConfigureAwait(false);
    }

    private async Task PollAccessRequestAsync(ApiContext context)
    {
        var caller = RequireCaller(context);
        var id = context.RouteValues["id"];
        var waitMs = 0;
        if (context.Request.Query.TryGetValue("wait_ms", out var waitText) &&
            (!int.TryParse(waitText, NumberStyles.None, CultureInfo.InvariantCulture, out waitMs) ||
             waitMs > _options.MaxAuthWaitMs))
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                $"wait_ms must be a whole number from 0 to {_options.MaxAuthWaitMs}.", "wait_ms");
        }

        // Only the process that made the request (same package, or same exe) may collect its token.
        if (_registry is ICallerBoundRequests bound && !bound.IsSameCaller(id, caller))
        {
            throw new ApiException(ErrorCodes.NotFound, "Unknown or expired access request.");
        }

        var result = await _registry.WaitForRequestAsync(id, TimeSpan.FromMilliseconds(waitMs), context.Aborted)
            .ConfigureAwait(false) ?? throw new ApiException(ErrorCodes.NotFound, "Unknown or expired access request.");

        await context.WriteJsonAsync(200, new AuthRequestStatus
        {
            Status = result.Status,
            Token = result.Token,
            TokenId = result.TokenId,
            AppId = result.AppId,
        }, AiHostJsonContext.Default.AuthRequestStatus).ConfigureAwait(false);
    }

    private async Task RevokeOwnTokenAsync(ApiContext context)
    {
        await _registry.RevokeAsync(context.Principal!.TokenId, context.Aborted).ConfigureAwait(false);
        await context.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    /// <summary>Fail closed: without a resolved, same-user caller there is no access request.</summary>
    private static CallerIdentity RequireCaller(ApiContext context)
    {
        var caller = context.Request.Caller;
        if (caller is null || !caller.SameUser)
        {
            throw new ApiException(ErrorCodes.AccessDenied,
                "The calling process could not be identified as running for the current Windows user.");
        }

        return caller;
    }

    // ---- errors ------------------------------------------------------------------------------------

    private ApiException ToApiException(Exception ex) => ex switch
    {
        ApiException api => api,
        EngineCrashedException => new ApiException(ErrorCodes.EngineRestarting,
            "The engine stopped and is restarting. Try again shortly.",
            retryAfterMs: _options.EngineRestartRetryAfterMs),
        EngineException engine when ErrorCatalog.TryGet(engine.ErrorCode, out _) =>
            new ApiException(engine.ErrorCode, PublicMessage(engine.ErrorCode)),
        EngineException => new ApiException(ErrorCodes.EngineError, PublicMessage(ErrorCodes.EngineError)),

        // Never echo exception text: it could carry request content or paths.
        _ => new ApiException(ErrorCodes.InternalError, PublicMessage(ErrorCodes.InternalError)),
    };

    private static string PublicMessage(string code) => code switch
    {
        ErrorCodes.EngineError => "The engine failed to answer.",
        ErrorCodes.InternalError => "Unexpected host error.",
        ErrorCodes.InsufficientMemory => "The model cannot load in the memory available now.",
        ErrorCodes.NoModel => "No model of the needed kind is installed.",
        ErrorCodes.ModelLoading => "The model is loading. Try again shortly.",
        ErrorCodes.ContextLengthExceeded => "The input is longer than the model's context.",
        ErrorCodes.DeadlineExceeded => "The request deadline passed.",
        _ => "The request failed.",
    };

    private static async Task WriteErrorAsync(ApiContext context, TrackingSink sink, ApiException error)
    {
        var envelope = ErrorCatalog.Create(error.Code, error.Message, error.Param, error.RetryAfterMs, error.Details);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, AiHostJsonContext.Default.ApiError);
        var ct = sink.ClientAborted;
        try
        {
            if (sink.Started)
            {
                if (sink.Streaming && !sink.Completed)
                {
                    // In-stream error: one data event, then close without [DONE].
                    await sink.WriteSseDataAsync(bytes, ct).ConfigureAwait(false);
                }

                await sink.CompleteAsync(ct).ConfigureAwait(false);
                return;
            }

            var headers = new Dictionary<string, string>(context.ResponseHeaders, StringComparer.OrdinalIgnoreCase);
            if (error.RetryAfterMs is { } ms && ErrorCatalog.Get(error.Code).Retryable)
            {
                headers[HeaderNames.RetryAfter] =
                    ((ms + 999) / 1000).ToString(CultureInfo.InvariantCulture);
            }

            await sink.StartAsync(ErrorCatalog.Get(error.Code).HttpStatus, "application/json; charset=utf-8",
                headers, ct).ConfigureAwait(false);
            await sink.WriteAsync(bytes, ct).ConfigureAwait(false);
            await sink.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private sealed record Route(
        string Method, string Template, string[] Segments, ApiHandler Handler, bool RequiresAuth, bool Disabled);
}
