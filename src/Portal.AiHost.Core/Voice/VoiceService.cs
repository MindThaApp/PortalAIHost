using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Api;

namespace Portal.AiHost.Core.Voice;

public sealed class VoiceServiceOptions
{
    /// <summary>Folder for the voice library (one subfolder per voice). Owned by the host.</summary>
    public required string VoicesDirectory { get; init; }

    /// <summary>Folder for generated audio. Owned by the host.</summary>
    public required string OutputDirectory { get; init; }

    public VoiceLibraryOptions Library { get; init; } = new();
    public int MaxTextChars { get; init; } = 20_000;
}

/// <summary>
/// The voice feature of the host: the shared voice library and the speech jobs, mapped onto the API pipeline. Every route
/// needs a token, so the same checks cover this PC's apps and signed paired devices. Voices are shared by all apps (that
/// is the point of the library); a voice is renamed or removed only by the app that made it or by an app on this PC.
/// </summary>
public sealed class VoiceService : IAsyncDisposable
{
    private readonly VoiceServiceOptions _options;
    private readonly ISpeechEngine _engine;
    private readonly IAudioDecoder? _decoder;
    private readonly IAsyncDisposable? _owned;

    public VoiceService(VoiceServiceOptions options, ISpeechEngine engine, IAudioDecoder? decoder = null, TimeProvider? time = null,
        IAsyncDisposable? owned = null)
    {
        _options = options;
        _engine = engine;
        _decoder = decoder;
        _owned = owned;
        Library = new VoiceLibrary(options.VoicesDirectory, options.Library, time);
        Jobs = new SpeechJobManager(Library, engine,
            new SpeechJobOptions { OutputDirectory = options.OutputDirectory, MaxTextChars = options.MaxTextChars }, time);
    }

    public VoiceLibrary Library { get; }

    public SpeechJobManager Jobs { get; }

    public VoiceCapabilities Capabilities()
    {
        var a = _engine.Availability;
        var sampleFormats = new List<string> { VoiceSampleFormats.Wav };
        if (_decoder is not null)
        {
            sampleFormats.AddRange(_decoder.Formats.Where(f => !sampleFormats.Contains(f)));
        }

        return new VoiceCapabilities
        {
            Cloning = a.Cloning,
            Speech = a.Speech,
            Formats = a.Speech ? ["wav"] : [],
            Reason = a.Speech ? null : a.Reason ?? UnavailableReasons.NoModel,
            SampleFormats = sampleFormats,
            MaxSampleSeconds = (int)Library.MaxSampleSeconds,
            MaxTextChars = _options.MaxTextChars,
        };
    }

    public void MapRoutes(ApiPipeline pipeline)
    {
        pipeline.Map("GET", Routes.Voices, ListVoicesAsync);
        pipeline.Map("POST", Routes.Voices, CreateVoiceAsync);
        pipeline.Map("PATCH", Routes.VoiceById, RenameVoiceAsync);
        pipeline.Map("DELETE", Routes.VoiceById, DeleteVoiceAsync);
        pipeline.Map("POST", Routes.SpeechJobs, CreateJobAsync);
        pipeline.Map("GET", Routes.SpeechJobById, GetJobAsync);
        pipeline.Map("GET", Routes.SpeechJobAudio, GetJobAudioAsync);
        pipeline.Map("DELETE", Routes.SpeechJobById, CancelJobAsync);
    }

    public async ValueTask DisposeAsync()
    {
        await Jobs.DisposeAsync().ConfigureAwait(false);
        if (_owned is not null)
        {
            await _owned.DisposeAsync().ConfigureAwait(false); // The voice engine process, after the last job has stopped.
        }
    }

    // ---- voices --------------------------------------------------------------------------------

    private Task ListVoicesAsync(ApiContext ctx) =>
        ctx.WriteJsonAsync(200, new VoiceList
        {
            Voices = Library.List().ToList(),
            Builtin = _engine.Availability.BuiltInVoices.ToList(),
        }, AiHostJsonContext.Default.VoiceList);

