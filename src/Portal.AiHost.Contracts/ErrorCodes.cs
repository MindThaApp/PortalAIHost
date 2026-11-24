using System.Text.Json;

namespace Portal.AiHost.Contracts;

/// <summary>Stable error codes (api-v1.md section 5). Clients switch on these, never on message text.</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedParameter = "unsupported_parameter";
    public const string ContextLengthExceeded = "context_length_exceeded";
    public const string AuthRequired = "auth_required";
    public const string TokenInvalid = "token_invalid";
    public const string TokenRevoked = "token_revoked";
    public const string AccessDenied = "access_denied";
    public const string AppPaused = "app_paused";
    public const string ForbiddenOrigin = "forbidden_origin";
    public const string NotFound = "not_found";
    public const string FeatureDisabled = "feature_disabled";
    public const string RequestTooLarge = "request_too_large";
    public const string QueueFull = "queue_full";
    public const string RateLimited = "rate_limited";
    public const string NoModel = "no_model";
    public const string ModelLoading = "model_loading";
    public const string EngineRestarting = "engine_restarting";
    public const string InsufficientMemory = "insufficient_memory";
    public const string HostStopping = "host_stopping";
    public const string AuthUnavailable = "auth_unavailable";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string EngineError = "engine_error";
    public const string InternalError = "internal_error";
}

/// <summary>Values of the error type field.</summary>
public static class ErrorTypes
{
    public const string InvalidRequest = "invalid_request_error";
    public const string Authentication = "authentication_error";
    public const string Permission = "permission_error";
    public const string NotFound = "not_found_error";
    public const string RateLimit = "rate_limit_error";
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Server = "server_error";
}

/// <summary>HTTP status, error type and retry advice for one error code.</summary>
public readonly record struct ErrorInfo(int HttpStatus, string Type, bool Retryable);

/// <summary>Lookup of every v1 error code.</summary>
public static class ErrorCatalog
{
    private static readonly Dictionary<string, ErrorInfo> Map = new(StringComparer.Ordinal)
    {
        [ErrorCodes.InvalidRequest] = new(400, ErrorTypes.InvalidRequest, false),
        [ErrorCodes.UnsupportedParameter] = new(400, ErrorTypes.InvalidRequest, false),
        [ErrorCodes.ContextLengthExceeded] = new(400, ErrorTypes.InvalidRequest, false),
        [ErrorCodes.AuthRequired] = new(401, ErrorTypes.Authentication, false),
        [ErrorCodes.TokenInvalid] = new(401, ErrorTypes.Authentication, false),
        [ErrorCodes.TokenRevoked] = new(401, ErrorTypes.Authentication, false),
        [ErrorCodes.AccessDenied] = new(403, ErrorTypes.Permission, false),
        [ErrorCodes.AppPaused] = new(403, ErrorTypes.Permission, false),
        [ErrorCodes.ForbiddenOrigin] = new(403, ErrorTypes.Permission, false),
        [ErrorCodes.NotFound] = new(404, ErrorTypes.NotFound, false),
        [ErrorCodes.FeatureDisabled] = new(404, ErrorTypes.NotFound, false),
        [ErrorCodes.RequestTooLarge] = new(413, ErrorTypes.InvalidRequest, false),
        [ErrorCodes.QueueFull] = new(429, ErrorTypes.RateLimit, true),
        [ErrorCodes.RateLimited] = new(429, ErrorTypes.RateLimit, true),
        [ErrorCodes.NoModel] = new(503, ErrorTypes.Unavailable, false),
        [ErrorCodes.ModelLoading] = new(503, ErrorTypes.Unavailable, true),
        [ErrorCodes.EngineRestarting] = new(503, ErrorTypes.Unavailable, true),
        [ErrorCodes.InsufficientMemory] = new(503, ErrorTypes.Unavailable, false),
        [ErrorCodes.HostStopping] = new(503, ErrorTypes.Unavailable, false),
        [ErrorCodes.AuthUnavailable] = new(503, ErrorTypes.Unavailable, true),
        [ErrorCodes.DeadlineExceeded] = new(504, ErrorTypes.Timeout, false),
        [ErrorCodes.EngineError] = new(500, ErrorTypes.Server, true),
        [ErrorCodes.InternalError] = new(500, ErrorTypes.Server, true),
    };

    public static IReadOnlyCollection<string> AllCodes => Map.Keys;

    public static bool TryGet(string code, out ErrorInfo info) => Map.TryGetValue(code, out info);

    public static ErrorInfo Get(string code) =>
        Map.TryGetValue(code, out var info) ? info : new ErrorInfo(500, ErrorTypes.Server, false);

    /// <summary>Builds a complete envelope for a code, using the catalogued type.</summary>
    public static ApiError Create(string code, string message, string? param = null,
        int? retryAfterMs = null, Dictionary<string, JsonElement>? details = null) =>
        new()
        {
            Error = new ApiErrorBody
            {
                Message = message,
                Type = Get(code).Type,
                Code = code,
                Param = param,
                RetryAfterMs = retryAfterMs,
                Details = details,
            },
        };
}
