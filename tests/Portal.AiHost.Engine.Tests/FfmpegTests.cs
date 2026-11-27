using System.Diagnostics;
using System.Globalization;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

public sealed class FfmpegArgsTests
{
    private const string In = @"C:\data\generated\videos\a.avi";
    private const string Out = @"C:\data\generated\.work\gen_0123456789abcdef\output.mp4";

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void ConvertUsesTheWindowsEncoderAndNeverOverwrites()
    {
        var a = FfmpegArgs.ConvertToMp4(In, Out);
        Assert.Equal("h264_mf", After(a, "-c:v"));
        Assert.DoesNotContain("libx264", a);
        Assert.Equal("yuv420p", After(a, "-pix_fmt"));
        Assert.Equal("+faststart", After(a, "-movflags"));
        Assert.Contains("-n", a);
        Assert.Equal("file,pipe", After(a, "-protocol_whitelist"));
        Assert.Equal(In, After(a, "-i"));
        Assert.Equal(Out, a[^1]);
    }

    [Theory]
    [InlineData("relative.avi")]
    [InlineData("http://example.com/a.avi")]
    [InlineData("pipe:0")]
    public void OnlyFullLocalPathsAreAccepted(string path) =>
        Assert.Throws<ArgumentException>(() => FfmpegArgs.ConvertToMp4(path, Out));

    [Fact]
    public void LastFrameReadsTheTailOfTheFileIntoAPipe()
    {
        var a = FfmpegArgs.LastFrame(In);
        Assert.Equal("-1", After(a, "-sseof"));
        Assert.Equal("pipe:1", a[^1]);
        Assert.Equal("image2pipe", After(a, "-f"));
    }

    private static MediaJoinPlan Plan(int clips, MediaTransition[] t, MediaAudioTrack[]? audio = null, bool duck = false) =>
        new([.. Enumerable.Range(0, clips).Select(i => $@"C:\v\clip{i}.mp4")], t, audio ?? [], duck);

    private static readonly FfmpegMediaInfo First = new(321, 241, "16/1", 2);

    [Fact]
    public void JoinChainsCutAndCrossfadeAndSumsTheLength()
    {
        var plan = Plan(3, [new(MediaTransitions.Crossfade, 0.5), new(MediaTransitions.Cut, 0)]);
        var a = FfmpegArgs.Join(plan, First, [2, 2, 3], @"C:\w\output.mp4", out var total);
        var graph = After(a, "-filter_complex");
        Assert.Equal(2 + 2 - 0.5 + 3, total, 3);
        Assert.Contains("xfade=transition=fade:duration=0.5:offset=1.5[x1]", graph);
        Assert.Contains("[x1][v2]concat=n=2:v=1:a=0[vout]", graph);
        Assert.Contains("scale=320:240", graph); // even sides
        Assert.Contains("fps=16/1", graph);
        Assert.DoesNotContain("[aout]", graph);
        Assert.Equal("[vout]", After(a, "-map"));
        Assert.Equal("h264_mf", After(a, "-c:v"));
    }

    [Fact]
    public void ACrossfadeIsClampedToHalfOfTheShorterClipAndTinyOnesBecomeCuts()
    {
        var a = FfmpegArgs.Join(Plan(2, [new(MediaTransitions.Crossfade, 5)]), First, [2, 1], @"C:\w\o.mp4", out var total);
        Assert.Contains("duration=0.5:offset=1.5", After(a, "-filter_complex"));
        Assert.Equal(2.5, total, 3);
        var cut = FfmpegArgs.Join(Plan(2, [new(MediaTransitions.Crossfade, 0.01)]), First, [2, 1], @"C:\w\o.mp4", out var t2);
        Assert.Contains("concat=n=2", After(cut, "-filter_complex"));
        Assert.Equal(3, t2, 3);
    }

    [Fact]
    public void ASingleClipJoinStillWorks()
    {
        var a = FfmpegArgs.Join(Plan(1, []), First, [2], @"C:\w\o.mp4", out var total);
        Assert.Contains("[v0]null[vout]", After(a, "-filter_complex"));
        Assert.Equal(2, total, 3);
    }

