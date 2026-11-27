using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Generation;

// ffmpeg-backed parts of the video jobs (docs/api-v1.md): MP4 output, the last picture of a clip, and joins.
// Nothing here runs unless GenerationServiceOptions.Media says ffmpeg is installed.
public sealed partial class GenerationService
{
    private readonly ConcurrentDictionary<string, JoinJob> _joins = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _joinGate = new(1, 1);
    private readonly SemaphoreSlim _frameGate = new(2, 2);

    private const int MaxAudioBytes = 64 * 1024 * 1024;

    private bool MediaAvailable => _o.Media?.Available == true;

    /// <summary>The <c>container</c> values a video job may ask for now.</summary>
    private List<string>? VideoContainersNow(bool video) =>
        video ? (MediaAvailable ? [GenerationContainers.Avi, GenerationContainers.Mp4] : [GenerationContainers.Avi]) : null;

    private static bool IsJobId(string text) => JobIdText().IsMatch(text);

    [GeneratedRegex("^gen_[0-9a-f]{16,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdText();

    // ---- MP4 output of a video job ----------------------------------------------------------------

    /// <summary>
    /// Checked at creation: an <c>mp4</c> request needs ffmpeg. (The shape of the value is checked by ValidateShape.)
    /// </summary>
    private void CheckContainer(GenerationJobRequest request)
    {
        if (request.Container == GenerationContainers.Mp4 && !MediaAvailable)
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter,
                "MP4 output needs ffmpeg, which is not installed on this PC. Ask for avi.", "container");
        }
    }

