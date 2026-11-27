using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

public sealed partial class AiHostClient
{
    /// <summary>
    /// Queues an image job (<c>POST /host/image/jobs</c>) and returns it at once, usually still <c>queued</c>. Poll it
    /// with <see cref="GetGenerationJobAsync"/> or <see cref="WaitForGenerationJobAsync"/>. Throws
    /// <see cref="AiHostException"/>, for example with <c>engine_missing</c>, <c>model_not_installed</c> or
    /// <c>insufficient_memory</c>. There is no fallback for generation.
    /// </summary>
    public Task<GenerationJob> CreateImageJobAsync(GenerationJobRequest request, CancellationToken cancellationToken = default) =>
        CreateGenerationJobAsync(ModelKinds.Image, request, cancellationToken);

    /// <summary>Queues a video job (<c>POST /host/video/jobs</c>). See <see cref="CreateImageJobAsync"/>.</summary>
    public Task<GenerationJob> CreateVideoJobAsync(GenerationJobRequest request, CancellationToken cancellationToken = default) =>
        CreateGenerationJobAsync(ModelKinds.Video, request, cancellationToken);

    /// <summary>Reads a job's state, progress, preview link and result (<c>GET /host/{kind}/jobs/{id}</c>).</summary>
    public async Task<GenerationJob> GetGenerationJobAsync(string kind, string jobId, CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, JobPath(kind, jobId), null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.GenerationJob, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists this app's jobs of a kind, newest first (<c>GET /host/{kind}/jobs</c>).</summary>
    public async Task<GenerationJobList> ListGenerationJobsAsync(string kind, CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, KindPath(kind), null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.GenerationJobList, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels a queued or running job (<c>DELETE /host/{kind}/jobs/{id}</c>) and returns its state afterwards.</summary>
    public async Task<GenerationJob> CancelGenerationJobAsync(string kind, string jobId, CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Delete, JobPath(kind, jobId), null, null, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.GenerationJob, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a finished job's file (PNG picture or AVI clip) into <paramref name="destination"/> and returns the byte count.
    /// The stream is written as the file arrives; the caller owns and closes it.
    /// </summary>
    public async Task<long> DownloadGenerationResultAsync(string kind, string jobId, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var resp = await SendCoreAsync(HttpMethod.Get, JobPath(kind, jobId) + "/result", null, null, true, true, cancellationToken)
            .ConfigureAwait(false);
        await using var body = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        return total;
    }

    /// <summary>The current preview picture (PNG) of a running job, or null when there is none yet.</summary>
    public async Task<byte[]?> GetGenerationPreviewAsync(string kind, string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await SendCoreAsync(HttpMethod.Get, JobPath(kind, jobId) + "/preview", null, null, true, false, cancellationToken)
                .ConfigureAwait(false);
            return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AiHostException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Polls a job until it is succeeded, failed or cancelled and returns it. <paramref name="progress"/> gets every
    /// polled state. Cancelling the token stops waiting; it does not cancel the job (use <see cref="CancelGenerationJobAsync"/>).
    /// </summary>
    public async Task<GenerationJob> WaitForGenerationJobAsync(string kind, string jobId, TimeSpan? pollInterval = null,
        IProgress<GenerationJob>? progress = null, CancellationToken cancellationToken = default)
    {
        var interval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(1);
        while (true)
        {
            var job = await GetGenerationJobAsync(kind, jobId, cancellationToken).ConfigureAwait(false);
            progress?.Report(job);
            if (GenerationJobStates.IsFinal(job.State))
            {
                return job;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GenerationJob> CreateGenerationJobAsync(string kind, GenerationJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = AiHostJson.SerializeToUtf8(request, AiHostJsonContext.Default.GenerationJobRequest);
        using var resp = await SendCoreAsync(HttpMethod.Post, KindPath(kind), body, null, true, false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.GenerationJob, ct).ConfigureAwait(false);
    }

    private static string KindPath(string kind) =>
        (Routes.JobsFor(kind) ?? throw new ArgumentException("kind must be image or video.", nameof(kind))).TrimStart('/');

    private static string JobPath(string kind, string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        if (jobId.Length > 64 || !jobId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException("Not a job id.", nameof(jobId));
        }

        return KindPath(kind) + "/" + jobId;
    }
}