    [Fact]
    public void JoinNeedsOneTransitionBetweenEachTwoClips() =>
        Assert.Throws<ArgumentException>(() => FfmpegArgs.Join(Plan(3, [new(MediaTransitions.Cut, 0)]), First, [1, 1, 1], @"C:\w\o.mp4", out _));

    [Fact]
    public void JoinMixesSoundtracksAndFadesThemOut()
    {
        var audio = new MediaAudioTrack[] { new(@"C:\w\m.wav", MediaAudioRoles.Music, 0.8, 0), new(@"C:\w\v.wav", MediaAudioRoles.Voice, 1, 0.5) };
        var a = FfmpegArgs.Join(Plan(2, [new(MediaTransitions.Cut, 0)], audio), First, [2, 2], @"C:\w\o.mp4", out _);
        var graph = After(a, "-filter_complex");
        Assert.Contains("[2:a]", graph);
        Assert.Contains("[3:a]", graph);
        Assert.Contains("adelay=500:all=1", graph);
        Assert.Contains("amix=inputs=2:duration=longest:normalize=0[amix]", graph);
        Assert.DoesNotContain("sidechaincompress", graph);
        Assert.Contains("afade=t=out:st=3.5:d=0.5[aout]", graph);
        Assert.Equal("aac", After(a, "-c:a"));
        Assert.Contains("[aout]", a);
    }

    [Fact]
    public void DuckingPutsTheMusicUnderTheVoice()
    {
        var audio = new MediaAudioTrack[] { new(@"C:\w\m.wav", MediaAudioRoles.Music, 1, 0), new(@"C:\w\v.wav", MediaAudioRoles.Voice, 1, 0) };
        var a = FfmpegArgs.Join(Plan(1, [], audio, duck: true), First, [4], @"C:\w\o.mp4", out _);
        var graph = After(a, "-filter_complex");
        Assert.Contains("asplit=2[vo][vsc]", graph);
        Assert.Contains("sidechaincompress", graph);
    }

    [Fact]
    public void ProbeOutputIsParsed()
    {
        var info = FfmpegArgs.ParseProbe("width=832\nheight=480\nr_frame_rate=16/1\nduration=2.062500\n");
        Assert.Equal(new FfmpegMediaInfo(832, 480, "16/1", 2.0625), info);
        Assert.Null(FfmpegArgs.ParseProbe("width=832\nheight=480\n"));
        Assert.Null(FfmpegArgs.ParseProbe("width=832\nheight=480\nduration=N/A\n"));
        Assert.Equal("16/1", FfmpegArgs.ParseProbe("width=64\nheight=64\nr_frame_rate=0/0\nduration=1\n")!.FrameRate);
    }

    [Fact]
    public void DecodeArgsUsePipesOnly()
    {
        var a = FfmpegArgs.DecodeToWav(24000);
        Assert.Equal("pipe:0", After(a, "-i"));
        Assert.Equal("pipe:1", a[^1]);
        Assert.Equal("24000", After(a, "-ar"));
    }
}

public sealed class FfmpegProgressTests
{
    [Theory]
    [InlineData("frame=   32 fps=0.0 q=-0.0 Lsize=      24KiB time=00:00:02.00 bitrate=  99.1kbits/s speed=33.4x", 2.0)]
    [InlineData("frame=1 time=01:02:03.50 bitrate=N/A", 3723.5)]
    [InlineData("size=N/A time=00:00:00.04 bitrate=N/A", 0.04)]
    public void TimeIsParsed(string line, double seconds)
    {
        Assert.True(FfmpegProgress.TryParseSeconds(line, out var s));
        Assert.Equal(seconds, s, 3);
    }

    [Theory]
    [InlineData("time=N/A bitrate=N/A")]
    [InlineData("Stream #0:0: Video: mjpeg")]
    [InlineData("")]
    public void OtherLinesAreIgnored(string line) => Assert.False(FfmpegProgress.TryParseSeconds(line, out _));