    private static void ValidateContainer(GenerationJobRequest r, bool video)
    {
        if (r.Container is null)
        {
            return;
        }

        if (!video)
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "container is for video jobs.", "container");
        }

        if (r.Container is not (GenerationContainers.Avi or GenerationContainers.Mp4))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "container must be avi or mp4.", "container");
        }
    }

    /// <summary>
    /// When the job asked for MP4: converts the finished AVI into <c>converting.mp4</c> of the job's work folder, then
    /// renames it to the job's partial output and removes the job's own AVI. The AVI is finished and marked so before
    /// this runs, so a crash in between leaves it for the start sweep to publish. When the conversion fails (not
    /// cancelled) the job keeps the AVI and still succeeds. Returns the partial output to publish.
    /// </summary>
    private async Task<string> ConvertIfAskedAsync(Job job, string aviPartial, CancellationToken ct)
    {
        if (job.Settings.Container != GenerationContainers.Mp4 || _o.Media is not { Available: true } media)
        {
            return aviPartial;
        }

        job.Update(j =>
        {
            j.Stage = GenerationStages.Converting;
            j.Step = null;
            j.Steps = null;
        });
        Raise(job);
        var temp = GenerationOutputFolder.ConvertingPath(job.WorkDir!);
        try
        {
            await media.ConvertToMp4Async(aviPartial, temp, null, ct).ConfigureAwait(false);
            var mp4 = GenerationOutputFolder.PartialPath(job.WorkDir!, ".mp4");
            File.Move(temp, mp4, overwrite: false); // Same folder, a new name.
            _o.Output.DeletePartial(aviPartial); // The job's own AVI; the MP4 replaces it.
            return mp4;
        }
        catch (Exception e) when (e is EngineException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _o.Log?.Invoke("MP4 conversion failed; the AVI is kept: " + e.GetType().Name);
            return aviPartial;
        }
    }

    // ---- routes ------------------------------------------------------------------------------------

    /// <summary>Adds the video-only routes: last picture of a job, and the join routes.</summary>
    private void MapMediaRoutes(ApiPipeline pipeline, string kind, string root)
    {
        if (kind != ModelKinds.Video)
        {
            return;
        }

        var json = AiHostJsonContext.Default;
        pipeline.Map("GET", root + "/{id}/last-frame", c =>
            WriteLastFrameAsync(c, JobResults.ResultIfSucceeded(Find(kind, Owner(c), c.RouteValues["id"]))));

        var joins = Routes.VideoJoins;
        pipeline.Map("POST", joins, async c =>
        {
            var body = c.ReadJson(json.VideoJoinRequest);
            var job = CreateJoin(Owner(c), body);
            c.ResponseHeaders["Location"] = joins + "/" + job.Id;
            await c.WriteJsonAsync(202, job, json.GenerationJob).ConfigureAwait(false);
        });
        pipeline.Map("GET", joins, c => c.WriteJsonAsync(200, new GenerationJobList
        {
            Data = [.. _joins.Values.Where(j => Mine(j, Owner(c))).OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot())],
        }, json.GenerationJobList));
        pipeline.Map("GET", joins + "/{id}", c => c.WriteJsonAsync(200, FindJoin(Owner(c), c.RouteValues["id"]).Snapshot(), json.GenerationJob));
        pipeline.Map("DELETE", joins + "/{id}", async c =>
        {
            var job = FindJoin(Owner(c), c.RouteValues["id"]);
            if (!job.IsFinal)
            {
                await job.Cancel.CancelAsync().ConfigureAwait(false);
                if (job.Task is { } t)
                {
                    await Task.WhenAny(t, Task.Delay(_o.CancelWait)).ConfigureAwait(false);
                }
            }

            await c.WriteJsonAsync(200, job.Snapshot(), json.GenerationJob).ConfigureAwait(false);
        });
        pipeline.Map("GET", joins + "/{id}/result", c =>
        {
            var job = FindJoin(Owner(c), c.RouteValues["id"]);
            return job.ResultPathIfSucceeded() is { } path
                ? WriteFileAsync(c, path, ContentTypeOf(path))
                : throw new ApiException(ErrorCodes.NotFound, "The join has no result (yet).");
        });
        pipeline.Map("GET", joins + "/{id}/last-frame", c =>
            WriteLastFrameAsync(c, FindJoin(Owner(c), c.RouteValues["id"]).ResultPathIfSucceeded()));
    }

    private async Task WriteLastFrameAsync(ApiContext c, string? path)
    {
        if (_o.Media is not { Available: true } media)
        {
            throw new ApiException(ErrorCodes.EngineMissing, "ffmpeg is not installed on this PC.");
        }

        if (path is null || !File.Exists(path))
        {
            throw new ApiException(ErrorCodes.NotFound, "The job has no result (yet).");
        }

        await _frameGate.WaitAsync(c.Aborted).ConfigureAwait(false);
        byte[] png;
        try
        {
            png = await media.ExtractLastFramePngAsync(path, c.Aborted).ConfigureAwait(false);
        }
        catch (EngineException e)
        {
            throw new ApiException(e.ErrorCode, e.Message);
        }
        finally
        {
            _frameGate.Release();
        }

        await c.WriteBytesAsync(200, "image/png", png).ConfigureAwait(false);
    }

    // ---- joins -------------------------------------------------------------------------------------

    private static bool Mine(JoinJob j, (string AppId, string DeviceId) owner) => j.AppId == owner.AppId && j.DeviceId == owner.DeviceId;

    private JoinJob FindJoin((string AppId, string DeviceId) owner, string id) =>
        _joins.TryGetValue(id, out var job) && Mine(job, owner) ? job : throw new ApiException(ErrorCodes.NotFound, "No such join.");

    /// <summary>The finished result file of one of the caller's video jobs (the clips of a join), or null.</summary>
    private string? FinishedClipOf((string AppId, string DeviceId) owner, string jobId) =>
        _jobs.TryGetValue(jobId, out var j) && j.Kind == ModelKinds.Video && j.AppId == owner.AppId && j.DeviceId == owner.DeviceId
            ? JobResults.ResultIfSucceeded(j)
            : _joins.TryGetValue(jobId, out var join) && Mine(join, owner) ? join.ResultPathIfSucceeded() : null;

    /// <summary>
    /// Turns a clip reference into the file of one of the caller's finished video jobs (or joins). Only a job id is
    /// accepted: a file name, a path, a drive, a folder or the id of another app is refused and nothing is read.
    /// </summary>
    internal string ResolveClip((string AppId, string DeviceId) owner, string reference, int index)
    {
        var param = $"clips[{index.ToString(CultureInfo.InvariantCulture)}]";
        var text = reference?.Trim() ?? "";
        if (UploadService.IsUploadId(text))
        {
            // An upload id: only the caller's own finished video upload (another app's id is the same not_found).
            return _o.Uploads?.ResolveVideo(owner, text)
                ?? throw new ApiException(ErrorCodes.NotFound, "No video upload with that id.", param);
        }

        if (!IsJobId(text))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "A clip is the id of a finished video job or of a video upload of this app.", param);
        }

        var path = FinishedClipOf(owner, text)
            ?? throw new ApiException(ErrorCodes.NotFound, "No finished video job with that id.", param);
        return File.Exists(path) ? path : throw new ApiException(ErrorCodes.NotFound, "The clip file is no longer on this PC.", param);
    }

    /// <summary>The transitions after each clip but the last, from the request's shorthand.</summary>
    internal static List<MediaTransition> NormalizeTransitions(int clips, List<VideoJoinTransition>? given)
    {
        var gaps = clips - 1;
        var list = given ?? [];
        if (gaps == 0 ? list.Count != 0 : list.Count != 0 && list.Count != 1 && list.Count != gaps)
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                "transitions needs none, one (for every gap) or one per gap (the number of clips minus one).", "transitions");
        }

        var result = new List<MediaTransition>(gaps);
        for (var i = 0; i < gaps; i++)
        {
            var t = list.Count == 0 ? null : list[list.Count == 1 ? 0 : i];
            var kind = t?.Kind ?? MediaTransitions.Cut;
            if (kind == MediaTransitions.Cut)
            {
                result.Add(new MediaTransition(MediaTransitions.Cut, 0));
            }
            else if (kind == MediaTransitions.Crossfade)
            {
                var seconds = t?.Seconds ?? 0.5;
                if (double.IsNaN(seconds) || seconds < 0.05 || seconds > VideoJoinLimits.MaxCrossfadeSeconds)
                {
                    throw new ApiException(ErrorCodes.InvalidRequest,
                        $"A crossfade lasts 0.05 to {VideoJoinLimits.MaxCrossfadeSeconds.ToString(CultureInfo.InvariantCulture)} seconds.",
                        "transitions");
                }

                result.Add(new MediaTransition(MediaTransitions.Crossfade, seconds));
            }
            else
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "A transition is cut or crossfade.", "transitions");
            }
        }

        return result;
    }

    private sealed record AudioSource(string Role, double Volume, double Start, byte[] Bytes);

    private AudioSource[] ResolveAudio((string AppId, string DeviceId) owner, List<VideoJoinAudio>? audio)
    {
        if (audio is null || audio.Count == 0)
        {
            return [];
        }

        if (audio.Count > VideoJoinLimits.MaxAudioTracks)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"A join takes at most {VideoJoinLimits.MaxAudioTracks} audio tracks.", "audio");
        }

        var result = new List<AudioSource>(audio.Count);
        for (var i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            var param = $"audio[{i.ToString(CultureInfo.InvariantCulture)}]";
            var role = a.Role ?? MediaAudioRoles.Music;
            if (role is not (MediaAudioRoles.Music or MediaAudioRoles.Voice))
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "role is music or voice.", param);
            }

            var volume = a.Volume ?? 1;
            var start = a.StartSeconds ?? 0;
            if (double.IsNaN(volume) || volume < 0 || volume > 2 || double.IsNaN(start) || start < 0 || start > 3600)
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "volume is 0 to 2 and start_seconds 0 to 3600.", param);
            }

            if ((a.SpeechJob is null) == (a.DataBase64 is null))
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "Give exactly one of speech_job and data_base64.", param);
            }

            byte[] bytes;
            if (a.SpeechJob is { } speech)
            {
                bytes = _o.SpeechAudio?.Invoke(owner.AppId, speech)
                    ?? throw new ApiException(ErrorCodes.NotFound, "No finished speech job with that id.", param);
            }
            else
            {
                try
                {
                    bytes = Convert.FromBase64String(a.DataBase64!);
                }
                catch (FormatException)
                {
                    throw new ApiException(ErrorCodes.InvalidRequest, "data_base64 is not valid base64.", param);
                }
            }

            if (bytes.Length == 0 || bytes.Length > MaxAudioBytes)
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "The audio is empty or too large.", param);
            }

            result.Add(new AudioSource(role, volume, start, bytes));
        }

        return [.. result];
    }

    /// <summary>Checks a join request, resolves its inputs and queues the job.</summary>
    public GenerationJob CreateJoin((string AppId, string DeviceId) owner, VideoJoinRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_o.Runtime.IsStopping || Volatile.Read(ref _disposed) != 0)
        {
            throw new ApiException(ErrorCodes.HostStopping, "Portal AI Host is quitting.");
        }

        if (!MediaAvailable)
        {
            throw new ApiException(ErrorCodes.EngineMissing, "Joining clips needs ffmpeg, which is not installed on this PC.");
        }

        if (request.Clips is not { Count: > 0 } || request.Clips.Count > VideoJoinLimits.MaxClips)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"clips needs 1 to {VideoJoinLimits.MaxClips} entries.", "clips");
        }

        if (owner.DeviceId != ClientPrincipal.LocalDevice)
        {
            var granted = _o.DeviceGranted ?? _o.Runtime.DeviceModelControl;
            if (granted?.Invoke(owner.DeviceId) != true)
            {
                throw new ApiException(ErrorCodes.ModelControlRequired,
                    "This device does not have Model control. Ask the owner of this PC to turn it on.");
            }
        }

        if (_joins.Values.Count(j => Mine(j, owner) && !j.IsFinal) >= VideoJoinLimits.MaxOpenJoinsPerApp)
        {
            throw new ApiException(ErrorCodes.QueueFull, "This app has too many joins waiting. Try again later.", retryAfterMs: 10_000);
        }

        var clips = request.Clips.Select((c, i) => ResolveClip(owner, c, i)).ToList();
        var transitions = NormalizeTransitions(clips.Count, request.Transitions);
        var audio = ResolveAudio(owner, request.Audio);

        var job = new JoinJob(NewJobId(), owner.AppId, owner.DeviceId, _o.Time.GetUtcNow(), clips, transitions, request.Duck == true);
        job.WorkDir = _o.Output.CreateWorkFolder(job.Id);
        try
        {
            for (var i = 0; i < audio.Length; i++)
            {
                var path = Path.Combine(job.WorkDir, $"audio-{i.ToString(CultureInfo.InvariantCulture)}.dat");
                AtomicFile.WriteAllBytes(path, audio[i].Bytes); // A new file in the job's own work folder.
                job.Audio.Add(new MediaAudioTrack(path, audio[i].Role, audio[i].Volume, audio[i].Start));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            CleanJoinFiles(job);
            throw new ApiException(ErrorCodes.InternalError, "The audio could not be stored.");
        }

        _joins[job.Id] = job;
        PruneJoins();
        job.Task = Task.Run(() => RunJoinAsync(job));
        return job.Snapshot();
    }

    private async Task RunJoinAsync(JoinJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, _lifetime.Token);
        var ct = cts.Token;
        string? partial = null;
        var held = false;
        try
        {
            await _joinGate.WaitAsync(ct).ConfigureAwait(false);
            held = true;
            var media = _o.Media is { Available: true } m ? m : throw new ApiException(ErrorCodes.EngineMissing, "ffmpeg is not installed on this PC.");
            partial = GenerationOutputFolder.PartialPath(job.WorkDir!, ".mp4");
            job.Update(j =>
            {
                j.State = GenerationJobStates.Running;
                j.Stage = GenerationStages.Encoding;
                j.StartedAt = _o.Time.GetUtcNow();
            });
            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateRunning);
            var progress = new SyncProgress<double>(p => job.Update(j => j.Fraction = Math.Max(j.Fraction, p)));
            await media.JoinAsync(new MediaJoinPlan(job.Clips, job.Transitions, job.Audio, job.Duck), partial, progress, ct)
                .ConfigureAwait(false);
            var size = await media.ProbeSizeAsync(partial, ct).ConfigureAwait(false);
            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateFinished);
            var final = _o.Output.Publish(partial, ModelKinds.Video, job.Id, job.CreatedAt);
            partial = null;
            job.Update(j =>
            {
                j.Width = size?.Width ?? 0;
                j.Height = size?.Height ?? 0;
                j.State = GenerationJobStates.Succeeded;
                j.Stage = null;
                j.Fraction = 1;
                j.ResultPath = final;
                j.FinishedAt = _o.Time.GetUtcNow();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var byHost = !job.Cancel.IsCancellationRequested;
            job.Update(j =>
            {
                j.State = GenerationJobStates.Cancelled;
                j.FinishedAt = _o.Time.GetUtcNow();
                j.Error = byHost ? Error(ErrorCodes.HostStopping, "Portal AI Host is quitting.") : null;
            });
        }
        catch (ApiException e)
        {
            FailJoin(job, e.Code, e.Message);
        }
        catch (EngineException e)
        {
            FailJoin(job, e.ErrorCode, e.Message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _o.Log?.Invoke("Join failed: " + e.GetType().Name);
            FailJoin(job, ErrorCodes.InternalError, "The join failed on this PC.");
        }
        finally
        {
            if (held)
            {
                _joinGate.Release();
            }

            if (partial is not null)
            {
                var cancelled = job.SnapshotState() == GenerationJobStates.Cancelled;
                Guard(() => _o.Output.MarkState(job.WorkDir!,
                    cancelled ? GenerationOutputFolder.StateCancelled : GenerationOutputFolder.StateFailed));
                Guard(() => _o.Output.DeletePartial(partial));
            }

            Guard(() => CleanJoinFiles(job));
        }
    }

    /// <summary>Removes the join's own audio files (fixed names it wrote) and then its work folder.</summary>
    private void CleanJoinFiles(JoinJob job)
    {
        if (job.WorkDir is not { } work)
        {
            return;
        }

        for (var i = 0; i < VideoJoinLimits.MaxAudioTracks; i++)
        {
            var path = Path.Combine(work, $"audio-{i.ToString(CultureInfo.InvariantCulture)}.dat");
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path); // The join's own audio copy.
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked: the folder stays and the next start's sweep skips it.
            }
        }

        _o.Output.CleanWorkFolder(work);
    }

    private void PruneJoins()
    {
        var finished = _joins.Values.Where(j => j.IsFinal).OrderBy(j => j.CreatedAt).ToList();
        for (var i = 0; i < finished.Count - GenerationLimits.MaxRememberedJobs; i++)
        {
            _joins.TryRemove(finished[i].Id, out _);
        }
    }

    private IEnumerable<Task> RunningJoinTasks() => _joins.Values.Select(j => j.Task).Where(t => t is not null).Select(t => t!);

    private static class JobResults
    {
        public static string? ResultIfSucceeded(Job j) => j.SnapshotState() == GenerationJobStates.Succeeded ? j.ResultPath : null;
    }

    private void FailJoin(JoinJob job, string code, string message) => job.Update(j =>
    {
        j.State = GenerationJobStates.Failed;
        j.FinishedAt = _o.Time.GetUtcNow();
        j.Error = Error(code, message);
    });

    private sealed class JoinJob(string id, string appId, string deviceId, DateTimeOffset createdAt, List<string> clips,
        List<MediaTransition> transitions, bool duck)
    {
        private readonly Lock _lock = new();

        public string Id { get; } = id;
        public string AppId { get; } = appId;
        public string DeviceId { get; } = deviceId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public List<string> Clips { get; } = clips;
        public List<MediaTransition> Transitions { get; } = transitions;
        public List<MediaAudioTrack> Audio { get; } = [];
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Duck { get; } = duck;
        public CancellationTokenSource Cancel { get; } = new();
        public Task? Task { get; set; }
        public string? WorkDir { get; set; }

        // Guarded by _lock.
        public string State { get; set; } = GenerationJobStates.Queued;
        public string? Stage { get; set; }
        public double Fraction { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string? ResultPath { get; set; }
        public ApiErrorBody? Error { get; set; }

        public bool IsFinal => GenerationJobStates.IsFinal(SnapshotState());

        public string SnapshotState()
        {
            lock (_lock)
            {
                return State;
            }
        }

        public string? ResultPathIfSucceeded()
        {
            lock (_lock)
            {
                return State == GenerationJobStates.Succeeded ? ResultPath : null;
            }
        }

        public void Update(Action<JoinJob> change)
        {
            lock (_lock)
            {
                if (!GenerationJobStates.IsFinal(State))
                {
                    change(this);
                }
            }
        }

        public GenerationJob Snapshot()
        {
            lock (_lock)
            {
                long bytes = 0;
                if (ResultPath is { } r)
                {
                    try
                    {
                        bytes = new FileInfo(r).Length;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                var root = Routes.VideoJoins;
                return new GenerationJob
                {
                    Id = Id,
                    Object = "generation.join",
                    Kind = ModelKinds.Video,
                    Model = "",
                    State = State,
                    Stage = State == GenerationJobStates.Running ? Stage : null,
                    Progress = Math.Round(Fraction, 4),
                    QueuePosition = State == GenerationJobStates.Queued ? 1 : null,
                    CreatedAt = CreatedAt,
                    StartedAt = StartedAt,
                    FinishedAt = FinishedAt,
                    Result = State == GenerationJobStates.Succeeded && ResultPath is { } path ? new GenerationResultInfo
                    {
                        Url = $"{root}/{Id}/result",
                        ContentType = "video/mp4",
                        FileName = Path.GetFileName(path),
                        Bytes = bytes,
                        Width = Width,
                        Height = Height,
                    } : null,
                    Error = Error,
                };
            }
        }
    }
}
