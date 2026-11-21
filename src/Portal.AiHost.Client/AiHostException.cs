namespace Portal.AiHost.Client;

/// <summary>An error returned by Portal AI Host, or a client-side condition that prevents a call.</summary>
public class AiHostException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">A v1 error code, or one of the <see cref="ClientErrorCodes"/> values.</param>
    /// <param name="message">Human-readable text; do not switch on it.</param>
    /// <param name="httpStatus">The HTTP status, or 0 when there was no response.</param>
    /// <param name="retryAfter">Server advice on when to retry, if any.</param>
    public AiHostException(string code, string message, int httpStatus = 0, TimeSpan? retryAfter = null)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        RetryAfter = retryAfter;
    }

    /// <summary>The error code. Switch on this.</summary>
    public string Code { get; }

    /// <summary>The HTTP status, or 0 when there was no response.</summary>
    public int HttpStatus { get; }

    /// <summary>The server's retry advice, if any.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Thrown when the host cannot be reached and no fallback model is configured.</summary>
public sealed class AiHostUnavailableException : AiHostException
{
    /// <summary>Creates the exception.</summary>
    public AiHostUnavailableException(string message, string code = ClientErrorCodes.HostUnavailable)
        : base(code, message)
    {
    }
}

/// <summary>Error codes raised by the client itself, beside the API's own codes.</summary>
public static class ClientErrorCodes
{
    /// <summary>The host is not installed, not running, or did not answer.</summary>
    public const string HostUnavailable = "host_unavailable";

    /// <summary>No access token is stored; the user has not granted access yet.</summary>
    public const string NoAccess = "no_access";

    /// <summary>The response stream broke after it had started.</summary>
    public const string StreamInterrupted = "stream_interrupted";
}