    [Fact]
    public void FractionStaysBelowOneUntilTheProcessEnds()
    {
        Assert.Equal(0.5, FfmpegProgress.Fraction(1, 2), 3);
        Assert.Equal(0.99, FfmpegProgress.Fraction(5, 2), 3);
        Assert.Equal(0, FfmpegProgress.Fraction(1, 0));
    }
}

public sealed class LastPngSinkTests
{
    private static byte[] Png(byte filler, int dataBytes)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            ms.Write(len);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write([0, 0, 0, 0]); // checksum is not checked here
        }

        Chunk("IHDR", new byte[13]);
        Chunk("IDAT", Enumerable.Repeat(filler, dataBytes).ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }

    [Fact]
    public void KeepsOnlyTheLastPictureEvenWhenWrittenInTinyPieces()
    {
        var a = Png(1, 50);
        var b = Png(2, 70);
        var c = Png(3, 30);
        var all = a.Concat(b).Concat(c).ToArray();
        var sink = new LastPngSink();
        for (var i = 0; i < all.Length; i += 7)
        {
            sink.Write(all, i, Math.Min(7, all.Length - i));
        }

        Assert.Equal(c, sink.Last);
    }

    [Fact]
    public void AnIncompleteLastPictureDoesNotReplaceTheCompleteOne()
    {
        var a = Png(1, 50);
        var b = Png(2, 70);
        var sink = new LastPngSink();
        sink.Write(a);
        sink.Write(b.AsSpan(0, b.Length - 5));
        Assert.Equal(a, sink.Last);
    }

    [Fact]
    public void NoPngDataMeansNoPicture()
    {
        var sink = new LastPngSink();
        Assert.Null(sink.Last);
        Assert.Throws<InvalidDataException>(() => sink.Write(new byte[16]));
    }
}

public sealed class FfmpegRunnerBasicTests
{
    [Fact]
    public async Task MissingFfmpegReportsUnavailableAndThrowsEngineMissing()
    {
        var runner = new FfmpegRunner(new FfmpegRunnerOptions { ExePath = Path.Combine(Path.GetTempPath(), "nope", "ffmpeg.exe") });
        Assert.False(runner.Available);
        var e = await Assert.ThrowsAsync<EngineException>(() =>
            runner.ConvertToMp4Async(@"C:\a.avi", @"C:\b.mp4", null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.EngineMissing, e.ErrorCode);
    }
}

/// <summary>
/// Real ffmpeg. Skips unless PORTAL_FFMPEG_DIR names a folder with ffmpeg.exe and ffprobe.exe (for example the
/// package's engine\ffmpeg after scripts\fetch-ffmpeg.ps1). Everything is written under %TEMP% and removed after.
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "portalaihost-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _ffmpeg;

