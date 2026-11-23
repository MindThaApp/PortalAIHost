using Sentry;

namespace Portal.AiHost.CrashReports;

/// <summary>
/// Crash reports for the window and the host. Off when the build has no address; otherwise on until the user turns
/// it off. Turning it off takes effect at once (nothing more is sent) and the user is never asked again.
/// </summary>
public static class CrashReporting
{
    private static readonly Lock Gate = new();
    private static volatile bool _enabled;
    private static IDisposable? _sdk;
    private static string _role = "app";
    private static string _version = "0.0.0";

    /// <summary>True when this build carries a crash-report address.</summary>
    public static bool IsAvailable => CrashBuildInfo.Dsn.Length > 0;

    /// <summary>True while reports are being sent.</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Starts (or leaves off) crash reports for this process. <paramref name="role"/> is "app" or "host".</summary>
    public static void Start(string role, string version, bool enabled)
    {
        lock (Gate)
        {
            _role = role;
            _version = version;
        }

        SetEnabled(enabled);
    }

    /// <summary>Follows the user's choice at once: closes the connection when off, opens it when on.</summary>
    public static void SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            _enabled = enabled && IsAvailable;
            if (_enabled && _sdk is null)
            {
                _sdk = SentrySdk.Init(Configure);
            }
            else if (!_enabled && _sdk is not null)
            {
                var sdk = _sdk;
                _sdk = null;
                try
                {
                    sdk.Dispose();
                }
                catch (Exception)
                {
                    // Closing must never throw into the app.
                }
            }
        }
    }

    /// <summary>Sends one handled error (for example a failure the app recovers from). No-op when off.</summary>
    public static void Report(Exception ex)
    {
        if (_enabled)
        {
            SentrySdk.CaptureException(ex);
        }
    }

    internal static void Configure(SentryOptions o)
    {
        o.Dsn = CrashBuildInfo.Dsn;
        o.Release = _version;
        o.Environment = CrashBuildInfo.Environment;
        o.SendDefaultPii = false;
        o.ServerName = null;
        o.AutoSessionTracking = false;
        o.MaxBreadcrumbs = 0;
        o.TracesSampleRate = 0;
        o.AttachStacktrace = true;
        o.IsGlobalModeEnabled = true;
        o.StackTraceMode = StackTraceMode.Original;
        o.SetBeforeSend((e, _) => _enabled ? CrashScrubber.Scrub(Tag(e)) : null);
        o.SetBeforeBreadcrumb((_, _) => null);
    }

    private static SentryEvent Tag(SentryEvent e)
    {
        e.SetTag("role", _role);
        return e;
    }
}
