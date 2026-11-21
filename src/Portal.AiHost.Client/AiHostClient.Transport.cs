using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

public sealed partial class AiHostClient
{
    private static readonly TimeSpan ClientTimeoutGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends one request with the retry policy. Returns a successful response (the caller disposes it), or throws
    /// <see cref="AiHostException"/> after updating the client state.
    /// </summary>
    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, byte[]? body,
        RequestOptions? ro, bool auth, bool stream, CancellationToken ct)
    {
        var priority = ro?.Priority ?? _options.DefaultPriority;
        var budget = ro?.Deadline ?? TimeSpan.FromMilliseconds(priority == AiPriority.Batch
            ? ApiLimits.DefaultBatchDeadlineMs
            : ApiLimits.DefaultInteractiveDeadlineMs);
        var clock = Stopwatch.StartNew();
        var rediscovered = false;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var endpoint = await EnsureEndpointAsync(ct).ConfigureAwait(false)
                ?? throw new AiHostUnavailableException("Portal AI Host is not available.");

            string? token = null;
            if (auth)
            {
                token = await GetTokenAsync(ct).ConfigureAwait(false);
                if (token is null)
                {
                    SetState(State is AiHostState.Denied or AiHostState.Revoked ? State : AiHostState.NeedsAccess);
                    throw new AiHostUnavailableException("This app has no Portal AI Host access yet.", ClientErrorCodes.NoAccess);
                }
            }

            using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseAddress, path));
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            AddControlHeaders(request, ro, priority, budget - clock.Elapsed);
            if (body is not null)
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content = content;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!stream)
            {
                cts.CancelAfter(budget + ClientTimeoutGrace);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request,
                    stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                InvalidateEndpoint();
                if (!rediscovered)
                {
                    rediscovered = true;
                    attempt--;
                    continue;
                }

                SetState(_discovery.LooksInstalled() ? AiHostState.NotRunning : AiHostState.NotInstalled);
                throw new AiHostUnavailableException("Portal AI Host did not answer.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new AiHostException(ErrorCodes.DeadlineExceeded, "The request took longer than its deadline.", 0);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            if (IsRetryable(error.Code) && attempt < MaxAttemptsFor(error.Code))
            {
                var delay = ComputeDelay(attempt, error.RetryAfter);
                if (clock.Elapsed + delay < budget)
                {
                    Log($"retrying after {error.Code}");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }
            }

            await ApplyFailureAsync(error, ct).ConfigureAwait(false);
            throw error;
        }
    }

    private void AddControlHeaders(HttpRequestMessage request, RequestOptions? ro, AiPriority priority, TimeSpan remaining)
    {
        request.Headers.TryAddWithoutValidation(HeaderNames.Priority,
            priority == AiPriority.Batch ? PriorityValues.Batch : PriorityValues.Interactive);
        if (ro?.Deadline is not null)
        {
            var ms = (long)Math.Clamp(remaining.TotalMilliseconds, ApiLimits.MinDeadlineMs, ApiLimits.MaxDeadlineMs);
            request.Headers.TryAddWithoutValidation(HeaderNames.DeadlineMs, ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (ro?.LoadBehavior is { } load)
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.Load,
                load == AiLoadBehavior.Fail ? LoadBehaviorValues.Fail : LoadBehaviorValues.Wait);
        }

        if (!string.IsNullOrEmpty(ro?.ClientRequestId))
        {
            request.Headers.TryAddWithoutValidation(HeaderNames.ClientRequestId, ro.ClientRequestId);
        }
    }

    private static bool IsRetryable(string code) =>
        code is ErrorCodes.QueueFull or ErrorCodes.ModelLoading or ErrorCodes.EngineRestarting
            or ErrorCodes.EngineError or ErrorCodes.InternalError;

    private int MaxAttemptsFor(string code) =>
        code is ErrorCodes.EngineError or ErrorCodes.InternalError
            ? Math.Min(2, _options.Retry.MaxAttempts)
            : _options.Retry.MaxAttempts;

    private TimeSpan ComputeDelay(int attempt, TimeSpan? serverAdvice)
    {
        if (serverAdvice is { } advice)
        {
            return advice;
        }

        var retry = _options.Retry;
        var step = Math.Min(retry.MaxDelay.TotalMilliseconds, retry.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jittered = step * (0.5 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromMilliseconds(Math.Max(jittered, Math.Min(retry.InitialDelay.TotalMilliseconds, step)));
    }

    private static async Task<AiHostException> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            var status = (int)response.StatusCode;
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                text = "";
            }

            ApiErrorBody? body = null;
            try
            {
                body = AiHostJson.Deserialize(text, AiHostJsonContext.Default.ApiError)?.Error;
            }
            catch (JsonException)
            {
                // not an API error body
            }

            TimeSpan? retryAfter = null;
            if (body?.RetryAfterMs is { } ms)
            {
                retryAfter = TimeSpan.FromMilliseconds(ms);
            }
            else if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                retryAfter = delta;
            }

            var code = string.IsNullOrEmpty(body?.Code) ? "http_" + status : body.Code;
            return new AiHostException(code, body?.Message ?? $"Portal AI Host answered HTTP {status}.", status, retryAfter);
        }
    }

    private async Task ApplyFailureAsync(AiHostException error, CancellationToken ct)
    {
        switch (error.Code)
        {
            case ErrorCodes.TokenRevoked or ErrorCodes.TokenInvalid:
                await _store.DeleteAsync(ct).ConfigureAwait(false); // only this app's token file
                _token = null;
                _tokenLoaded = true;
                SetState(AiHostState.Revoked);
                break;
            case ErrorCodes.AuthRequired:
                SetState(AiHostState.NeedsAccess);
                break;
            case ErrorCodes.AccessDenied:
                SetState(AiHostState.Denied);
                break;
            case ErrorCodes.NoModel:
                SetState(AiHostState.NoModel);
                break;
            case ErrorCodes.HostStopping:
                InvalidateEndpoint();
                SetState(AiHostState.NotRunning);
                break;
        }
    }

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            var value = AiHostJson.Deserialize(text, info);
            if (value is not null)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // fall through to the error below
        }

        throw new AiHostException(ErrorCodes.InternalError, "Portal AI Host sent a reply this client could not read.",
            (int)response.StatusCode);
    }

    /// <summary>Whether a failed model call should be answered by the fallback model instead of throwing.</summary>
    private bool CanFallback(AiHostException ex, RequestOptions? ro) =>
        _options.Fallback is not null && ro?.AllowFallback != false && IsFallbackCode(ex);

    private static bool IsFallbackCode(AiHostException ex) =>
        ex.Code is ClientErrorCodes.HostUnavailable or ClientErrorCodes.NoAccess or ClientErrorCodes.StreamInterrupted
            or ErrorCodes.NoModel or ErrorCodes.InsufficientMemory or ErrorCodes.HostStopping
            or ErrorCodes.TokenRevoked or ErrorCodes.TokenInvalid or ErrorCodes.AuthRequired or ErrorCodes.AccessDenied
            or ErrorCodes.DeadlineExceeded or ErrorCodes.QueueFull or ErrorCodes.ModelLoading
            or ErrorCodes.EngineRestarting or ErrorCodes.EngineError or ErrorCodes.InternalError or ErrorCodes.RateLimited
        || (ex.HttpStatus >= 500 && !ErrorCatalog.TryGet(ex.Code, out _));

    private string RaiseFallback(AiHostException ex)
    {
        var message = ex.Code switch
        {
            ClientErrorCodes.HostUnavailable or ErrorCodes.HostStopping => "Home host offline — using this PC's model",
            ClientErrorCodes.NoAccess or ErrorCodes.AuthRequired => "Portal AI Host access is not set up — using this PC's model",
            ErrorCodes.TokenRevoked or ErrorCodes.TokenInvalid => "Access to Portal AI Host was removed — using this PC's model",
            ErrorCodes.AccessDenied => "Portal AI Host access was denied — using this PC's model",
            ErrorCodes.NoModel or ErrorCodes.InsufficientMemory => "Home host has no usable model — using this PC's model",
            _ => "Home host did not answer in time — using this PC's model",
        };
        FallbackActivated?.Invoke(this, new FallbackActivatedEventArgs(ex.Code, message));
        return message;
    }
}