    public FfmpegIntegrationTests()
    {
        var env = Environment.GetEnvironmentVariable("PORTAL_FFMPEG_DIR");
        if (env is { Length: > 0 } && File.Exists(Path.Combine(env, "ffmpeg.exe")) && File.Exists(Path.Combine(env, "ffprobe.exe")))
        {
            _ffmpeg = Path.Combine(env, "ffmpeg.exe");
        }

        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true); // The folder this test class made under %TEMP%.
        }
        catch (IOException)
        {
        }
    }

    private FfmpegRunner Runner(Action<FfmpegRunnerOptions>? _ = null) => new(new FfmpegRunnerOptions { ExePath = _ffmpeg! });

    private async Task<string> Make(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(_ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n" }.Concat(args).Append(path))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }

    [Fact]
    public async Task ConvertsAnAviToMp4AndExtractsTheLastFrame()
    {
        if (_ffmpeg is null)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var avi = await Make("a.avi", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=16:duration=2", "-c:v", "mjpeg", "-q:v", "3");
        var runner = Runner();
        Assert.True(runner.Available);
        var seen = new List<double>();
        var mp4 = Path.Combine(_dir, "a.mp4");
        await runner.ConvertToMp4Async(avi, mp4, new Progress<double>(seen.Add), ct);
        Assert.True(new FileInfo(mp4).Length > 1000);
        var info = await runner.ProbeAsync(mp4, ct);
        Assert.NotNull(info);
        Assert.Equal(320, info.Width);
        Assert.InRange(info.DurationSeconds, 1.9, 2.2);
        Assert.Equal(1, seen[^1]);

        // Never overwrites.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ConvertToMp4Async(avi, mp4, null, ct));

        var png = await runner.ExtractLastFramePngAsync(mp4, ct);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.True(png.Length > 500);
    }

    [Fact]
    public async Task JoinsClipsWithACrossfadeAndASoundtrack()
    {
        if (_ffmpeg is null)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var a = await Make("a.avi", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=16:duration=2", "-c:v", "mjpeg", "-q:v", "3");
        var b = await Make("b.avi", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=16:duration=2", "-c:v", "mjpeg", "-q:v", "3");
        var music = await Make("m.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-c:a", "pcm_s16le");
        var voice = await Make("v.wav", "-f", "lavfi", "-i", "sine=frequency=880:duration=2", "-c:a", "pcm_s16le");
        var runner = Runner();
        var output = Path.Combine(_dir, "joined.mp4");
        var plan = new MediaJoinPlan([a, b], [new MediaTransition(MediaTransitions.Crossfade, 0.5)],
            [new MediaAudioTrack(music, MediaAudioRoles.Music, 0.8, 0), new MediaAudioTrack(voice, MediaAudioRoles.Voice, 1, 0.5)], true);
        await runner.JoinAsync(plan, output, null, ct);
        var info = await runner.ProbeAsync(output, ct);
        Assert.NotNull(info);
        Assert.Equal(320, info.Width);
        Assert.InRange(info.DurationSeconds, 3.3, 3.8); // 2 + 2 - 0.5
    }

    [Fact]
    public async Task DecodesMp3OverPipes()
    {
        if (_ffmpeg is null)
        {
            return;
        }

        var mp3 = await Make("t.mp3", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "libmp3lame");
        var wav = await new FfmpegAudioDecoder(_ffmpeg).DecodeToWavAsync(await File.ReadAllBytesAsync(mp3, TestContext.Current.CancellationToken),
            VoiceSampleFormats.Mp3, TestContext.Current.CancellationToken);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.InRange(wav.Length, 24000 * 2 * 2 - 9000, 24000 * 2 * 2 + 9000);
    }

    [Fact]
    public async Task ATimeoutKillsTheProcessAndSaysSo()
    {
        if (_ffmpeg is null)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var avi = await Make("a.avi", "-f", "lavfi", "-i", "testsrc=size=640x480:rate=25:duration=20", "-c:v", "mjpeg", "-q:v", "2");
        var runner = new FfmpegRunner(new FfmpegRunnerOptions { ExePath = _ffmpeg, ConvertTimeout = TimeSpan.FromMilliseconds(30) });
        var e = await Assert.ThrowsAsync<EngineException>(() => runner.ConvertToMp4Async(avi, Path.Combine(_dir, "t.mp4"), null, ct));
        Assert.Equal(ErrorCodes.EngineError, e.ErrorCode);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner().ExtractLastFramePngAsync(avi, cancelled.Token));
    }
}

public sealed class FfmpegAudioDecoderTests
{
    [Fact]
    public void Mp3IsOfferedOnlyWhileFfmpegIsInstalled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "portalaihost-decoder-test-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(dir, "ffmpeg.exe");
        try
        {
            Assert.Empty(new FfmpegAudioDecoder(exe).Formats);
            Directory.CreateDirectory(dir);
            File.WriteAllText(exe, "stand-in");
            Assert.Equal([VoiceSampleFormats.Mp3], new FfmpegAudioDecoder(exe).Formats);
        }
        finally
        {
            if (File.Exists(exe))
            {
                File.Delete(exe); // The stand-in this test made.
            }

            if (Directory.Exists(dir))
            {
                Directory.Delete(dir); // Empty folder this test made.
            }
        }
    }

    [Fact]
    public async Task AMissingFfmpegRefusesToDecode() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegAudioDecoder(Path.Combine(Path.GetTempPath(), "nope", "ffmpeg.exe"))
            .DecodeToWavAsync([1, 2, 3], VoiceSampleFormats.Mp3, TestContext.Current.CancellationToken));
}
