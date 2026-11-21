using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Auth;

/// <summary>
/// Finds which process owns a loopback connection and who it is (package family name or exe path).
/// Windows: GetExtendedTcpTable + process queries. Tests: a fake.
/// </summary>
public interface ICallerIdentityResolver
{
    /// <summary>
    /// <paramref name="remote"/> is the client's end of the connection, <paramref name="local"/> the host's.
    /// Returns null when no owner can be found (the request is then refused).
    /// </summary>
    CallerIdentity? Resolve(IPEndPoint remote, IPEndPoint local);
}

/// <summary>
/// Binds an access request to the process that made it: only the same package (or, unpackaged, the same exe)
/// may poll it and collect the token.
/// </summary>
public interface ICallerBoundRequests
{
    /// <summary>True when <paramref name="poller"/> may poll the request (also true for unknown ids).</summary>
    bool IsSameCaller(string requestId, CallerIdentity poller);
}

/// <summary>Audit trail of auth decisions. By construction it carries ids only, never token text.</summary>
public interface IAuthAuditLog
{
    void Record(AuthAuditEvent auditEvent);
}

public enum AuthAuditKind
{
    RequestCreated,
    RequestRefused,
    Approved,
    Denied,
    Expired,
    TokenIssued,
    TokenRevoked,
    TokenRejected,
    ReAllowed,
}

/// <summary>One audit entry. Detail is a fixed reason word (for example "rate_limited"), never request content.</summary>
public sealed record AuthAuditEvent(
    AuthAuditKind Kind, DateTimeOffset At, string? AppId, string? RequestId, string? TokenId, string? Detail);

/// <summary>What the approval window shows: "&lt;App&gt; on &lt;device&gt;" plus the verified caller line.</summary>
public sealed record ApprovalPrompt(
    string RequestId,
    string Title,
    string AppName,
    string DeviceName,
    string CallerLine,
    bool CallerVerified,
    string? Reason,
    DateTimeOffset ExpiresAt);

public static class ApprovalPrompts
{
    /// <summary>Device name shown for requests from this PC.</summary>
    public const string LocalDeviceName = "this PC";

    public static ApprovalPrompt For(AccessRequestInfo info, string? deviceName = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var device = string.IsNullOrWhiteSpace(deviceName) ? LocalDeviceName : TextRules.Sanitize(deviceName);
        var app = TextRules.Sanitize(info.AppName);
        return new ApprovalPrompt(
            info.RequestId,
            $"{app} on {device}",
            app,
            device,
            CallerLine(info.Caller),
            info.Caller.Verified,
            info.Reason is null ? null : TextRules.Sanitize(info.Reason),
            info.ExpiresAt);
    }

    /// <summary>"verified: Publisher.App" or "unverified: C:\path\app.exe".</summary>
    public static string CallerLine(CallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.Verified && !string.IsNullOrEmpty(caller.PackageFamilyName))
        {
            return "verified: " + caller.PackageFamilyName;
        }

        return "unverified: " + (caller.ExePath ?? $"process {caller.ProcessId}");
    }

    /// <summary>Control-pipe form for the approval window (WP6).</summary>
    public static PendingApproval ToControl(AccessRequestInfo info)
    {
        var prompt = For(info);
        return new PendingApproval
        {
            RequestId = info.RequestId,
            AppId = info.AppId,
            AppName = prompt.AppName,
            AppVersion = info.AppVersion,
            Reason = prompt.Reason,
            CallerIdentity = prompt.CallerLine,
            CallerVerified = prompt.CallerVerified,
            ExpiresAt = info.ExpiresAt,
        };
    }
}

/// <summary>Validation of text that reaches the approval prompt.</summary>
internal static class TextRules
{
    /// <summary>
    /// True when the text has no control or format characters (Cc, Cf: includes bidi overrides and
    /// zero-width characters that could disguise an app name in the prompt).
    /// </summary>
    public static bool IsDisplaySafe(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                return false;
            }
        }

        return true;
    }

    public static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            sb.Append(IsDisplaySafe(rune.ToString()) ? rune.ToString() : "\uFFFD");
        }

        return sb.ToString().Trim();
    }
}

// ---- persisted registry (clients.json) --------------------------------------

/// <summary>One issued token. TokenHash is the keyed HMAC (base64); the token itself is never stored.</summary>
internal sealed class StoredClient
{
    public string TokenId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? PackageFamilyName { get; set; }
    public string? ExePath { get; set; }
    public bool CallerVerified { get; set; }
    public string DeviceId { get; set; } = ClientPrincipal.LocalDevice;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A denied app (per device), refused until DeniedUntil.</summary>
internal sealed class StoredDenial
{
    public string AppId { get; set; } = "";
    public string DeviceId { get; set; } = ClientPrincipal.LocalDevice;
    public DateTimeOffset DeniedUntil { get; set; }
}

internal sealed class ClientRegistryData
{
    public List<StoredClient> Clients { get; set; } = new();
    public List<StoredDenial> Denials { get; set; } = new();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClientRegistryData))]
internal sealed partial class AuthJsonContext : JsonSerializerContext
{
}
