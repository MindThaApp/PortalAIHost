using System.Collections.Concurrent;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Generation;

public sealed partial class GenerationService
{
    private readonly ConcurrentDictionary<string, UpscaleJob> _upscales = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _upscaleGpuGate = new(1, 1);
    private readonly SemaphoreSlim _upscaleCpuGate = new(1, 1);

    private const int MaxOpenUpscalesPerApp = 8;

    private static readonly string[] UpscaleTiers = ["fast", "balanced", "best"];

    // Default output megapixels per second per tier (GPU, CPU) until the Host has measured this PC.
    private static double DefaultRate(string tier, bool gpu) => (tier, gpu) switch
    {
        ("fast", true) => 8,
        ("balanced", true) => 2,
        ("best", true) => 0.5,
        ("fast", false) => 1,
        ("balanced", false) => 0.25,
        _ => 0.05,
    };

    private string? FirstUpscaleModel()
    {
        if (_o.UpscaleModelFolder is not { } folder || !Directory.Exists(folder))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*.onnx").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Why upscaling cannot run now: "no_engine", "no_model" or null when it can.</summary>
    private string? UpscaleBlocker() =>
        _o.Upscaler is not { EnginePresent: true } ? "no_engine" : FirstUpscaleModel() is null ? "no_model" : null;

    private UpscaleAvailability UpscaleAvailabilityNow()
    {
        var blocker = UpscaleBlocker();
        var ready = blocker ?? "ready";
        var best = blocker ?? (_o.UpscaleBestInstalled?.Invoke() == true ? "ready" : "needs_download");
        return new UpscaleAvailability
        {
            Image = blocker is null,
            Detail = ready,
            Tiers = new Dictionary<string, string> { ["fast"] = ready, ["balanced"] = ready, ["best"] = best },
        };
    }

    private (string Tier, int Scale, bool Gpu, string Device) CheckUpscale(UpscaleRequest r)
    {
        var tier = (r.Tier ?? "balanced").Trim().ToLowerInvariant();
        if (!UpscaleTiers.Contains(tier) || (tier == "best" && _o.UpscaleBestInstalled?.Invoke() != true))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "tier is fast or balanced (best needs the Best add-on).", "tier");
        }

