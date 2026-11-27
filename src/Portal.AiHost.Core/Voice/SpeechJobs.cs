using System.Globalization;
using System.Threading.Channels;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Voice;

public sealed class SpeechJobOptions
{
    /// <summary>Folder the host owns for result files. Files here are only ever created, never overwritten or deleted.</summary>
    public required string OutputDirectory { get; init; }

    public int MaxTextChars { get; init; } = 20_000;
    public int MaxChunkChars { get; init; } = 300;
    public int MaxQueuedPerApp { get; init; } = 8;

    /// <summary>How many finished jobs stay in the list (the files stay on disk).</summary>
    public int MaxRememberedJobs { get; init; } = 200;

    public double MinSpeed { get; init; } = 0.5;
    public double MaxSpeed { get; init; } = 2.0;
}

/// <summary>Splits text into pieces a voice engine reads well: sentence ends first, then spaces, never over the limit.</summary>
public static class SpeechText
{
    public static List<string> Split(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();

        void Flush()
        {
            var piece = current.ToString().Trim();
            if (piece.Length > 0)
            {
                chunks.Add(piece);
            }

            current.Clear();
        }

        foreach (var sentence in Sentences(text))
        {
            var s = sentence;
            while (s.Length > maxChars)
            {
                var cut = s.LastIndexOf(' ', maxChars - 1, maxChars);
                if (cut <= 0)
                {
                    cut = maxChars;
                }

                if (current.Length + cut > maxChars)
                {
                    Flush();
                }

                current.Append(s, 0, cut);
                Flush();
                s = s[cut..].TrimStart();
            }

            if (current.Length + s.Length + 1 > maxChars)
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(s);
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var end = c is '.' or '!' or '?' or '\n' or '。' or '！' or '？';
            if (end && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) || c == '\n'))
            {
                var piece = text[start..(i + 1)].Trim();
                if (piece.Length > 0)
                {
                    yield return piece;
                }

                start = i + 1;
            }
        }

        var rest = text[start..].Trim();
        if (rest.Length > 0)
        {
            yield return rest;
        }
    }
}

/// <summary>
/// Speech jobs: a small queue with one worker (the voice engine reads one chunk at a time). A job reads the text chunk by
/// chunk, so progress is real and a cancel takes effect between chunks. The result is one WAV in the host's output
/// folder under a unique name, tagged as made with Portal. Jobs belong to the app that made them. Self-contained on
/// purpose; it will move onto the shared generation queue later.
/// </summary>
public sealed class SpeechJobManager : IAsyncDisposable
{
    private readonly VoiceLibrary _library;
    private readonly ISpeechEngine _engine;
    private readonly SpeechJobOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public SpeechJobManager(VoiceLibrary library, ISpeechEngine engine, SpeechJobOptions options, TimeProvider? time = null)
    {
        _library = library;
        _engine = engine;
        _options = options;
        _time = time ?? TimeProvider.System;
        _worker = Task.Run(RunAsync);
    }

    public SpeechJobOptions Options => _options;

