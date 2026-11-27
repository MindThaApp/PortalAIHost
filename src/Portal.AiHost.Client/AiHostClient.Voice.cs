using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Client;

/// <summary>Voice library and speech jobs (api-v1 section 10). None of these calls has a fallback.</summary>
public sealed partial class AiHostClient
{
    private static readonly TimeSpan DefaultSpeechPoll = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Whether this host can clone voices and read text aloud, from <c>GET /host/capabilities</c>. Null when the host has
    /// no voice feature. Check <see cref="VoiceCapabilities.Speech"/> and <see cref="VoiceCapabilities.Cloning"/> before
    /// showing voice controls.
    /// </summary>
    public async Task<VoiceCapabilities?> GetVoiceCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        (await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false)).Voice;

    /// <summary>The shared voice library (every app's voices) and the built-in voices.</summary>
    public async Task<VoiceList> ListVoicesAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "host/voices", null, options, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.VoiceList, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes a reusable voice from an MP3 or WAV sample (3 to 60 seconds of speech; 10 to 30 works best; at most about
    /// 3 MB, because the request is capped at 4 MB). <paramref name="consent"/> is <see cref="VoiceConsent.OwnVoice"/> or
    /// <see cref="VoiceConsent.Permission"/> and is required.
    /// </summary>
    public async Task<VoiceInfo> CreateVoiceAsync(string name, byte[] sample, string consent, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var body = AiHostJson.SerializeToUtf8(new VoiceCreateRequest
        {
            Name = name,
            Consent = consent,
            SampleBase64 = Convert.ToBase64String(sample),
        }, AiHostJsonContext.Default.VoiceCreateRequest);
        using var resp = await SendCoreAsync(HttpMethod.Post, "host/voices", body, options, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.VoiceInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames a voice (1 to 64 characters). Only the app that made it, or an app on the host's own PC, may.</summary>
    public async Task<VoiceInfo> RenameVoiceAsync(string voiceId, string newName, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = AiHostJson.SerializeToUtf8(new VoiceRenameRequest { Name = newName }, AiHostJsonContext.Default.VoiceRenameRequest);
        using var resp = await SendCoreAsync(HttpMethod.Patch, "host/voices/" + Uri.EscapeDataString(voiceId), body, options, true,
            false, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.VoiceInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a voice. Only the app that made it, or an app on the host's own PC, may.</summary>
    public async Task DeleteVoiceAsync(string voiceId, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Delete, "host/voices/" + Uri.EscapeDataString(voiceId), null, options, true,
            false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts reading text aloud. Poll <see cref="GetSpeechJobAsync"/>, or use <see cref="SpeakAsync"/>.</summary>
    public async Task<SpeechJobInfo> StartSpeechAsync(SpeechJobRequest request, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = AiHostJson.SerializeToUtf8(request, AiHostJsonContext.Default.SpeechJobRequest);
        using var resp = await SendCoreAsync(HttpMethod.Post, "host/speech/jobs", body, options, true, false, cancellationToken)
            .ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.SpeechJobInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Progress, queue position and, when completed, the result of a speech job.</summary>
    public async Task<SpeechJobInfo> GetSpeechJobAsync(string jobId, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "host/speech/jobs/" + Uri.EscapeDataString(jobId), null, options, true,
            false, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.SpeechJobInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels a waiting or running job. A finished job is returned as it is.</summary>
    public async Task<SpeechJobInfo> CancelSpeechAsync(string jobId, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Delete, "host/speech/jobs/" + Uri.EscapeDataString(jobId), null, options, true,
            false, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(resp, AiHostJsonContext.Default.SpeechJobInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The WAV file of a completed job.</summary>
    public async Task<byte[]> DownloadSpeechAsync(string jobId, RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var resp = await SendCoreAsync(HttpMethod.Get, "host/speech/jobs/" + Uri.EscapeDataString(jobId) + "/audio", null,
            options, true, false, cancellationToken).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads text aloud and returns the WAV bytes: starts the job, polls until it ends, downloads the audio. Cancelling the
    /// token cancels the job on the host. Throws <see cref="AiHostException"/> if the job fails or is cancelled.
    /// </summary>
    public async Task<byte[]> SpeakAsync(SpeechJobRequest request, IProgress<double>? progress = null, TimeSpan? pollEvery = null,
        RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var job = await StartSpeechAsync(request, options, cancellationToken).ConfigureAwait(false);
        try
        {
            while (job.Status is SpeechJobStatuses.Queued or SpeechJobStatuses.Running)
            {
                progress?.Report(job.Progress);
                await Task.Delay(pollEvery ?? DefaultSpeechPoll, cancellationToken).ConfigureAwait(false);
                job = await GetSpeechJobAsync(job.Id, options, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                await CancelSpeechAsync(job.Id, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AiHostException)
            {
            }

            throw;
        }

        if (job.Status == SpeechJobStatuses.Completed)
        {
            progress?.Report(1);
            return await DownloadSpeechAsync(job.Id, options, cancellationToken).ConfigureAwait(false);
        }

        throw new AiHostException(job.Error?.Code ?? ErrorCodes.InternalError,
            job.Error?.Message ?? "The speech job did not finish (" + job.Status + ").");
    }
}
