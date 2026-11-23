using System.Text.RegularExpressions;
using Sentry;

namespace Portal.AiHost.CrashReports;

/// <summary>
/// Removes everything personal from a crash report before it leaves the PC: file names and paths, the Windows user
/// and machine names, addresses, request data, breadcrumbs and anything that looks like typed or generated text.
/// What stays is the exception type, a cleaned short message, the stack trace and the app version.
/// </summary>
public static partial class CrashScrubber
{
    public const string PathMark = "[path]";
    public const string RemovedText = "[removed]";
    public const string TextMark = "[text]";
    public const int MaxMessageLength = 200;

    /// <summary>Quoted text longer than this is treated as user text and removed.</summary>
    private const int MaxQuoted = 24;

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\r\n""'<>|*?]*", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"\\\\[^\s\\/]+[\\/][^\r\n""'<>|*?]*", RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    [GeneratedRegex(@"(?<![\w.:])/(?:[\w.\-]+/)+[\w.\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex RootedPath();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex("\"([^\"\r\n]{25,})\"|'([^'\r\n]{25,})'", RegexOptions.CultureInvariant)]
    private static partial Regex LongQuoted();

    /// <summary>Cleans one piece of text. Never returns more than <see cref="MaxMessageLength"/> characters.</summary>
    public static string? ScrubText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var s = text;
        s = Url().Replace(s, "[url]");
        s = UncPath().Replace(s, PathMark);
        s = DrivePath().Replace(s, PathMark);
        s = RootedPath().Replace(s, PathMark);
        s = Email().Replace(s, "[email]");
        s = Ipv4().Replace(s, "[ip]");
        s = LongQuoted().Replace(s, TextMark);
        s = RemoveName(s, Environment.UserName);
        s = RemoveName(s, Environment.MachineName);
        return s.Length > MaxMessageLength ? s[..MaxMessageLength] + "..." : s;
    }

    private static string RemoveName(string s, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
        {
            return s;
        }

        return s.Replace(name, "[name]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Sentry callback: cleans the event in place and returns it.</summary>
    public static SentryEvent? Scrub(SentryEvent? e)
    {
        if (e is null)
        {
            return null;
        }

        e.User = new SentryUser();
        e.ServerName = null;
        e.Request = new SentryRequest();
        // Breadcrumbs and extras are never recorded (see CrashReporting options), so there is nothing to clear.
        e.Message = null; // free text logged by the code; the exception below carries the cause
        e.TransactionName = null;
        e.Contexts.Device.Name = null;
        e.Contexts.Device.Timezone = null;
        e.Contexts.Device.BootTime = null;
        e.Contexts.Remove("culture");
        e.Contexts.Remove("response");
        e.Contexts.Remove("app_name");
        if (e.Contexts.App is { } app)
        {
            app.Identifier = null;
            app.StartTime = null;
        }

        foreach (var ex in e.SentryExceptions ?? [])
        {
            // Error text can echo a request (prompt, JSON, engine output), so it is never sent. The type, module,
            // HResult and our own stack frames stay. Inner exceptions are separate entries in this list.
            ex.Value = RemovedText;
            if (ex.Stacktrace is { } st)
            {
                ScrubFrames(st.Frames);
            }
        }

        foreach (var thread in e.SentryThreads ?? [])
        {
            if (thread.Stacktrace is { } st)
            {
                ScrubFrames(st.Frames);
            }
        }

        return e;
    }

    private static void ScrubFrames(IList<SentryStackFrame> frames)
    {
        foreach (var f in frames)
        {
            f.AbsolutePath = null;
            f.FileName = null;
            f.ContextLine = null;
            f.PreContext.Clear();
            f.PostContext.Clear();
            f.Vars.Clear();
        }
    }
}
