using System.Diagnostics;
using System.Globalization;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Engine;

public sealed class FfmpegRunnerOptions
{
    /// <summary>Full path of ffmpeg.exe (engine\ffmpeg\ffmpeg.exe in the package).</summary>
    public string ExePath { get; init; } = "";

    /// <summary>Full path of ffprobe.exe. Empty: the file next to ffmpeg.exe.</summary>
    public string ProbePath { get; init; } = "";

    /// <summary>Host log sink. Receives exit codes only, never paths or text from the media.</summary>
    public Action<string>? Log { get; init; }

    public TimeSpan ConvertTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan JoinTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Limit of one encoder test encode (and of the encoder list).</summary>
    public TimeSpan EncoderProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a cancelled or timed-out process may take to end after the kill.</summary>
    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Encoding competes with the person using the PC: below normal.</summary>
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
}

/// <summary>
/// The host's media tools on ffmpeg. One child process per operation (see <see cref="FfmpegProcess"/>): killed with
/// its own process tree on cancel or timeout, progress read from the <c>time=</c> of stderr, output only to the path
/// the caller gives (a new file in the job's work folder; ffmpeg runs with <c>-n</c>) or to a pipe. This class deletes
/// nothing: a failed run leaves its partial output to the caller, who owns the folder.
/// </summary>
public sealed class FfmpegRunner : IMediaTools
{
    private readonly FfmpegRunnerOptions _o;

    public FfmpegRunner(FfmpegRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
    }

    public bool Available => _o.ExePath.Length > 0 && File.Exists(_o.ExePath) && File.Exists(ProbeExe);

    private string ProbeExe => _o.ProbePath.Length > 0
        ? _o.ProbePath
        : Path.Combine(Path.GetDirectoryName(_o.ExePath) ?? "", "ffprobe.exe");

    private void RequireAvailable()
    {
        if (!Available)
        {
            throw new EngineException(ErrorCodes.EngineMissing, "ffmpeg is not installed on this PC.");
        }
    }

