using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;

namespace Portal.AiHost.Core.Tests.Support;

/// <summary>Deterministic clock: time moves only on Advance, which fires due timers in order.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } d && d <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
                next.DueAt = next.Period > TimeSpan.Zero ? _now + next.Period : null;
                if (next.DueAt is null)
                {
                    _timers.Remove(next);
                }
            }

            next.Fire();
        }
    }

    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    return true;
                }

                DueAt = owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                owner._timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Stand-in for DPAPI: a reversible, tamper-evident wrapping that is clearly not the plain key.</summary>
public sealed class FakeKeyProtector : IHostKeyProtector
{
    private static readonly byte[] Magic = "FAKEDPAPI1"u8.ToArray();

    public int ProtectCalls { get; private set; }

    public bool FailUnprotect { get; set; }

    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        return [.. Magic, .. plaintext.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (FailUnprotect || protectedData.Length < Magic.Length || !protectedData.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Fake blob could not be opened.");
        }

        return protectedData.Skip(Magic.Length).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}

/// <summary>A unique temp folder (with the test-root marker) removed at the end; only this folder is deleted.</summary>
public sealed class TempDir : IDisposable
{
    public const string Marker = ".portal-ai-host-test-root";

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pah-wp4-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        System.IO.File.WriteAllText(System.IO.Path.Combine(Path, Marker), "");
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        // Recursive delete only of the folder this class created, identified by its marker file.
        if (System.IO.File.Exists(System.IO.Path.Combine(Path, Marker)))
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public sealed class RecordingAudit : IAuthAuditLog
{
    public List<AuthAuditEvent> Events { get; } = new();

    public void Record(AuthAuditEvent auditEvent)
    {
        lock (Events)
        {
            Events.Add(auditEvent);
        }
    }
}

/// <summary>Captures what the pipeline writes.</summary>
public sealed class RecordingSink : IResponseSink
{
    private readonly CancellationTokenSource _abort = new();
    private readonly MemoryStream _body = new();

    public int? Status { get; private set; }
    public string? ContentType { get; private set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SseData { get; } = new();
    public List<string> SseComments { get; } = new();
    public bool Completed { get; private set; }

    public CancellationToken ClientAborted => _abort.Token;

    public string BodyText => Encoding.UTF8.GetString(_body.ToArray());

    public void Abort() => _abort.Cancel();

    public ValueTask StartAsync(int statusCode, string contentType, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        Status = statusCode;
        ContentType = contentType;
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                Headers[k] = v;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _body.Write(data.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteSseDataAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        SseData.Add(Encoding.UTF8.GetString(json.Span));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteSseCommentAsync(string comment, CancellationToken ct)
    {
        SseComments.Add(comment);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(CancellationToken ct)
    {
        Completed = true;
        return ValueTask.CompletedTask;
    }
}

public static class Callers
{
    public static CallerIdentity Packaged(string pfn = "MindThaApp.VoicePortal_8wekyb3d8bbwe", int pid = 4242) =>
        new(pid, pfn, @"C:\Program Files\WindowsApps\VoicePortal\VoicePortal.exe", Verified: true, SameUser: true);

    public static CallerIdentity Unpackaged(string exe = @"C:\Tools\tool.exe", int pid = 5151) =>
        new(pid, null, exe, Verified: false, SameUser: true);

    public static CallerIdentity OtherUser() =>
        new(6161, null, @"C:\Users\Other\x.exe", Verified: false, SameUser: false);
}

/// <summary>Fake resolver for code that needs ICallerIdentityResolver.</summary>
public sealed class FakeCallerIdentityResolver(CallerIdentity? identity) : ICallerIdentityResolver
{
    public CallerIdentity? Resolve(System.Net.IPEndPoint remote, System.Net.IPEndPoint local) => identity;
}