    public SpeechJobInfo Create(string appId, SpeechJobRequest request)
    {
        var text = (request.Text ?? "").Trim();
        if (text.Length == 0)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "text is required.", "text");
        }

        if (text.Length > _options.MaxTextChars)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"text is longer than {_options.MaxTextChars} characters.", "text");
        }

        if (request.Format is not null && !string.Equals(request.Format, "wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "Only \"wav\" is available as an output format.", "format");
        }

        var speed = request.Speed ?? 1.0;
        if (double.IsNaN(speed) || speed < _options.MinSpeed || speed > _options.MaxSpeed)
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                string.Create(CultureInfo.InvariantCulture, $"speed must be {_options.MinSpeed} to {_options.MaxSpeed}."), "speed");
        }

        var hasClone = !string.IsNullOrEmpty(request.VoiceId);
        var hasBuiltIn = !string.IsNullOrEmpty(request.BuiltinVoice);
        if (hasClone == hasBuiltIn)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "Send exactly one of voice_id and builtin_voice.",
                hasClone ? "builtin_voice" : "voice_id");
        }

        var availability = _engine.Availability;
        string? samplePath = null;
        if (hasClone)
        {
            if (!VoiceLibrary.IsValidId(request.VoiceId) || _library.Get(request.VoiceId!) is null)
            {
                throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.", "voice_id");
            }

            if (!availability.Cloning)
            {
                throw new ApiException(ErrorCodes.NoModel, "No voice cloning model is installed.");
            }

            samplePath = _library.SamplePath(request.VoiceId!) ?? throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.", "voice_id");
        }
        else
        {
            if (!availability.BuiltIn || availability.BuiltInVoices.All(v => v.Id != request.BuiltinVoice))
            {
                throw hasBuiltIn && availability.BuiltIn
                    ? new ApiException(ErrorCodes.InvalidRequest, "Unknown built-in voice.", "builtin_voice")
                    : new ApiException(ErrorCodes.NoModel, "No speech model is installed.");
            }
        }

        var chunks = SpeechText.Split(text, _options.MaxChunkChars);
        var job = new Job(TokenFormat.NewId("sp_", 12), appId, request.VoiceId, request.BuiltinVoice, samplePath, speed, chunks,
            _time.GetUtcNow());
        lock (_gate)
        {
            var waiting = _jobs.Values.Count(j => j.AppId == appId && j.Status is SpeechJobStatuses.Queued or SpeechJobStatuses.Running);
            if (waiting >= _options.MaxQueuedPerApp)
            {
                throw new ApiException(ErrorCodes.QueueFull, "Too many speech jobs are waiting for this app.", retryAfterMs: 5000);
            }

            _jobs[job.Id] = job;
            _order.Add(job.Id);
            Trim();
        }

        _queue.Writer.TryWrite(job);
        return Snapshot(job, includePath: true);
    }

    /// <summary>The job, or null when unknown or owned by another app.</summary>
    public SpeechJobInfo? Get(string appId, string id, bool includePath)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out var job) && job.AppId == appId ? Snapshot(job, includePath) : null;
        }
    }

    /// <summary>Cancels a queued or running job. A finished job is returned as it is.</summary>
    public SpeechJobInfo? Cancel(string appId, string id, bool includePath)
    {
        Job? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job) || job.AppId != appId)
            {
                return null;
            }

            if (job.Status == SpeechJobStatuses.Queued)
            {
                job.Status = SpeechJobStatuses.Cancelled;
                job.FinishedAt = _time.GetUtcNow();
            }
        }

        job.Cancel.Cancel();
        lock (_gate)
        {
            return Snapshot(job, includePath);
        }
    }

    /// <summary>The finished WAV bytes of a completed job, or null (unknown, other app, or not completed).</summary>
    public byte[]? ReadResult(string appId, string id)
    {
        string? path;
        lock (_gate)
        {
            path = _jobs.TryGetValue(id, out var job) && job.AppId == appId && job.Status == SpeechJobStatuses.Completed
                ? job.ResultPath
                : null;
        }

        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>True while a queued or running job (any app) reads with this voice.</summary>
    public bool IsVoiceInUse(string voiceId)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => j.VoiceId == voiceId && j.Status is SpeechJobStatuses.Queued or SpeechJobStatuses.Running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    // ---- worker --------------------------------------------------------------------------------

    private async Task RunAsync()
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await RunJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunJobAsync(Job job)
    {
        lock (_gate)
        {
            if (job.Status != SpeechJobStatuses.Queued)
            {
                return; // Cancelled while waiting.
            }

            job.Status = SpeechJobStatuses.Running;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, _stop.Token);
        try
        {
            var parts = new List<PcmAudio>();
            for (var i = 0; i < job.Chunks.Count; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var audio = await _engine.SynthesizeAsync(
                    new SpeechChunkRequest(job.Chunks[i], job.Speed, job.BuiltinVoice, job.SamplePath), linked.Token).ConfigureAwait(false);
                parts.Add(audio);
                lock (_gate)
                {
                    job.Progress = (i + 1) / (double)job.Chunks.Count * 0.95;
                }
            }

            linked.Token.ThrowIfCancellationRequested();
            var joined = Join(parts);
            var bytes = WavAudio.WritePcm16(joined);
            var path = WriteResult(job, bytes);
            lock (_gate)
            {
                job.ResultPath = path;
                job.Result = new SpeechJobResult
                {
                    Format = "wav",
                    FileName = Path.GetFileName(path),
                    Bytes = bytes.Length,
                    DurationMs = joined.DurationMs,
                    SampleRate = joined.SampleRate,
                };
                job.Progress = 1;
                job.Status = SpeechJobStatuses.Completed;
                job.FinishedAt = _time.GetUtcNow();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                job.Status = SpeechJobStatuses.Cancelled;
                job.FinishedAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                job.Status = SpeechJobStatuses.Failed;
                job.FinishedAt = _time.GetUtcNow();
                job.Error = ex switch
                {
                    EngineException e => new SpeechJobError { Code = e.ErrorCode, Message = "The voice engine could not read this text." },
                    EngineCrashedException => new SpeechJobError { Code = ErrorCodes.EngineRestarting, Message = "The voice engine stopped. Try again." },
                    _ => new SpeechJobError { Code = ErrorCodes.InternalError, Message = "Unexpected host error." },
                };
            }
        }
    }

    private static PcmAudio Join(List<PcmAudio> parts)
    {
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("No audio was produced.");
        }

        var rate = parts[0].SampleRate;
        var gap = rate / 5; // 200 ms of silence between pieces.
        var total = parts.Sum(p => p.Samples.Length) + gap * (parts.Count - 1);
        var all = new short[total];
        var pos = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i].SampleRate == rate ? parts[i] : WavAudio.Resample(parts[i], rate);
            if (pos + part.Samples.Length > all.Length)
            {
                Array.Resize(ref all, pos + part.Samples.Length);
            }

            part.Samples.CopyTo(all, pos);
            pos += part.Samples.Length + (i < parts.Count - 1 ? gap : 0);
        }

        return new PcmAudio(pos < all.Length ? all[..pos] : all, rate);
    }

    /// <summary>Writes the result to a file name that does not exist yet: the job id makes it unique, and a clash gets "(2)".</summary>
    private string WriteResult(Job job, byte[] bytes)
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        var stamp = job.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = AtomicFile.UniquePath(_options.OutputDirectory, $"speech-{stamp}-{job.Id}.wav");
        AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }

    private void Trim()
    {
        while (_order.Count > _options.MaxRememberedJobs)
        {
            var oldest = _order.FirstOrDefault(id => _jobs[id].Status is not (SpeechJobStatuses.Queued or SpeechJobStatuses.Running));
            if (oldest is null)
            {
                return;
            }

            _order.Remove(oldest);
            _jobs.Remove(oldest); // The record only: the result file stays where it is.
        }
    }

    private SpeechJobInfo Snapshot(Job job, bool includePath)
    {
        int? position = null;
        if (job.Status == SpeechJobStatuses.Queued)
        {
            position = _jobs.Values.Count(j => j.Status == SpeechJobStatuses.Queued && j.CreatedAt < job.CreatedAt) +
                (_jobs.Values.Any(j => j.Status == SpeechJobStatuses.Running) ? 1 : 0);
        }

        return new SpeechJobInfo
        {
            Id = job.Id,
            Status = job.Status,
            Progress = Math.Round(job.Progress, 3),
            VoiceId = job.VoiceId,
            BuiltinVoice = job.BuiltinVoice,
            CreatedAt = job.CreatedAt,
            FinishedAt = job.FinishedAt,
            QueuePosition = position,
            Result = job.Result is null ? null : new SpeechJobResult
            {
                Format = job.Result.Format,
                FileName = job.Result.FileName,
                Bytes = job.Result.Bytes,
                DurationMs = job.Result.DurationMs,
                SampleRate = job.Result.SampleRate,
                Path = includePath ? job.ResultPath : null,
            },
            Error = job.Error,
        };
    }

    private sealed class Job(string id, string appId, string? voiceId, string? builtinVoice, string? samplePath, double speed,
        List<string> chunks, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;
        public string AppId { get; } = appId;
        public string? VoiceId { get; } = voiceId;
        public string? BuiltinVoice { get; } = builtinVoice;
        public string? SamplePath { get; } = samplePath;
        public double Speed { get; } = speed;
        public List<string> Chunks { get; } = chunks;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public CancellationTokenSource Cancel { get; } = new();
        public string Status { get; set; } = SpeechJobStatuses.Queued;
        public double Progress { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public SpeechJobResult? Result { get; set; }
        public string? ResultPath { get; set; }
        public SpeechJobError? Error { get; set; }
    }
}