    private async Task CreateVoiceAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.VoiceCreateRequest);

        // Consent comes first: nothing about the sample is looked at without it.
        if (!VoiceConsent.IsValid(body.Consent))
        {
            throw new ApiException(ErrorCodes.ConsentRequired,
                "consent must be \"own_voice\" or \"permission\": say whose voice this is.", "consent");
        }

        VoiceLibrary.NormalizeName(body.Name);
        byte[] sample;
        try
        {
            sample = Convert.FromBase64String(body.SampleBase64 ?? "");
        }
        catch (FormatException)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "sample_base64 is not valid base64.", "sample_base64");
        }

        if (sample.Length == 0)
        {
            throw new ApiException(ErrorCodes.InvalidRequest, "sample_base64 is required.", "sample_base64");
        }

        byte[] wav;
        if (WavAudio.LooksLikeWav(sample))
        {
            wav = sample;
        }
        else if (WavAudio.LooksLikeMp3(sample))
        {
            if (_decoder is null || !_decoder.Formats.Contains(VoiceSampleFormats.Mp3))
            {
                throw new ApiException(ErrorCodes.UnsupportedAudio,
                    "This host cannot read MP3 files yet. Send a WAV file.", "sample_base64");
            }

            try
            {
                wav = await _decoder.DecodeToWavAsync(sample, VoiceSampleFormats.Mp3, ctx.Aborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or AudioFormatException)
            {
                throw new ApiException(ErrorCodes.UnsupportedAudio, "The MP3 file could not be read.", "sample_base64");
            }
        }
        else
        {
            throw new ApiException(ErrorCodes.UnsupportedAudio, "The sample must be an MP3 or WAV file.", "sample_base64");
        }

        var info = Library.Create(body.Name, body.Consent!, ctx.Principal!.AppId, wav);
        await ctx.WriteJsonAsync(201, info, AiHostJsonContext.Default.VoiceInfo).ConfigureAwait(false);
    }

    private async Task RenameVoiceAsync(ApiContext ctx)
    {
        var id = ctx.RouteValues["id"];
        var body = ctx.ReadJson(AiHostJsonContext.Default.VoiceRenameRequest);
        RequireVoiceOwner(ctx, id);
        var info = Library.Rename(id, body.Name) ?? throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.");
        await ctx.WriteJsonAsync(200, info, AiHostJsonContext.Default.VoiceInfo).ConfigureAwait(false);
    }

    private async Task DeleteVoiceAsync(ApiContext ctx)
    {
        var id = ctx.RouteValues["id"];
        RequireVoiceOwner(ctx, id);
        if (Jobs.IsVoiceInUse(id))
        {
            throw new ApiException(ErrorCodes.VoiceInUse, "A speech job is using this voice. Cancel it or wait for it to finish.");
        }

        if (!Library.Delete(id))
        {
            throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.");
        }

        await ctx.WriteEmptyAsync(204).ConfigureAwait(false);
    }

    private void RequireVoiceOwner(ApiContext ctx, string id)
    {
        var voice = Library.Get(id) ?? throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.");
        var p = ctx.Principal!;
        if (p.Transport != Transport.Loopback && !string.Equals(voice.CreatedBy, p.AppId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.AccessDenied, "Only the app that made this voice, or an app on this PC, can change it.");
        }
    }

    // ---- jobs ----------------------------------------------------------------------------------

    private async Task CreateJobAsync(ApiContext ctx)
    {
        var body = ctx.ReadJson(AiHostJsonContext.Default.SpeechJobRequest);
        var info = Jobs.Create(ctx.Principal!.AppId, body);
        await ctx.WriteJsonAsync(202, ForCaller(ctx, info), AiHostJsonContext.Default.SpeechJobInfo).ConfigureAwait(false);
    }

    private async Task GetJobAsync(ApiContext ctx)
    {
        var info = Jobs.Get(ctx.Principal!.AppId, ctx.RouteValues["id"], IsLocal(ctx))
            ?? throw new ApiException(ErrorCodes.NotFound, "Unknown speech job.");
        await ctx.WriteJsonAsync(200, info, AiHostJsonContext.Default.SpeechJobInfo).ConfigureAwait(false);
    }

    private async Task CancelJobAsync(ApiContext ctx)
    {
        var info = Jobs.Cancel(ctx.Principal!.AppId, ctx.RouteValues["id"], IsLocal(ctx))
            ?? throw new ApiException(ErrorCodes.NotFound, "Unknown speech job.");
        await ctx.WriteJsonAsync(200, info, AiHostJsonContext.Default.SpeechJobInfo).ConfigureAwait(false);
    }

    private async Task GetJobAudioAsync(ApiContext ctx)
    {
        var id = ctx.RouteValues["id"];
        var bytes = Jobs.ReadResult(ctx.Principal!.AppId, id);
        if (bytes is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "No finished audio for this speech job.");
        }

        await ctx.WriteBytesAsync(200, "audio/wav", bytes).ConfigureAwait(false);
    }

    private static bool IsLocal(ApiContext ctx) => ctx.Principal!.Transport == Transport.Loopback;

    private static SpeechJobInfo ForCaller(ApiContext ctx, SpeechJobInfo info)
    {
        if (!IsLocal(ctx) && info.Result is not null)
        {
            info.Result.Path = null; // A path on this PC means nothing to a paired device.
        }

        return info;
    }
}