        var scale = r.Scale ?? 2;
        if (scale is not (2 or 4))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "scale is 2 or 4.", "scale");
        }

        var device = (r.Device ?? "auto").Trim().ToLowerInvariant();
        if (device is not ("auto" or "gpu" or "cpu"))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "device is auto, gpu or cpu.", "device");
        }

        var gpu = device != "cpu";
        return (tier, scale, gpu, gpu ? "dml" : "cpu");
    }

    /// <summary>The input file of an upscale: the caller's own finished image job or image upload. Never a path.</summary>
    internal string ResolveUpscaleInput((string AppId, string DeviceId) owner, UpscaleSource? source)
    {
        var given = new[] { source?.Job, source?.Upload }.Count(s => !string.IsNullOrWhiteSpace(s));
        if (given != 1)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "source needs exactly one of job or upload (an id, never a path).", "source");
        }

        if (!string.IsNullOrWhiteSpace(source!.Upload))
        {
            var id = source.Upload.Trim();
            if (!UploadService.IsUploadId(id))
            {
                throw new ApiException(ErrorCodes.InvalidRequest, "source.upload is an upload id.", "source.upload");
            }

            return _o.Uploads?.ResolveImage(owner, id)
                ?? throw new ApiException(ErrorCodes.NotFound, "No picture upload with that id.", "source.upload");
        }

        var jobId = source.Job!.Trim();
        if (!IsJobId(jobId))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "source.job is the id of a finished image job.", "source.job");
        }

        var path = _jobs.TryGetValue(jobId, out var j) && j.Kind == ModelKinds.Image && j.AppId == owner.AppId && j.DeviceId == owner.DeviceId
            ? JobResults.ResultIfSucceeded(j)
            : null;
        return path is not null && File.Exists(path)
            ? path
            : throw new ApiException(ErrorCodes.NotFound, "No finished image job with that id.", "source.job");
    }

    // PNG header size, else a 2 megapixel guess.
    private static (int Width, int Height) PictureSize(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var h = new byte[24];
            if (s.Read(h, 0, 24) == 24 && h[0] == 0x89 && h[1] == 0x50)
            {
                return ((h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19], (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return (1600, 1280);
    }

    /// <summary>Estimate without queueing: seconds per image and the warning level.</summary>
    public UpscaleEstimate EstimateUpscale((string AppId, string DeviceId) owner, UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (tier, scale, gpu, _) = CheckUpscale(request);
        var input = ResolveUpscaleInput(owner, request.Source);
        var (w, h) = PictureSize(input);
        var megapixels = (double)w * scale * h * scale / 1_000_000d;
        var rate = _o.UpscaleRate?.Invoke(tier, gpu) ?? DefaultRate(tier, gpu);
        var seconds = Math.Round(megapixels / Math.Max(rate, 0.0001), 1);
        var blocker = UpscaleBlocker();
        return new UpscaleEstimate
        {
            Tier = tier,
            Device = gpu ? "gpu" : "cpu",
            Available = blocker is null,
            Reason = blocker,
            SecondsPerImage = seconds,
            Warning = UpscaleWarnings.For(seconds), // Never hides a tier for being slow; only warns.
        };
    }

    public GenerationJob CreateUpscale((string AppId, string DeviceId) owner, UpscaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_o.Runtime.IsStopping || Volatile.Read(ref _disposed) != 0)
        {
            throw new ApiException(ErrorCodes.HostStopping, "Portal AI Host is quitting.");
        }

        var (tier, scale, gpu, device) = CheckUpscale(request);
        var input = ResolveUpscaleInput(owner, request.Source);
        if (UpscaleBlocker() is { } blocker)
        {
            throw new ApiException(ErrorCodes.EngineMissing,
                blocker == "no_engine" ? "The upscale helper is not installed on this PC." : "No upscale model is installed on this PC.");
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

        if (_upscales.Values.Count(j => j.AppId == owner.AppId && j.DeviceId == owner.DeviceId && !j.IsFinal) >= MaxOpenUpscalesPerApp)
        {
            throw new ApiException(ErrorCodes.QueueFull, "This app has too many upscales waiting. Try again later.", retryAfterMs: 10_000);
        }

        var job = new UpscaleJob(NewJobId(), owner.AppId, owner.DeviceId, _o.Time.GetUtcNow(), input, scale, gpu, device)
        {
            Model = FirstUpscaleModel()!,
        };
        job.WorkDir = _o.Output.CreateWorkFolder(job.Id);
        _upscales[job.Id] = job;
        PruneUpscales();
        job.Task = Task.Run(() => RunUpscaleAsync(job));
        return job.Snapshot();
    }

    private async Task RunUpscaleAsync(UpscaleJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, _lifetime.Token);
        var ct = cts.Token;
        string? partial = null;
        SemaphoreSlim? held = null;
        try
        {
            // One GPU job at a time; CPU jobs run in their own lane so they never block the GPU queue.
            var gate = job.Gpu ? _upscaleGpuGate : _upscaleCpuGate;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            held = gate;
            var helper = _o.Upscaler is { EnginePresent: true } h ? h : throw new ApiException(ErrorCodes.EngineMissing, "The upscale helper is not installed on this PC.");
            partial = GenerationOutputFolder.PartialPath(job.WorkDir!, ".png");
            job.Update(j =>
            {
                j.State = GenerationJobStates.Running;
                j.Stage = "upscaling";
                j.StartedAt = _o.Time.GetUtcNow();
            });
            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateRunning);
            await helper.UpscaleAsync(job.Model, job.Input, partial, job.Scale, job.Device,
                p => job.Update(j => j.Fraction = Math.Max(j.Fraction, Math.Min(p, 0.99))), ct).ConfigureAwait(false);
            if (!File.Exists(partial))
            {
                throw new ApiException(ErrorCodes.InternalError, "The upscaler wrote no picture.");
            }

            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateFinished);
            var final = _o.Output.Publish(partial, ModelKinds.Image, job.Id, job.CreatedAt); // New unique name, never overwrites.
            partial = null;
            job.Update(j =>
            {
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
            FailUpscale(job, e.Code, e.Message);
        }
        catch (EngineException e)
        {
            FailUpscale(job, e.ErrorCode, e.Message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _o.Log?.Invoke("Upscale failed: " + e.GetType().Name);
            FailUpscale(job, ErrorCodes.InternalError, "The upscale failed on this PC.");
        }
        finally
        {
            held?.Release();
            if (partial is not null)
            {
                var cancelled = job.SnapshotState() == GenerationJobStates.Cancelled;
                Guard(() => _o.Output.MarkState(job.WorkDir!,
                    cancelled ? GenerationOutputFolder.StateCancelled : GenerationOutputFolder.StateFailed));
                Guard(() => _o.Output.DeletePartial(partial)); // The job's own partial file only.
            }

            if (job.WorkDir is { } work)
            {
                Guard(() => _o.Output.CleanWorkFolder(work));
            }
        }
    }

    private void FailUpscale(UpscaleJob job, string code, string message) => job.Update(j =>
    {
        j.State = GenerationJobStates.Failed;
        j.FinishedAt = _o.Time.GetUtcNow();
        j.Error = Error(code, message);
    });

    private void PruneUpscales()
    {
        var finished = _upscales.Values.Where(j => j.IsFinal).OrderBy(j => j.CreatedAt).ToList();
        for (var i = 0; i < finished.Count - GenerationLimits.MaxRememberedJobs; i++)
        {
            _upscales.TryRemove(finished[i].Id, out _);
        }
    }

    private UpscaleJob FindUpscale((string AppId, string DeviceId) owner, string id) =>
        _upscales.TryGetValue(id, out var job) && job.AppId == owner.AppId && job.DeviceId == owner.DeviceId
            ? job
            : throw new ApiException(ErrorCodes.NotFound, "No such upscale.");

    private IEnumerable<Task> RunningUpscaleTasks() => _upscales.Values.Select(j => j.Task).Where(t => t is not null).Select(t => t!);

    private void MapUpscaleRoutes(ApiPipeline pipeline)
    {
        var json = AiHostJsonContext.Default;
        var root = Routes.ImageUpscales;
        pipeline.Map("POST", root, async c =>
        {
            var job = CreateUpscale(Owner(c), c.ReadJson(json.UpscaleRequest));
            c.ResponseHeaders["Location"] = root + "/" + job.Id;
            await c.WriteJsonAsync(202, job, json.GenerationJob).ConfigureAwait(false);
        });
        pipeline.Map("POST", root + "/estimate", c =>
            c.WriteJsonAsync(200, EstimateUpscale(Owner(c), c.ReadJson(json.UpscaleRequest)), json.UpscaleEstimate));
        pipeline.Map("GET", root, c =>
        {
            var owner = Owner(c);
            return c.WriteJsonAsync(200, new GenerationJobList
            {
                Data = [.. _upscales.Values.Where(j => j.AppId == owner.AppId && j.DeviceId == owner.DeviceId)
                    .OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot())],
            }, json.GenerationJobList);
        });
        pipeline.Map("GET", root + "/{id}", c => c.WriteJsonAsync(200, FindUpscale(Owner(c), c.RouteValues["id"]).Snapshot(), json.GenerationJob));
        pipeline.Map("DELETE", root + "/{id}", async c =>
        {
            var job = FindUpscale(Owner(c), c.RouteValues["id"]);
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
        pipeline.Map("GET", root + "/{id}/result", c =>
            FindUpscale(Owner(c), c.RouteValues["id"]).ResultPathIfSucceeded() is { } path
                ? WriteFileAsync(c, path, ContentTypeOf(path))
                : throw new ApiException(ErrorCodes.NotFound, "The upscale has no result (yet)."));
    }

    private sealed class UpscaleJob(string id, string appId, string deviceId, DateTimeOffset createdAt, string input,
        int scale, bool gpu, string device)
    {
        private readonly Lock _lock = new();

        public string Id { get; } = id;
        public string AppId { get; } = appId;
        public string DeviceId { get; } = deviceId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public string Input { get; } = input;
        public int Scale { get; } = scale;
        public bool Gpu { get; } = gpu;
        public string Device { get; } = device;
        public string Model { get; init; } = "";
        public CancellationTokenSource Cancel { get; } = new();
        public Task? Task { get; set; }
        public string? WorkDir { get; set; }
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

        public void Update(Action<UpscaleJob> change)
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

                return new GenerationJob
                {
                    Id = Id,
                    Object = "generation.upscale",
                    Kind = ModelKinds.Image,
                    Model = Path.GetFileNameWithoutExtension(Model),
                    State = State,
                    Stage = State == GenerationJobStates.Running ? Stage : null,
                    Progress = Math.Round(Fraction, 4),
                    QueuePosition = State == GenerationJobStates.Queued ? 1 : null,
                    CreatedAt = CreatedAt,
                    StartedAt = StartedAt,
                    FinishedAt = FinishedAt,
                    Result = State == GenerationJobStates.Succeeded && ResultPath is { } path ? new GenerationResultInfo
                    {
                        Url = $"{Routes.ImageUpscales}/{Id}/result",
                        ContentType = "image/png",
                        FileName = Path.GetFileName(path),
                        Bytes = bytes,
                    } : null,
                    Error = Error,
                };
            }
        }
    }
}
