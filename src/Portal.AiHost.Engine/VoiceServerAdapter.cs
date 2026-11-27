using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Voice;

namespace Portal.AiHost.Engine;

/// <summary>Optional extras for the voice helper argument list. They may not change the bind address or the key.</summary>
public sealed record VoiceServerArgsOptions
{
    public IReadOnlyList<string> ExtraArgs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Adapter for the voice helper: a small loopback server that wraps the speech runtime (sherpa-onnx: Pocket TTS for
/// cloning, Kokoro for built-in voices). The argument list comes from a table, one row per flag, so a new flag is one row.
/// </summary>
public sealed class VoiceServerAdapter : IEngineAdapter
{
    private readonly VoiceServerArgsOptions _options;

    public VoiceServerAdapter(VoiceServerArgsOptions? options = null)
    {
        _options = options ?? new VoiceServerArgsOptions();
    }

    public string EngineKind => EngineKinds.VoiceServer;
    public string HealthPath => "/health";
    public string ChatPath => "/v1/speech"; // Unused: the voice helper has no chat.
    public string EmbeddingsPath => "/v1/speech";
    public string TokenizePath => "/v1/speech";
    public string ApplyTemplatePath => "/v1/speech";

    public IReadOnlyList<string> BuildArgs(EngineLaunchSpec spec, EngineEndpoint endpoint) =>
        VoiceServerArgs.Build(spec, endpoint, _options);
}

/// <summary>Builds and validates the voice helper command line.</summary>
public static class VoiceServerArgs
{
    /// <summary>One row per flag: its name and how to compute its value (null skips the flag).</summary>
    private static readonly (string Flag, Func<EngineLaunchSpec, EngineEndpoint, string?> Value)[] Table =
    [
        ("--host", (_, _) => EngineEndpoint.Host),
        ("--port", (_, e) => e.Port.ToString(CultureInfo.InvariantCulture)),
        ("--api-key", (_, e) => e.ApiKey),
        // The folder that holds the "cloning" and "fixed" model folders.
        ("--model-dir", (s, _) => s.ModelFiles.Count > 0 ? s.ModelFiles[0] : null),
        ("--threads", (s, _) => s.Threads is > 0 ? s.Threads.Value.ToString(CultureInfo.InvariantCulture) : null),
        ("--provider", (_, _) => "cpu"),
    ];

    public static IReadOnlyList<string> Build(EngineLaunchSpec spec, EngineEndpoint endpoint, VoiceServerArgsOptions? options = null)
    {
        options ??= new VoiceServerArgsOptions();
        if (spec.ModelFiles.Count == 0)
        {
            throw new ArgumentException("A model folder is required.", nameof(spec));
        }

        var args = new List<string>();
        foreach (var (flag, value) in Table)
        {
            if (value(spec, endpoint) is { } v)
            {
                args.Add(flag);
                args.Add(v);
            }
        }

        args.AddRange(options.ExtraArgs);
        LlamaServerArgs.AssertLoopbackOnly(args);
        return args;
    }
}

public sealed class VoiceEngineOptions
{
    /// <summary>The voice helper executable.</summary>
    public required string ExePath { get; init; }

    /// <summary>Folder with "cloning" and "fixed" model folders (each present only when installed).</summary>
    public required string ModelRoot { get; init; }

    public int? Threads { get; init; }

    public IReadOnlyList<BuiltInVoiceInfo> BuiltInVoices { get; init; } =
    [
        new BuiltInVoiceInfo { Id = "af_heart", Name = "Heart (US, female)", Language = "en-US" },
        new BuiltInVoiceInfo { Id = "am_michael", Name = "Michael (US, male)", Language = "en-US" },
        new BuiltInVoiceInfo { Id = "bf_emma", Name = "Emma (UK, female)", Language = "en-GB" },
        new BuiltInVoiceInfo { Id = "bm_george", Name = "George (UK, male)", Language = "en-GB" },
    ];

    public const string CloningFolder = "cloning";
    public const string FixedFolder = "fixed";
}

/// <summary>
/// The real <see cref="ISpeechEngine"/>: starts the supervised voice helper when the first chunk arrives and sends it one
/// text chunk at a time. The supervisor restarts it after a crash and unloads it when idle; the next chunk starts it again.
/// </summary>
public sealed class VoiceEngineClient : ISpeechEngine
{
    private readonly EngineSupervisor _supervisor;
    private readonly VoiceEngineOptions _options;
    private readonly SemaphoreSlim _start = new(1, 1);

    public VoiceEngineClient(EngineSupervisor supervisor, VoiceEngineOptions options)
    {
        _supervisor = supervisor;
        _options = options;
    }

    public VoiceAvailability Availability
    {
        get
        {
            if (!File.Exists(_options.ExePath))
            {
                return VoiceAvailability.None;
            }

            var cloning = Directory.Exists(Path.Combine(_options.ModelRoot, VoiceEngineOptions.CloningFolder));
            var fixedVoices = Directory.Exists(Path.Combine(_options.ModelRoot, VoiceEngineOptions.FixedFolder));
            return cloning || fixedVoices
                ? new VoiceAvailability(cloning, fixedVoices, fixedVoices ? _options.BuiltInVoices : [])
                : VoiceAvailability.None;
        }
    }

    public async Task<PcmAudio> SynthesizeAsync(SpeechChunkRequest request, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var content = new ByteArrayContent(BuildBody(request));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var result = await _supervisor.ForwardAsync(HttpMethod.Post, "/v1/speech", content, ct).ConfigureAwait(false);
        if (!result.Response.IsSuccessStatusCode)
        {
            throw new EngineException(ErrorCodes.EngineError, "The voice engine refused the text.");
        }

        var bytes = await result.Response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            return WavAudio.ReadMono(bytes);
        }
        catch (AudioFormatException)
        {
            throw new EngineException(ErrorCodes.EngineError, "The voice engine sent audio the host could not read.");
        }
    }

    internal static byte[] BuildBody(SpeechChunkRequest r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("text", r.Text);
            w.WriteNumber("speed", r.Speed);
            w.WriteString("format", "wav");
            w.WritePropertyName("voice");
            w.WriteStartObject();
            if (r.CloneSamplePath is not null)
            {
                w.WriteString("kind", "clone");
                w.WriteString("sample_path", r.CloneSamplePath); // Read-only for the helper; it never writes there.
            }
            else
            {
                w.WriteString("kind", "builtin");
                w.WriteString("id", r.BuiltInVoice);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_supervisor.State == EngineState.Ready)
        {
            return;
        }

        await _start.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_supervisor.State != EngineState.Ready)
            {
                await _supervisor.StartAsync(new EngineLaunchSpec
                {
                    Engine = EngineKinds.VoiceServer,
                    Kind = ModelKinds.Voice,
                    ModelId = "voice",
                    DisplayName = "Voice",
                    ModelFiles = [_options.ModelRoot],
                    Placement = new Placement { Backend = RuntimeProbe.Cpu },
                    Threads = _options.Threads,
                }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _start.Release();
        }
    }
}

/// <summary>
/// Reads MP3 samples through ffmpeg, over pipes only: bytes in on stdin, a 24 kHz mono WAV out on stdout. No file is
/// written or deleted.
/// </summary>
public sealed class FfmpegAudioDecoder : IAudioDecoder
{
    private readonly string _exePath;
    private readonly int _sampleRate;
    private readonly TimeSpan _timeout;

    public FfmpegAudioDecoder(string exePath, int sampleRate = 24000, TimeSpan? timeout = null)
    {
        _exePath = exePath;
        _sampleRate = sampleRate;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    public IReadOnlyList<string> Formats => File.Exists(_exePath) ? [VoiceSampleFormats.Mp3] : [];

    public static IReadOnlyList<string> BuildArgs(int sampleRate) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-i", "pipe:0",
        "-vn", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
        "-c:a", "pcm_s16le", "-f", "wav", "pipe:1",
    ];

    public async Task<byte[]> DecodeToWavAsync(byte[] input, string format, CancellationToken ct)
    {
        if (format != VoiceSampleFormats.Mp3 || !File.Exists(_exePath))
        {
            throw new InvalidOperationException("This format cannot be decoded here.");
        }

        var psi = new ProcessStartInfo(_exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArgs(_sampleRate))
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            var output = new MemoryStream();
            var copyOut = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // ffmpeg stopped reading (damaged file): its exit code says so below.
            }

            await Task.WhenAll(copyOut, drainErr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || output.Length == 0)
            {
                throw new InvalidOperationException("ffmpeg could not read the file.");
            }

            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true); // The process this method started.
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }
}
