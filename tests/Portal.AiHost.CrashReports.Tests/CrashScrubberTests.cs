using Portal.AiHost.CrashReports;
using Sentry;
using Sentry.Protocol;

namespace Portal.AiHost.CrashReports.Tests;

public class CrashScrubberTests
{
    private static SentryEvent Event(string message)
    {
        var e = new SentryEvent
        {
            ServerName = Environment.MachineName,
            Message = new SentryMessage { Message = "user typed: secret prompt" },
            User = new SentryUser { Username = Environment.UserName, IpAddress = "203.0.113.9", Email = "a@b.com", Id = "1" },
            Request = new SentryRequest { Url = "http://127.0.0.1:5000/v1/chat", Data = "{\"messages\":[\"hello\"]}", QueryString = "q=1" },
        };
        e.SentryExceptions = [new SentryException
        {
            Type = "IOException",
            Value = message,
            Stacktrace = new SentryStackTrace
            {
                Frames = [new SentryStackFrame
                {
                    Function = "Run",
                    AbsolutePath = @"C:\Users\someone\src\X.cs",
                    FileName = @"C:\Users\someone\src\X.cs",
                    ContextLine = "var prompt = \"hi\";",
                }],
            },
        }];
        e.Contexts.Device.Name = Environment.MachineName;
        return e;
    }

    [Fact]
    public void Removes_user_machine_ip_request_and_message()
    {
        var e = CrashScrubber.Scrub(Event("boom"))!;
        Assert.Null(e.ServerName);
        Assert.Null(e.Message);
        Assert.Null(e.User.Username);
        Assert.Null(e.User.IpAddress);
        Assert.Null(e.User.Email);
        Assert.Null(e.User.Id);
        Assert.Null(e.Request.Url);
        Assert.Null(e.Request.Data);
        Assert.Null(e.Request.QueryString);
        Assert.Null(e.Contexts.Device.Name);
    }

    [Fact]
    public void Removes_file_names_and_source_lines_from_frames()
    {
        var e = CrashScrubber.Scrub(Event("boom"))!;
        var f = e.SentryExceptions!.Single().Stacktrace!.Frames.Single();
        Assert.Null(f.AbsolutePath);
        Assert.Null(f.FileName);
        Assert.Null(f.ContextLine);
        Assert.Equal("Run", f.Function);
    }

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\Jane Doe\Documents\notes.txt'.")]
    [InlineData(@"Access denied D:\Models\llama\model.gguf now")]
    [InlineData("Bad path "+@"\\server\share\folder\a.gguf")]
    [InlineData("Missing /home/jane/models/a.gguf")]
    public void Replaces_drive_unc_and_rooted_paths(string text)
    {
        var s = CrashScrubber.ScrubText(text)!;
        Assert.Contains(CrashScrubber.PathMark, s);
        Assert.DoesNotContain("Jane", s);
        Assert.DoesNotContain("Models", s);
        Assert.DoesNotContain("share", s);
        Assert.DoesNotContain("jane", s);
        Assert.DoesNotContain("gguf", s);
    }

    [Fact]
    public void Removes_user_and_machine_names_wherever_they_appear()
    {
        var s = CrashScrubber.ScrubText($"hello {Environment.UserName.ToUpperInvariant()} on {Environment.MachineName}")!;
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName, s, StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length >= 3) Assert.DoesNotContain(Environment.MachineName, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Removes_addresses_emails_urls_and_long_quoted_text()
    {
        var s = CrashScrubber.ScrubText("from 192.168.4.4 mail me@example.com see https://x.test/a?b=1 said \"tell me a very long story about my dog Rex\" ok")!;
        Assert.DoesNotContain("192.168", s);
        Assert.DoesNotContain("example.com", s);
        Assert.DoesNotContain("x.test", s);
        Assert.DoesNotContain("dog Rex", s);
        Assert.Contains(CrashScrubber.TextMark, s);
    }

    [Fact]
    public void Keeps_short_plain_messages_and_caps_length()
    {
        Assert.Equal("The pipe was closed", CrashScrubber.ScrubText("The pipe was closed"));
        var long_ = new string('x', 500);
        Assert.True(CrashScrubber.ScrubText(long_)!.Length <= CrashScrubber.MaxMessageLength + 3);
    }

    [Fact]
    public void Scrubs_exception_message_in_the_event()
    {
        var e = CrashScrubber.Scrub(Event(@"No access to C:\Users\Jane\a.txt"))!;
        var v = e.SentryExceptions!.Single().Value!;
        Assert.DoesNotContain("Jane", v);
        Assert.Equal("IOException", e.SentryExceptions!.Single().Type);
    }

    [Fact]
    public void Null_event_stays_null()
    {
        Assert.Null(CrashScrubber.Scrub(null));
    }

    [Fact]
    public void Without_a_build_address_nothing_is_sent()
    {
        // A test build has no key file here unless the machine has one; either way turning it off stops it.
        CrashReporting.SetEnabled(false);
        Assert.False(CrashReporting.IsEnabled);
        CrashReporting.Report(new InvalidOperationException("x"));
    }
}
