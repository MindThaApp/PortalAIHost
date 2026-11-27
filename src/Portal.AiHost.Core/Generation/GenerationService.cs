using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;
using Portal.AiHost.Core.Catalogue;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Runtime;

namespace Portal.AiHost.Core.Generation;

/// <summary>An installed, complete image or video model with its files on disk.</summary>
public sealed record GenerationModel(
    string Id, string DisplayName, string Kind, string Family, IReadOnlyList<GenerationComponent> Components, CatalogueEntry? Entry = null);

/// <summary>Read-only view of the installed generation models. Nothing here writes, moves or deletes a model file.</summary>
public interface IGenerationModelSource
{
    /// <summary>Installed, complete models of a kind (image or video). The first one is the default.</summary>
    Task<IReadOnlyList<GenerationModel>> ListAsync(string kind, CancellationToken ct);

    /// <summary>Marks the model's files as in use while a job runs (so they cannot be removed under it).</summary>
    Task<IAsyncDisposable> UseAsync(string modelId, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(NoUse.Instance);

    private sealed class NoUse : IAsyncDisposable
    {
        public static NoUse Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class GenerationServiceOptions
{
    public required AiHostRuntime Runtime { get; init; }
    public required IScheduler Scheduler { get; init; }
    public required IGenerationEngine Engine { get; init; }
    public required IGenerationModelSource Models { get; init; }
    public required GenerationOutputFolder Output { get; init; }

    /// <summary>Paired device: holds Model control now. Null: the runtime's own check.</summary>
    public Func<string, bool>? DeviceGranted { get; init; }

    /// <summary>Paired device: its allowed-models list lets it use the model. Null: every model.</summary>
    public Func<string, string, bool>? DeviceMayUse { get; set; }

    /// <summary>Longest a job may wait in the queue.</summary>
    public TimeSpan QueueDeadline { get; init; } = TimeSpan.FromHours(12);

    /// <summary>How long DELETE waits for a running job to end before it answers.</summary>
    public TimeSpan CancelWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Host log sink (never prompt text).</summary>
    public Action<string>? Log { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Image and video jobs (docs/api-v1.md, "Image and video jobs"). A job is created by POST, waits in the fair queue
/// under the generation slot (one job at a time on the one engine), is planned against the memory left when it starts,
/// and runs the engine as its own process. The result goes to a new unique file in the host-owned output folder.
/// Jobs belong to the app and device that created them; another caller sees not_found.
/// </summary>
public sealed partial class GenerationService : IAsyncDisposable
{
    /// <summary>Scheduler kind: image and video share one engine and one GPU, so one queue.</summary>
    public const string SchedulerKind = AiHostRuntime.GenerationSlot;

    private readonly GenerationServiceOptions _o;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    private int _availableBits; // 1: image, 2: video
    private long _availableAt = long.MinValue;
    private int _refreshing;

    /// <summary>How long the cached "engine and model present" answer of <see cref="Availability"/> is reused.</summary>
    public static readonly TimeSpan AvailabilityMaxAge = TimeSpan.FromSeconds(5);

    public GenerationService(GenerationServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _o = options;
        _o.Runtime.GenerationInfo = Availability;
        _ = RefreshAvailabilityAsync();
    }

    /// <summary>
    /// The generation section of status and capabilities. Engine and model presence is cached (it needs a store
    /// listing) and refreshed in the background when older than <see cref="AvailabilityMaxAge"/>; the job counts are live.
    /// </summary>
    public GenerationAvailability Availability()
    {
        var age = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _availableAt));
        if (age > AvailabilityMaxAge)
        {
            _ = RefreshAvailabilityAsync();
        }

        var bits = Volatile.Read(ref _availableBits);
        var (image, video) = ((bits & 1) != 0, (bits & 2) != 0);
        var states = _jobs.Values.Select(j => j.SnapshotState()).ToList();
        return new GenerationAvailability
        {
            Image = image,
            Video = video,
            Queued = states.Count(s => s == GenerationJobStates.Queued),
            Running = states.Count(s => s == GenerationJobStates.Running),
        };
    }

    /// <summary>Checks now whether the engine binary and an available model of each kind are installed.</summary>
    public async Task RefreshAvailabilityAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            var present = _o.Engine.Check().Present;
            var image = present && await HasModelAsync(ModelKinds.Image).ConfigureAwait(false);
            var video = present && await HasModelAsync(ModelKinds.Video).ConfigureAwait(false);
            var next = (image ? 1 : 0) | (video ? 2 : 0);
            if (Interlocked.Exchange(ref _availableBits, next) != next)
            {
                _o.Runtime.NoteGenerationChanged();
            }

            Interlocked.Exchange(ref _availableAt, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _o.Log?.Invoke("Generation availability check failed: " + e.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task<bool> HasModelAsync(string kind)
    {
        foreach (var m in await _o.Models.ListAsync(kind, _lifetime.Token).ConfigureAwait(false))
        {
            if (GenerationFamilies.Normalize(m.Entry?.Family ?? m.Family, m.Id + " " + m.DisplayName) is { } f &&
                _o.Engine.SupportsFamily(f, kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Paired device: its allowed-models list lets it use the model (set once the device store exists).</summary>
    public Func<string, string, bool>? DeviceMayUse
    {
        get => _o.DeviceMayUse;
        set => _o.DeviceMayUse = value;
    }

    /// <summary>Raised after a job changed state (queued, running, finished).</summary>
    public event Action<GenerationJob>? JobChanged;

    // ---- routes ------------------------------------------------------------------------------------

    /// <summary>Adds the job routes of both kinds. They need a token, from this PC or from a paired device.</summary>
    public void MapRoutes(ApiPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var json = AiHostJsonContext.Default;
        foreach (var kind in new[] { ModelKinds.Image, ModelKinds.Video })
        {
            var root = Routes.JobsFor(kind)!;
            pipeline.Map("POST", root, async c =>
            {
                var body = c.ReadJson(json.GenerationJobRequest);
                var job = await CreateAsync(kind, Owner(c), body, c.Aborted).ConfigureAwait(false);
                c.ResponseHeaders["Location"] = root + "/" + job.Id;
                await c.WriteJsonAsync(202, job, json.GenerationJob).ConfigureAwait(false);
            });
            pipeline.Map("GET", root, c => c.WriteJsonAsync(200, List(kind, Owner(c)), json.GenerationJobList));
            pipeline.Map("GET", root + "/{id}", c =>
                c.WriteJsonAsync(200, Get(kind, Owner(c), c.RouteValues["id"]), json.GenerationJob));
            pipeline.Map("DELETE", root + "/{id}", async c =>
            {
                var job = await CancelAsync(kind, Owner(c), c.RouteValues["id"]).ConfigureAwait(false);
                await c.WriteJsonAsync(200, job, json.GenerationJob).ConfigureAwait(false);
            });
            pipeline.Map("GET", root + "/{id}/result", c => WriteResultAsync(c, kind));
            pipeline.Map("GET", root + "/{id}/preview", c => WritePreviewAsync(c, kind));
        }
    }

    private static (string AppId, string DeviceId) Owner(ApiContext c) =>
        c.Principal is { } p ? (p.AppId, p.DeviceId) : throw new ApiException(ErrorCodes.AuthRequired, "Authorization: Bearer <token> is required.");

    // ---- create ------------------------------------------------------------------------------------

    /// <summary>Checks the request, engine, model, device rights and memory, then queues the job.</summary>
    public async Task<GenerationJob> CreateAsync(string kind, (string AppId, string DeviceId) owner, GenerationJobRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_o.Runtime.IsStopping || Volatile.Read(ref _disposed) != 0)
        {
            throw new ApiException(ErrorCodes.HostStopping, "Portal AI Host is quitting.");
        }

        var video = kind == ModelKinds.Video;
        ValidateShape(request, video);

        var check = _o.Engine.Check();
        if (!check.Present)
        {
            throw new ApiException(ErrorCodes.EngineMissing,
                check.Reason ?? "The image and video engine is not installed on this PC.") { Details = KindDetails(kind) };
        }

        var installed = await _o.Models.ListAsync(kind, ct).ConfigureAwait(false);
        var model = request.Model is { Length: > 0 } wanted
            ? installed.FirstOrDefault(m => string.Equals(m.Id, wanted, StringComparison.Ordinal))
            : installed.FirstOrDefault();
        if (model is null)
        {
            throw new ApiException(ErrorCodes.ModelNotInstalled, request.Model is { Length: > 0 }
                ? "That model is not installed on this PC."
                : $"No {kind} model is installed on this PC.", request.Model is { Length: > 0 } ? "model" : null) { Details = KindDetails(kind) };
        }

        var family = GenerationFamilies.Normalize(model.Entry?.Family ?? model.Family, model.Id + " " + model.DisplayName);
        if (family is null || !_o.Engine.SupportsFamily(family, kind))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "The image and video engine cannot run this model.", "model");
        }

        if (owner.DeviceId != ClientPrincipal.LocalDevice)
        {
            var granted = _o.DeviceGranted ?? _o.Runtime.DeviceModelControl;
            if (granted?.Invoke(owner.DeviceId) != true)
            {
                throw new ApiException(ErrorCodes.ModelControlRequired,
                    "This device does not have Model control. Ask the owner of this PC to turn it on.");
            }

            if (_o.DeviceMayUse is { } mayUse && !mayUse(owner.DeviceId, model.Id))
            {
                throw new ApiException(ErrorCodes.ModelNotFound, "This device may not use that model.");
            }
        }

        var open = _jobs.Values.Count(j => j.AppId == owner.AppId && j.DeviceId == owner.DeviceId && !j.IsFinal);
        if (open >= GenerationLimits.MaxOpenJobsPerApp)
        {
            throw new ApiException(ErrorCodes.QueueFull, "This app has too many image and video jobs waiting. Try again later.",
                retryAfterMs: 10_000);
        }

        var settings = Resolve(request, model, family, video);
        var initBytes = DecodeInitImage(request.InitImage, out var initExt);
        if (initBytes is not null && video && !(model.Entry?.Generation?.ImageToVideo == true ||
            model.Components.Any(c => c.Role == GenerationFileRoles.ClipVision)))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "This model cannot start a clip from a picture.", "init_image");
        }

        // Refuse at once when the run cannot fit even with no other image or video job running.
        var needs = Needs(model, family, video, settings);
        // A video job may count the RAM of a processor-only director (chat.2) as free: the director yields while it runs.
        var plan = await _o.Runtime.PlanGenerationAsync(needs, check.GpuBuild, (long)settings.Width * settings.Height, ct,
            ignoreGenerationJobs: true, minVramBytes: MinVram(model),
            ignoreSlot: video && _o.Runtime.DirectorCanYield ? ModelSlots.Second(ModelKinds.Chat) : null).ConfigureAwait(false);
        if (!plan.Fits)
        {
            throw InsufficientMemory(plan, needs);
        }

        var now = _o.Time.GetUtcNow();
        var job = new Job(NewJobId(), kind, owner.AppId, owner.DeviceId, model, family, settings, now);
        var work = _o.Output.CreateWorkFolder(job.Id);
        job.WorkDir = work;
        if (initBytes is not null)
        {
            job.InitPath = _o.Output.WriteInitImage(work, initBytes, initExt!);
        }

        _jobs[job.Id] = job;
        Prune();
        job.Task = Task.Run(() => RunAsync(job));
        Raise(job);
        return job.Snapshot(Routes.JobsFor(kind)!);
    }

    /// <summary>The catalogue's min_vram_gb in bytes, or 0 when the entry gives none.</summary>
    private static long MinVram(GenerationModel model) =>
        model.Entry?.MinVramGb is { } gb && gb > 0 ? (long)(gb * (1L << 30)) : 0;

    private static Dictionary<string, JsonElement> KindDetails(string kind) =>
        new() { ["kind"] = JsonSerializer.SerializeToElement(kind, AiHostJsonContext.Default.String) };

    private static ApiException InsufficientMemory(GenerationPlan plan, GenerationNeeds needs) =>
        new(ErrorCodes.InsufficientMemory, plan.Reason.Length > 0 ? plan.Reason : "The model cannot run in the memory available now.")
        {
            Details = new()
            {
                ["needed_bytes"] = JsonSerializer.SerializeToElement(needs.WeightsBytes + needs.ComputeBytes, AiHostJsonContext.Default.Int64),
            },
        };

    [GeneratedRegex("^[a-z0-9_+.-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SamplerPattern();

    private static void ValidateShape(GenerationJobRequest r, bool video)
    {
        static ApiException Bad(string param, string message) => new(ErrorCodes.InvalidRequest, message, param);
        if (string.IsNullOrWhiteSpace(r.Prompt))
        {
            throw Bad("prompt", "prompt is required.");
        }

        if (r.Prompt.Length > GenerationLimits.MaxPromptChars || (r.NegativePrompt?.Length ?? 0) > GenerationLimits.MaxPromptChars)
        {
            throw Bad("prompt", $"prompt and negative_prompt may have at most {GenerationLimits.MaxPromptChars} characters.");
        }

        foreach (var (name, side) in new[] { ("width", r.Width), ("height", r.Height) })
        {
            if (side is { } s && (s < GenerationLimits.MinSide || s > GenerationLimits.MaxSide || s % GenerationLimits.SideMultiple != 0))
            {
                throw Bad(name, $"{name} must be {GenerationLimits.MinSide}-{GenerationLimits.MaxSide} and a multiple of {GenerationLimits.SideMultiple}.");
            }
        }

        if (r.Steps is { } steps && (steps < 1 || steps > GenerationLimits.MaxSteps))
        {
            throw Bad("steps", $"steps must be 1-{GenerationLimits.MaxSteps}.");
        }

        if (r.CfgScale is { } cfg && (cfg < 0 || cfg > GenerationLimits.MaxCfgScale || double.IsNaN(cfg)))
        {
            throw Bad("cfg_scale", $"cfg_scale must be 0-{GenerationLimits.MaxCfgScale}.");
        }

        if (r.Sampler is { } sampler && !SamplerPattern().IsMatch(sampler))
        {
            throw Bad("sampler", "sampler is not a valid sampler name.");
        }

        if (!video && (r.Frames is > 1 || r.Fps is not null))
        {
            throw new ApiException(ErrorCodes.UnsupportedParameter, "frames and fps are for video jobs.", r.Frames is > 1 ? "frames" : "fps");
        }

        if (r.Frames is { } frames && (frames < 1 || frames > GenerationLimits.MaxFrames))
        {
            throw Bad("frames", $"frames must be 1-{GenerationLimits.MaxFrames}.");
        }

        if (r.Fps is { } fps && (fps < 1 || fps > GenerationLimits.MaxFps))
        {
            throw Bad("fps", $"fps must be 1-{GenerationLimits.MaxFps}.");
        }

        if (r.Strength is { } strength && (video || r.InitImage is null || strength < 0 || strength > 1 || double.IsNaN(strength)))
        {
            throw Bad("strength", "strength (0-1) is for image jobs with an init_image.");
        }
    }

    /// <summary>Request values, then the catalogue's defaults, then the engine's per-family defaults.</summary>
    internal sealed record JobSettings(string Prompt, string? NegativePrompt, int Width, int Height, int Steps, long Seed,
        double CfgScale, string? Sampler, double? FlowShift, int Frames, int Fps, double? Strength);

    private static JobSettings Resolve(GenerationJobRequest r, GenerationModel model, string family, bool video)
    {
        var g = model.Entry?.Generation;
        var d = FamilyDefaults(family);
        var width = r.Width ?? g?.DefaultWidth ?? d.Width;
        var height = r.Height ?? g?.DefaultHeight ?? d.Height;
        if (g?.MaxSide is { } maxSide && (width > maxSide || height > maxSide))
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"This model accepts at most {maxSide} pixels per side.", width > maxSide ? "width" : "height");
        }

        var frames = video ? r.Frames ?? g?.DefaultFrames ?? d.Frames : 1;
        if (video && g?.MaxFrames is { } maxFrames && frames > maxFrames)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, $"This model makes at most {maxFrames} frames.", "frames");
        }

        var seed = r.Seed is { } s && s >= 0 ? s : RandomNumberGenerator.GetInt32(int.MaxValue);
        return new JobSettings(r.Prompt, r.NegativePrompt, width, height, r.Steps ?? g?.DefaultSteps ?? d.Steps, seed,
            r.CfgScale ?? g?.DefaultCfgScale ?? d.CfgScale, r.Sampler ?? g?.Sampler ?? d.Sampler, g?.FlowShift ?? d.FlowShift,
            frames, r.Fps ?? g?.Fps ?? d.Fps, r.Strength);
    }

    /// <summary>Defaults per family (kept here so the host does not depend on one engine's adapter for them).</summary>
    private static (int Width, int Height, int Steps, double CfgScale, string Sampler, double? FlowShift, int Frames, int Fps) FamilyDefaults(string family) =>
        family switch
        {
            GenerationFamilies.Sdxl => (1024, 1024, 25, 7, "euler_a", null, 1, 16),
            GenerationFamilies.Sd3 => (1024, 1024, 28, 4.5, "euler", null, 1, 16),
            GenerationFamilies.Flux => (1024, 1024, 20, 1, "euler", null, 1, 16),
            GenerationFamilies.Wan => (832, 480, 20, 6, "euler", 3.0, 33, 16),
            GenerationFamilies.Sd2 => (768, 768, 20, 7, "euler_a", null, 1, 16),
            _ => (512, 512, 20, 7, "euler_a", null, 1, 16),
        };

    private static GenerationNeeds Needs(GenerationModel model, string family, bool video, JobSettings s)
    {
        var g = model.Entry?.Generation;
        var compute = g?.ComputeBytes ?? (model.Entry?.Memory.ComputeOverheadBytes is > 0 ? model.Entry.Memory.ComputeOverheadBytes : null);
        var defaultPixels = (long)(g?.DefaultWidth ?? s.Width) * (g?.DefaultHeight ?? s.Height);
        return GenerationPlanner.Estimate(model.Components, family, video, s.Width, s.Height, s.Frames, compute, defaultPixels);
    }

    private static byte[]? DecodeInitImage(string? text, out string? extension)
    {
        extension = null;
        if (text is null)
        {
            return null;
        }

        var comma = text.StartsWith("data:", StringComparison.Ordinal) ? text.IndexOf(',', StringComparison.Ordinal) : -1;
        var b64 = comma >= 0 ? text[(comma + 1)..] : text;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "init_image must be base64 PNG or JPEG.", "init_image");
        }

        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            extension = ".png";
        }
        else if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            extension = ".jpg";
        }
        else
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "init_image must be a PNG or JPEG picture.", "init_image");
        }

        return bytes;
    }

    private static string NewJobId() => "gen_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    // ---- run ---------------------------------------------------------------------------------------

    private async Task RunAsync(Job job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancel.Token, _lifetime.Token);
        var ct = cts.Token;
        string? partial = null;
        var yielded = false;
        try
        {
            var queued = new Progress<QueueEvent>(e =>
            {
                if (e.Kind == QueueEventKind.Queued)
                {
                    job.Update(j => j.QueuePosition = e.Position);
                }
            });
            await using var lease = await _o.Scheduler.AcquireAsync(new SchedulingRequest
            {
                AppId = job.AppId,
                Kind = SchedulerKind,
                Priority = RequestPriority.Batch,
                Deadline = job.CreatedAt + _o.QueueDeadline,
            }, queued, ct).ConfigureAwait(false);

            var check = _o.Engine.Check();
            if (!check.Present)
            {
                throw new ApiException(ErrorCodes.EngineMissing, check.Reason ?? "The image and video engine is not installed on this PC.");
            }

            // The model may have been removed while the job waited.
            var stillThere = await _o.Models.ListAsync(job.Kind, ct).ConfigureAwait(false);
            if (!stillThere.Any(m => m.Id == job.Model.Id))
            {
                throw new ApiException(ErrorCodes.ModelNotInstalled, "That model is not installed on this PC any more.");
            }

            await using var inUse = await _o.Models.UseAsync(job.Model.Id, ct).ConfigureAwait(false);
            var video = job.Kind == ModelKinds.Video;
            var needs = Needs(job.Model, job.Family, video, job.Settings);
            var pixels = (long)job.Settings.Width * job.Settings.Height;
            var minVram = MinVram(job.Model);
            var plan = await _o.Runtime.PlanGenerationAsync(needs, check.GpuBuild, pixels, ct, minVramBytes: minVram).ConfigureAwait(false);
            if (!plan.Fits && video && _o.Runtime.DirectorCanYield)
            {
                // Plan section 6.4: when the clip does not fit next to a processor-only director, the director yields
                // (engine stop of chat.2 only) and is loaded again after the clip.
                var second = ModelSlots.Second(ModelKinds.Chat);
                var without = await _o.Runtime.PlanGenerationAsync(needs, check.GpuBuild, pixels, ct, minVramBytes: minVram, ignoreSlot: second)
                    .ConfigureAwait(false);
                if (without.Fits && await _o.Runtime.YieldDirectorAsync(ct).ConfigureAwait(false))
                {
                    yielded = true;
                    plan = await _o.Runtime.PlanGenerationAsync(needs, check.GpuBuild, pixels, ct, minVramBytes: minVram).ConfigureAwait(false);
                    plan = plan.Fits ? plan : without;
                }
            }

            if (!plan.Fits || plan.Placement is null)
            {
                throw InsufficientMemory(plan, needs);
            }

            using var reservation = _o.Runtime.ReserveGeneration(job.Id, plan, job.Model.Id);
            partial = GenerationOutputFolder.PartialPath(job.WorkDir!, video ? ".avi" : ".png");
            job.Update(j =>
            {
                j.State = GenerationJobStates.Running;
                j.Stage = GenerationStages.Loading;
                j.StartedAt = _o.Time.GetUtcNow();
                j.QueuePosition = null;
                j.Plan = plan;
            });
            Raise(job);

            var s = job.Settings;
            var launch = new GenerationLaunch
            {
                Kind = job.Kind,
                Family = job.Family,
                Components = job.Model.Components,
                Prompt = s.Prompt,
                NegativePrompt = s.NegativePrompt,
                Width = s.Width,
                Height = s.Height,
                Steps = s.Steps,
                Seed = s.Seed,
                CfgScale = s.CfgScale,
                Sampler = s.Sampler,
                FlowShift = s.FlowShift,
                Frames = s.Frames,
                Fps = s.Fps,
                InitImagePath = job.InitPath,
                Strength = s.Strength,
                OutputPath = partial,
                PreviewPath = GenerationOutputFolder.PreviewPath(job.WorkDir!),
                Placement = plan.Placement,
                Threads = _o.Runtime.EngineThreads,
                MemoryLimitBytes = _o.Runtime.EngineMemoryCap,
            };
            var progress = new SyncProgress<GenerationProgress>(p => job.Update(j =>
            {
                j.Stage = p.Stage;
                j.Fraction = Math.Max(j.Fraction, p.Fraction);
                j.Step = p.Steps > 0 ? p.Step : null;
                j.Steps = p.Steps > 0 ? p.Steps : null;
            }));
            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateRunning);
            await _o.Engine.RunAsync(launch, progress, ct).ConfigureAwait(false);

            // From here the output is complete: a crash before the move leaves it to the start sweep to publish.
            _o.Output.MarkState(job.WorkDir!, GenerationOutputFolder.StateFinished);
            var final = _o.Output.Publish(partial, job.Kind, job.Id, job.CreatedAt);
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
            Fail(job, e.Code, e.Message);
        }
        catch (EngineException e)
        {
            Fail(job, e.ErrorCode, e.Message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _o.Log?.Invoke("Generation job failed: " + e.GetType().Name);
            Fail(job, ErrorCodes.InternalError, "The job failed on this PC.");
        }
        finally
        {
            if (partial is not null)
            {
                // Cancel or failure: only this job's own partial output goes. The marker first, so a crash in between
                // leaves the sweep a clear answer (cancelled: delete; failed: keep in recovered).
                var cancelled = job.SnapshotState() == GenerationJobStates.Cancelled;
                Guard(() => _o.Output.MarkState(job.WorkDir!,
                    cancelled ? GenerationOutputFolder.StateCancelled : GenerationOutputFolder.StateFailed));
                Guard(() => _o.Output.DeletePartial(partial));
            }

            if (job.WorkDir is { } work)
            {
                Guard(() => _o.Output.CleanWorkFolder(work));
            }

            if (yielded)
            {
                await ResumeDirectorQuietlyAsync().ConfigureAwait(false);
            }

            Raise(job);
        }
    }

    private async Task ResumeDirectorQuietlyAsync()
    {
        try
        {
            await _o.Runtime.ResumeDirectorAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The next chat.2 request loads it again.
            _o.Log?.Invoke("Director did not resume after a clip: " + e.GetType().Name);
        }
    }

    private void Fail(Job job, string code, string message) => job.Update(j =>
    {
        j.State = GenerationJobStates.Failed;
        j.FinishedAt = _o.Time.GetUtcNow();
        j.Error = Error(code, message);
    });

    private static ApiErrorBody Error(string code, string message) =>
        new() { Code = code, Message = message, Type = ErrorCatalog.Get(code).Type };

    private void Guard(Action a)
    {
        try
        {
            a();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _o.Log?.Invoke("Generation clean-up skipped: " + e.GetType().Name);
        }
    }

    private void Raise(Job job)
    {
        try
        {
            JobChanged?.Invoke(job.Snapshot(Routes.JobsFor(job.Kind)!));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    // ---- read and cancel ---------------------------------------------------------------------------

    private Job Find(string kind, (string AppId, string DeviceId) owner, string id) =>
        _jobs.TryGetValue(id, out var job) && job.Kind == kind && job.AppId == owner.AppId && job.DeviceId == owner.DeviceId
            ? job
            : throw new ApiException(ErrorCodes.NotFound, "No such job.");

    public GenerationJob Get(string kind, (string AppId, string DeviceId) owner, string id) =>
        Find(kind, owner, id).Snapshot(Routes.JobsFor(kind)!);

    public GenerationJobList List(string kind, (string AppId, string DeviceId) owner) => new()
    {
        Data = [.. _jobs.Values.Where(j => j.Kind == kind && j.AppId == owner.AppId && j.DeviceId == owner.DeviceId)
            .OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot(Routes.JobsFor(kind)!))],
    };

    /// <summary>Cancels a queued or running job and waits (bounded) for it to end. A finished job is returned as it is.</summary>
    public async Task<GenerationJob> CancelAsync(string kind, (string AppId, string DeviceId) owner, string id)
    {
        var job = Find(kind, owner, id);
        if (!job.IsFinal)
        {
            await job.Cancel.CancelAsync().ConfigureAwait(false);
            if (job.Task is { } t)
            {
                await Task.WhenAny(t, Task.Delay(_o.CancelWait)).ConfigureAwait(false);
            }
        }

        return job.Snapshot(Routes.JobsFor(kind)!);
    }

    private async Task WriteResultAsync(ApiContext c, string kind)
    {
        var job = Find(kind, Owner(c), c.RouteValues["id"]);
        if (job.ResultPath is not { } path || job.SnapshotState() != GenerationJobStates.Succeeded)
        {
            throw new ApiException(ErrorCodes.NotFound, "The job has no result (yet).");
        }

        await WriteFileAsync(c, path, ContentTypeOf(path)).ConfigureAwait(false);
    }

    private async Task WritePreviewAsync(ApiContext c, string kind)
    {
        var job = Find(kind, Owner(c), c.RouteValues["id"]);
        var path = job.WorkDir is { } w && !job.IsFinal ? GenerationOutputFolder.PreviewPath(w) : null;
        byte[]? bytes = null;
        if (path is not null)
        {
            try
            {
                // The engine may be rewriting it: read a copy, sharing everything.
                await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bytes = new byte[s.Length];
                await s.ReadExactlyAsync(bytes, c.Aborted).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                bytes = null;
            }
        }

        if (bytes is not { Length: > 0 })
        {
            throw new ApiException(ErrorCodes.NotFound, "No preview yet.");
        }

        await c.StartRawAsync(200, "image/png", c.ResponseHeaders).ConfigureAwait(false);
        await c.WriteRawAsync(bytes).ConfigureAwait(false);
        await c.CompleteAsync().ConfigureAwait(false);
    }

    private static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".avi" => "video/x-msvideo",
        ".jpg" => "image/jpeg",
        _ => "image/png",
    };

    private static async Task WriteFileAsync(ApiContext c, string path, string contentType)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(ErrorCodes.NotFound, "The result file is no longer on this PC.");
        }

        await using (stream.ConfigureAwait(false))
        {
            c.ResponseHeaders["Content-Length"] = stream.Length.ToString(CultureInfo.InvariantCulture);
            c.ResponseHeaders["Content-Disposition"] = "attachment; filename=\"" + Path.GetFileName(path) + "\"";
            await c.StartRawAsync(200, contentType, c.ResponseHeaders).ConfigureAwait(false);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, c.Aborted).ConfigureAwait(false)) > 0)
            {
                await c.WriteRawAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            await c.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Forgets the oldest finished jobs beyond the limit (their files stay where they are).</summary>
    private void Prune()
    {
        var finished = _jobs.Values.Where(j => j.IsFinal).OrderBy(j => j.CreatedAt).ToList();
        for (var i = 0; i < finished.Count - GenerationLimits.MaxRememberedJobs; i++)
        {
            _jobs.TryRemove(finished[i].Id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        var running = _jobs.Values.Select(j => j.Task).Where(t => t is not null).Select(t => t!).ToArray();
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(_o.CancelWait)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>Reports on the calling thread (no synchronisation context hop), so progress is never stale or reordered.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Job(string id, string kind, string appId, string deviceId, GenerationModel model, string family,
        JobSettings settings, DateTimeOffset createdAt)
    {
        private readonly Lock _lock = new();

        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string AppId { get; } = appId;
        public string DeviceId { get; } = deviceId;
        public GenerationModel Model { get; } = model;
        public string Family { get; } = family;
        public JobSettings Settings { get; } = settings;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public CancellationTokenSource Cancel { get; } = new();
        public Task? Task { get; set; }
        public string? WorkDir { get; set; }
        public string? InitPath { get; set; }

        // Guarded by _lock.
        public string State { get; set; } = GenerationJobStates.Queued;
        public string? Stage { get; set; }
        public double Fraction { get; set; }
        public int? Step { get; set; }
        public int? Steps { get; set; }
        public int? QueuePosition { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public GenerationPlan? Plan { get; set; }
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

        public void Update(Action<Job> change)
        {
            lock (_lock)
            {
                if (!GenerationJobStates.IsFinal(State))
                {
                    change(this);
                }
            }
        }

        public GenerationJob Snapshot(string root)
        {
            lock (_lock)
            {
                var preview = State == GenerationJobStates.Running && WorkDir is { } w && File.Exists(GenerationOutputFolder.PreviewPath(w));
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
                    Kind = Kind,
                    Model = Model.Id,
                    State = State,
                    Stage = State == GenerationJobStates.Running ? Stage : null,
                    Progress = Math.Round(Fraction, 4),
                    Step = State == GenerationJobStates.Running ? Step : null,
                    Steps = State == GenerationJobStates.Running ? Steps : null,
                    QueuePosition = State == GenerationJobStates.Queued ? QueuePosition ?? 1 : null,
                    CreatedAt = CreatedAt,
                    StartedAt = StartedAt,
                    FinishedAt = FinishedAt,
                    PreviewUrl = preview ? $"{root}/{Id}/preview" : null,
                    Plan = Plan is { Placement: { } p } plan ? new GenerationPlanInfo
                    {
                        Backend = p.Backend, GpuBytes = plan.GpuBytes, CpuBytes = plan.CpuBytes, OnCpu = [.. plan.OnCpu],
                    } : null,
                    Result = State == GenerationJobStates.Succeeded && ResultPath is { } path ? new GenerationResultInfo
                    {
                        Url = $"{root}/{Id}/result",
                        ContentType = ContentTypeOf(path),
                        FileName = Path.GetFileName(path),
                        Bytes = bytes,
                        Width = Settings.Width,
                        Height = Settings.Height,
                        Frames = Kind == ModelKinds.Video ? Settings.Frames : null,
                        Seed = Settings.Seed,
                    } : null,
                    Error = Error,
                };
            }
        }
    }
}
