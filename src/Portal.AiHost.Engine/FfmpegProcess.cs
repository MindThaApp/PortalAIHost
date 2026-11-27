using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

/// <summary>Reads the progress lines ffmpeg prints on stderr (<c>frame=… time=00:00:01.23 …</c>).</summary>
public static partial class FfmpegProgress
{
    [GeneratedRegex(@"\btime=(?:(\d+):)?(\d{1,2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    /// <summary>The media time a progress line has reached, in seconds. False for any other line (or a "N/A" time).</summary>
    public static bool TryParseSeconds(string line, out double seconds)
    {
        seconds = 0;
        var m = TimePattern().Match(line);
        if (!m.Success)
        {
            return false;
        }

        var hours = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var secs = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    /// <summary>Progress 0 to 1 of a run that is <paramref name="total"/> seconds long (never 1 until the process ends).</summary>
    public static double Fraction(double seconds, double total) =>
        total > 0 ? Math.Clamp(seconds / total, 0, 0.99) : 0;
}

/// <summary>
/// Runs one ffmpeg or ffprobe child process: in its own Job Object (kill on close), cancellable, time-limited, with
/// stdin, stdout and stderr all handled in memory. It starts nothing but that one process and ends only that process
/// (and what it started); it writes no file itself.
/// </summary>
internal static class FfmpegProcess
{
    /// <summary>Starts <paramref name="exe"/>, feeds <paramref name="stdin"/>, copies stdout to <paramref name="stdout"/>.</summary>
    /// <returns>The exit code.</returns>
    /// <exception cref="OperationCanceledException">After the process was killed because <paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="EngineException">The process did not start, or ran past <paramref name="timeout"/> (it was killed).</exception>
    public static async Task<int> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, Stream? stdout,
        Action<string>? onStderrLine, TimeSpan timeout, ProcessPriorityClass? priority, TimeSpan killWait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var job = new JobObject();
        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
            {
                throw new EngineException(ErrorCodes.EngineError, "ffmpeg did not start.");
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new EngineException(ErrorCodes.EngineError, "ffmpeg did not start: " + e.Message, e);
        }

        try
        {
            job.Assign(proc);
        }
        catch (Exception e) when (e is Win32Exception or PlatformNotSupportedException)
        {
            Kill(proc);
            throw new EngineException(ErrorCodes.EngineError, "Could not put ffmpeg in a job object: " + e.Message, e);
        }

        if (priority is { } p)
        {
            try
            {
                proc.PriorityClass = p;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                // Already ended: the exit code tells.
            }
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var reg = limit.Token.Register(static state => Kill((Process)state!), proc);
        var err = PumpLinesAsync(proc.StandardError, onStderrLine ?? (static _ => { }));
        var outTask = stdout is not null
            ? proc.StandardOutput.BaseStream.CopyToAsync(stdout, limit.Token)
            : proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, limit.Token);
        if (stdin is not null)
        {
            try
            {
                await proc.StandardInput.BaseStream.WriteAsync(stdin, limit.Token).ConfigureAwait(false);
                await proc.StandardInput.BaseStream.FlushAsync(limit.Token).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                // ffmpeg stopped reading (damaged file): the exit code says so.
            }
            catch (OperationCanceledException)
            {
                // Handled below with the wait.
            }
        }

        try
        {
            await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(err, outTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            using var wait = new CancellationTokenSource(killWait);
            try
            {
                await proc.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The job object closes with this method and ends whatever is left.
            }

            ct.ThrowIfCancellationRequested();
            throw new EngineException(ErrorCodes.EngineError, "ffmpeg ran too long and was stopped.");
        }

        return proc.ExitCode;
    }

    /// <summary>Hands over each stderr line; carriage returns end a line too (the stats line is redrawn in place).</summary>
    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var ch = buffer[i];
                    if (ch is '\r' or '\n')
                    {
                        if (line.Length > 0)
                        {
                            onLine(line.ToString());
                            line.Clear();
                        }
                    }
                    else if (line.Length < 2048)
                    {
                        line.Append(ch);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Killed mid-read.
        }

        if (line.Length > 0)
        {
            onLine(line.ToString());
        }
    }

    /// <summary>Ends the one process this class started and its children (never any other process).</summary>
    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// A write-only stream that keeps only the last complete PNG picture of a concatenated PNG stream (what
/// <c>-f image2pipe</c> writes), so a clip of any length costs the memory of about two pictures.
/// </summary>
internal sealed class LastPngSink : Stream
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const long MaxPictureBytes = 128L * 1024 * 1024;

    private MemoryStream _buffer = new();
    private int _parsed;

    /// <summary>The last complete picture seen so far, or null.</summary>
    public byte[]? Last { get; private set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _buffer.Write(buffer, offset, count);
        Parse();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _buffer.Write(buffer);
        Parse();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    private void Parse()
    {
        while (true)
        {
            var data = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
            if (_parsed == 0)
            {
                if (data.Length < Signature.Length)
                {
                    return;
                }

                if (!data[..Signature.Length].SequenceEqual(Signature))
                {
                    throw new InvalidDataException("Not a PNG stream.");
                }

                _parsed = Signature.Length;
            }

            if (data.Length < _parsed + 8)
            {
                return;
            }

            var length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[_parsed..]);
            var end = _parsed + 8 + length + 4;
            if (end > MaxPictureBytes)
            {
                throw new InvalidDataException("The picture is too large.");
            }

            if (data.Length < end)
            {
                return;
            }

            var isEnd = data.Slice(_parsed + 4, 4).SequenceEqual("IEND"u8);
            _parsed = (int)end;
            if (!isEnd)
            {
                continue;
            }

            Last = data[.._parsed].ToArray();
            var rest = data[_parsed..].ToArray();
            _buffer = new MemoryStream();
            _buffer.Write(rest);
            _parsed = 0;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
