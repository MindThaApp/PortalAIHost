using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Tests.Support;
using Portal.AiHost.Core.Voice;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Core.Tests.Voice;

internal static class VoiceTestAudio
{
    /// <summary>A WAV of a tone: any rate and channel count, 16-bit PCM.</summary>
    public static byte[] Tone(double seconds, int rate = 44100, int channels = 2)
    {
        var frames = (int)(seconds * rate);
        var mono = new short[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 300 * i / rate) * 9000);
            for (var c = 0; c < channels; c++)
            {
                mono[i * channels + c] = v;
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + mono.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(mono.Length * 2);
        foreach (var s in mono)
        {
            w.Write(s);
        }

        return ms.ToArray();
    }
}

public sealed class WavAudioTests
{
    [Fact]
    public void ConvertsStereo44kToMono24k()
    {
        var wav = VoiceTestAudio.Tone(2, 44100, 2);
        var pcm = WavAudio.ConvertToMono(wav, 24000);
        Assert.Equal(24000, pcm.SampleRate);
        Assert.InRange(pcm.Samples.Length, 47990, 48010);
        Assert.True(pcm.Samples.Max() > 5000);
    }

    [Fact]
    public void WrittenFileCarriesThePortalTagAndStaysReadable()
    {
        var bytes = WavAudio.WritePcm16(new PcmAudio(new short[2400], 24000));
        Assert.True(WavAudio.HasPortalTag(bytes));
        var info = WavAudio.Parse(bytes);
        Assert.Equal(2400 * 2, info.DataLength);
        Assert.Equal(24000, info.SampleRate);
        Assert.Equal(2400, WavAudio.ReadMono(bytes).Samples.Length);
    }

    [Fact]
    public void UntaggedFileHasNoTag() =>
        Assert.False(WavAudio.HasPortalTag(VoiceTestAudio.Tone(0.2, 24000, 1)));

    [Fact]
    public void GarbageIsRejected() =>
        Assert.Throws<AudioFormatException>(() => WavAudio.Parse("not audio at all......"u8));
}

public sealed class SpeechTextTests
{
    [Fact]
    public void SplitsAtSentencesAndNeverExceedsTheLimit()
    {
        var text = "One. Two! Three? " + new string('a', 50) + " " + new string('b', 50);
        var parts = SpeechText.Split(text, 40);
        Assert.All(parts, p => Assert.True(p.Length <= 40, p));
        Assert.Contains("Two!", string.Join(" ", parts));
    }

    [Fact]
    public void KeepsAllWords()
    {
        var text = string.Join(" ", Enumerable.Range(0, 200).Select(i => "w" + i + "."));
        var joined = string.Join(" ", SpeechText.Split(text, 100));
        Assert.Equal(text, joined);
    }
}