    private static void RequireNew(string output)
    {
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new InvalidOperationException("The output path already exists.");
        }
    }

    private readonly Lock _encodersLock = new();
    private Task<IReadOnlyList<MediaEncoder>>? _encoders;

    /// <summary>
    /// The encoders that passed the test encode, probed on first use (on the thread pool, never at start-up) and
    /// cached for the life of the process. A cancelled or failed probe is not cached. Each test encode is one child
    /// process of this host, limited to <see cref="FfmpegRunnerOptions.EncoderProbeTimeout"/> and killed with its tree.
    /// </summary>
    public Task<IReadOnlyList<MediaEncoder>> ListEncodersAsync(CancellationToken ct)
    {
        if (!Available)
        {
            return Task.FromResult<IReadOnlyList<MediaEncoder>>([]);
        }

        Task<IReadOnlyList<MediaEncoder>> task;
        lock (_encodersLock)
        {
            // The probe itself is not tied to one caller's token (others wait for the same run); the host's own
            // shutdown ends it through the child-process kill of the caller that started it.
            task = _encoders ??= Task.Run(() => new EncoderProbe(RunProbeAsync, _o.Log).RunAsync(CancellationToken.None));
        }

        return AwaitAsync(task, ct);
    }

    private async Task<IReadOnlyList<MediaEncoder>> AwaitAsync(Task<IReadOnlyList<MediaEncoder>> task, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_encodersLock)
            {
                if (ReferenceEquals(_encoders, task))
                {
                    _encoders = null;
                }
            }

            return [];
        }
    }

    private async Task<(int Code, string Output)> RunProbeAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var sink = new MemoryStream();
        var code = await FfmpegProcess.RunAsync(_o.ExePath, args, null, sink, null, _o.EncoderProbeTimeout, _o.Priority, _o.KillWait, ct)
            .ConfigureAwait(false);
        return (code, Encoding.UTF8.GetString(sink.GetBuffer(), 0, (int)sink.Length));
    }

    public async Task<FfmpegMediaInfo?> ProbeAsync(string path, CancellationToken ct)
    {
        RequireAvailable();
        var text = new StringBuilder();
        var sink = new MemoryStream();
        var code = await FfmpegProcess.RunAsync(ProbeExe, FfmpegArgs.Probe(path), null, sink, null, _o.ProbeTimeout, _o.Priority,
            _o.KillWait, ct).ConfigureAwait(false);
        if (code != 0)
        {
            return null;
        }

        text.Append(Encoding.UTF8.GetString(sink.GetBuffer(), 0, (int)sink.Length));
        return FfmpegArgs.ParseProbe(text.ToString());
    }

    public async Task<double?> ProbeDurationAsync(string path, CancellationToken ct) =>
        (await ProbeAsync(path, ct).ConfigureAwait(false))?.DurationSeconds;

    public async Task<(int Width, int Height)?> ProbeSizeAsync(string path, CancellationToken ct) =>
        await ProbeAsync(path, ct).ConfigureAwait(false) is { } info ? (info.Width, info.Height) : null;

    public Task ConvertToMp4Async(string inputPath, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        ConvertToMp4Async(inputPath, outputPath, null, progress, ct);

    public async Task ConvertToMp4Async(string inputPath, string outputPath, MediaEncodeSettings? encode, IProgress<double>? progress,
        CancellationToken ct)
    {
        RequireAvailable();
        RequireNew(outputPath);
        var total = (await ProbeAsync(inputPath, ct).ConfigureAwait(false))?.DurationSeconds ?? 0;
        await RunToFileAsync(FfmpegArgs.ConvertToMp4(inputPath, outputPath, encode), outputPath, total, progress, _o.ConvertTimeout, ct)
            .ConfigureAwait(false);
    }

    public async Task JoinAsync(MediaJoinPlan plan, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireAvailable();
        RequireNew(outputPath);
        FfmpegMediaInfo? first = null;
        var durations = new List<double>(plan.Clips.Count);
        foreach (var clip in plan.Clips)
        {
            var info = await ProbeAsync(clip, ct).ConfigureAwait(false)
                ?? throw new EngineException(ErrorCodes.EngineError, "A clip of the join could not be read.");
            first ??= info;
            durations.Add(info.DurationSeconds);
        }

        var args = FfmpegArgs.Join(plan, first!, durations, outputPath, out var total);
        await RunToFileAsync(args, outputPath, total, progress, _o.JoinTimeout, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ExtractLastFramePngAsync(string inputPath, CancellationToken ct)
    {
        RequireAvailable();
        var sink = new LastPngSink();
        int code;
        try
        {
            code = await FfmpegProcess.RunAsync(_o.ExePath, FfmpegArgs.LastFrame(inputPath), null, sink, null, _o.FrameTimeout,
                _o.Priority, _o.KillWait, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            throw new EngineException(ErrorCodes.EngineError, "ffmpeg wrote no picture.", e);
        }

        if (code != 0 || sink.Last is not { Length: > 0 } png)
        {
            _o.Log?.Invoke($"ffmpeg last frame ended with exit code {code}.");
            throw new EngineException(ErrorCodes.EngineError, "The last picture of the clip could not be read.");
        }

        return png;
    }

    private async Task RunToFileAsync(IReadOnlyList<string> args, string outputPath, double totalSeconds, IProgress<double>? progress,
        TimeSpan timeout, CancellationToken ct)
    {
        void OnLine(string line)
        {
            if (progress is not null && totalSeconds > 0 && FfmpegProgress.TryParseSeconds(line, out var seconds))
            {
                progress.Report(FfmpegProgress.Fraction(seconds, totalSeconds));
            }
        }

        var code = await FfmpegProcess.RunAsync(_o.ExePath, args, null, null, OnLine, timeout, _o.Priority, _o.KillWait, ct)
            .ConfigureAwait(false);
        var info = new FileInfo(outputPath);
        if (code != 0 || !info.Exists || info.Length == 0)
        {
            _o.Log?.Invoke($"ffmpeg ended with exit code {code.ToString(CultureInfo.InvariantCulture)}.");
            throw new EngineException(ErrorCodes.EngineError, code == 0
                ? "ffmpeg wrote no output."
                : $"ffmpeg failed (exit code {code.ToString(CultureInfo.InvariantCulture)}).");
        }

        progress?.Report(1);
    }
}