public sealed class VoiceLibraryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private VoiceLibrary Library() => new(_dir.File("voices"));

    [Fact]
    public void CreateStoresAConvertedCopyUnderAUniqueIdAndNeverTouchesTheOriginal()
    {
        var original = _dir.File("mine.wav");
        var bytes = VoiceTestAudio.Tone(12, 44100, 2);
        File.WriteAllBytes(original, bytes);
        var lib = Library();

        var a = lib.Create("Anna", VoiceConsent.OwnVoice, "app.one", bytes);
        var b = lib.Create("Anna", VoiceConsent.OwnVoice, "app.one", bytes);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Matches("^vc_[0-9a-f]{24}$", a.Id);
        var sample = lib.SamplePath(a.Id)!;
        Assert.StartsWith(lib.Root, sample, StringComparison.OrdinalIgnoreCase);
        var stored = WavAudio.Parse(File.ReadAllBytes(sample));
        Assert.Equal(24000, stored.SampleRate);
        Assert.Equal(1, stored.Channels);
        Assert.Equal(bytes, File.ReadAllBytes(original)); // The caller's file is exactly as it was.
        Assert.Equal(2, lib.List().Count);
        Assert.Equal(VoiceConsent.OwnVoice, lib.Get(a.Id)!.Consent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    public void ConsentIsRequired(string? consent)
    {
        var ex = Assert.Throws<ApiException>(() => Library().Create("Anna", consent!, "app", VoiceTestAudio.Tone(10)));
        Assert.Equal(ErrorCodes.ConsentRequired, ex.Code);
        Assert.False(Directory.Exists(_dir.File("voices")) && Directory.EnumerateDirectories(_dir.File("voices")).Any());
    }

    [Fact]
    public void TooShortOrTooLongIsRefused()
    {
        var lib = Library();
        Assert.Equal(ErrorCodes.InvalidRequest,
            Assert.Throws<ApiException>(() => lib.Create("a", VoiceConsent.Permission, "app", VoiceTestAudio.Tone(1))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest,
            Assert.Throws<ApiException>(() => lib.Create("a", VoiceConsent.Permission, "app", VoiceTestAudio.Tone(70, 16000, 1))).Code);
    }

    [Fact]
    public void DeleteRemovesOnlyThatVoicesFolder()
    {
        var lib = Library();
        var keep = lib.Create("Keep", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var gone = lib.Create("Gone", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var stranger = _dir.File(Path.Combine("voices", "my-own-folder"));
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "note.txt"), "mine");
        var outside = _dir.File("outside.txt");
        File.WriteAllText(outside, "outside");

        Assert.True(lib.Delete(gone.Id));

        Assert.False(Directory.Exists(Path.Combine(lib.Root, gone.Id)));
        Assert.True(File.Exists(Path.Combine(lib.Root, keep.Id, VoiceLibrary.SampleFileName)));
        Assert.True(File.Exists(Path.Combine(stranger, "note.txt")));
        Assert.True(File.Exists(outside));
        Assert.Single(lib.List());
        Assert.False(lib.Delete(gone.Id));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../voices")]
    [InlineData("vc_../../x")]
    [InlineData("my-own-folder")]
    [InlineData("vc_00000000000000000000000G")]
    public void DeleteRefusesAnythingThatIsNotAHostMadeId(string id)
    {
        var lib = Library();
        lib.Create("Keep", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var stranger = _dir.File(Path.Combine("voices", "my-own-folder"));
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "note.txt"), "mine");

        Assert.Throws<ApiException>(() => lib.Delete(id));

        Assert.True(File.Exists(Path.Combine(stranger, "note.txt")));
        Assert.Single(lib.List());
    }

    [Fact]
    public void DeleteLeavesAFolderThatHoldsUnknownFiles()
    {
        var lib = Library();
        var v = lib.Create("Odd", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var extra = Path.Combine(lib.Root, v.Id, "extra.bin");
        File.WriteAllText(extra, "not ours");

        Assert.ThrowsAny<IOException>(() => lib.Delete(v.Id));

        Assert.True(File.Exists(extra)); // Non-recursive: nothing unknown is removed.
    }

    [Fact]
    public void RenameKeepsTheSample()
    {
        var lib = Library();
        var v = lib.Create("Old", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var renamed = lib.Rename(v.Id, "  New name ")!;
        Assert.Equal("New name", renamed.Name);
        Assert.Equal("New name", lib.Get(v.Id)!.Name);
        Assert.NotNull(lib.SamplePath(v.Id));
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<ApiException>(() => lib.Rename(v.Id, "   ")).Code);
    }
}

public sealed class SpeechJobTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly FakeSpeechEngine _engine = new();
    private VoiceLibrary _library = null!;
    private SpeechJobManager _jobs = null!;

    public ValueTask InitializeAsync()
    {
        _library = new VoiceLibrary(_dir.File("voices"));
        _jobs = new SpeechJobManager(_library, _engine, new SpeechJobOptions { OutputDirectory = _dir.File("out"), MaxChunkChars = 40 });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _jobs.DisposeAsync();
        _dir.Dispose();
    }

    private async Task<SpeechJobInfo> WaitAsync(string app, string id, Func<SpeechJobInfo, bool> done)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = _jobs.Get(app, id, true)!;
            if (done(job))
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException();
    }

    [Fact]
    public async Task BuiltInVoiceJobCompletesWithATaggedWavInTheOutputFolder()
    {
        var created = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hello there. This is a test of the voice." });
        var job = await WaitAsync("app", created.Id, j => j.Status == SpeechJobStatuses.Completed);

        Assert.Equal(1, job.Progress);
        Assert.NotNull(job.Result);
        Assert.StartsWith(Path.GetFullPath(_dir.File("out")), job.Result!.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(created.Id, job.Result.FileName);
        var bytes = File.ReadAllBytes(job.Result.Path!);
        Assert.True(WavAudio.HasPortalTag(bytes));
        Assert.Equal(bytes.Length, job.Result.Bytes);
        Assert.True(_engine.Chunks.Count >= 2);
        Assert.Equal(bytes, _jobs.ReadResult("app", created.Id));
    }

    [Fact]
    public async Task CloneJobReadsTheLibrarySamplePath()
    {
        var v = _library.Create("Anna", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        var created = _jobs.Create("app", new SpeechJobRequest { VoiceId = v.Id, Text = "Hi.", Speed = 1.25 });
        await WaitAsync("app", created.Id, j => j.Status == SpeechJobStatuses.Completed);
        var chunk = _engine.Chunks.Single();
        Assert.Equal(_library.SamplePath(v.Id), chunk.CloneSamplePath);
        Assert.Equal(1.25, chunk.Speed);
        Assert.Null(chunk.BuiltInVoice);
    }

    [Fact]
    public async Task CancelStopsARunningJobAndDeleteOfItsVoiceIsHeldUntilThen()
    {
        var v = _library.Create("Anna", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        _engine.Gate = new SemaphoreSlim(0);
        var created = _jobs.Create("app", new SpeechJobRequest { VoiceId = v.Id, Text = "One. Two. Three." });
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_jobs.IsVoiceInUse(v.Id));

        _jobs.Cancel("app", created.Id, true);
        var job = await WaitAsync("app", created.Id, j => j.Status == SpeechJobStatuses.Cancelled);

        Assert.Null(job.Result);
        Assert.False(_jobs.IsVoiceInUse(v.Id));
        Assert.False(Directory.Exists(_dir.File("out")) && Directory.EnumerateFiles(_dir.File("out")).Any());
    }

    [Fact]
    public async Task QueuedJobCanBeCancelledBeforeItStarts()
    {
        _engine.Gate = new SemaphoreSlim(0);
        var first = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "First." });
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Second." });
        Assert.Equal(SpeechJobStatuses.Queued, _jobs.Get("app", second.Id, true)!.Status);
        Assert.Equal(1, _jobs.Get("app", second.Id, true)!.QueuePosition);

        Assert.Equal(SpeechJobStatuses.Cancelled, _jobs.Cancel("app", second.Id, true)!.Status);
        _engine.Gate.Release();
        await WaitAsync("app", first.Id, j => j.Status == SpeechJobStatuses.Completed);

        Assert.Single(_engine.Chunks); // The cancelled job never reached the engine.
    }

    [Fact]
    public async Task EngineFailureEndsTheJobAsFailedWithoutAFile()
    {
        var created = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Please FAIL now." });
        var job = await WaitAsync("app", created.Id, j => j.Status == SpeechJobStatuses.Failed);
        Assert.Equal(ErrorCodes.EngineError, job.Error!.Code);
        Assert.False(Directory.Exists(_dir.File("out")) && Directory.EnumerateFiles(_dir.File("out")).Any());
    }

    [Fact]
    public async Task ResultFilesAreNeverOverwritten()
    {
        // Two results never share a file name, and the first stays byte for byte as it was.
        var a = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "One." });
        var ja = await WaitAsync("app", a.Id, j => j.Status == SpeechJobStatuses.Completed);
        var before = File.ReadAllBytes(ja.Result!.Path!);
        var b = _jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "One." });
        var jb = await WaitAsync("app", b.Id, j => j.Status == SpeechJobStatuses.Completed);

        Assert.NotEqual(ja.Result.Path, jb.Result!.Path);
        Assert.Equal(before, File.ReadAllBytes(ja.Result.Path!));
        Assert.Equal(2, Directory.GetFiles(_dir.File("out")).Length);
    }

    [Fact]
    public async Task JobsBelongToTheirApp()
    {
        var created = _jobs.Create("app.one", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hi." });
        await WaitAsync("app.one", created.Id, j => j.Status == SpeechJobStatuses.Completed);
        Assert.Null(_jobs.Get("app.two", created.Id, true));
        Assert.Null(_jobs.Cancel("app.two", created.Id, true));
        Assert.Null(_jobs.ReadResult("app.two", created.Id));
    }

    [Theory]
    [InlineData("", "af_heart", null, ErrorCodes.InvalidRequest)]
    [InlineData("Hi.", null, null, ErrorCodes.InvalidRequest)]
    [InlineData("Hi.", "nope", null, ErrorCodes.InvalidRequest)]
    [InlineData("Hi.", "af_heart", 9.0, ErrorCodes.InvalidRequest)]
    public void BadRequestsAreRefused(string text, string? builtin, double? speed, string code)
    {
        var ex = Assert.Throws<ApiException>(() =>
            _jobs.Create("app", new SpeechJobRequest { Text = text, BuiltinVoice = builtin, Speed = speed }));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void UnknownVoiceAndMissingModelsAreNamed()
    {
        Assert.Equal(ErrorCodes.VoiceNotFound, Assert.Throws<ApiException>(() =>
            _jobs.Create("app", new SpeechJobRequest { VoiceId = "vc_" + new string('a', 24), Text = "Hi." })).Code);

        var v = _library.Create("Anna", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(10));
        _engine.Availability = new VoiceAvailability(false, true, _engine.Availability.BuiltInVoices);
        Assert.Equal(ErrorCodes.NoModel, Assert.Throws<ApiException>(() =>
            _jobs.Create("app", new SpeechJobRequest { VoiceId = v.Id, Text = "Hi." })).Code);
    }
}
