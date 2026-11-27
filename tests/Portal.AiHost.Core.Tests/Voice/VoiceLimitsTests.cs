using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Tests.Support;
using Portal.AiHost.Core.Voice;
using Portal.AiHost.FakeEngine;

namespace Portal.AiHost.Core.Tests.Voice;

public sealed class VoiceLimitsTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(4, 64, "low")]
    [InlineData(16, 7, "low")]
    [InlineData(8, 16, "mid")]
    [InlineData(12, 16, "mid")]
    [InlineData(8, 64, "mid")]
    [InlineData(12, 32, "high")]
    [InlineData(24, 128, "high")]
    public void TierFollowsCoresAndMemory(int cores, long ramGb, string tier)
    {
        var limits = VoiceLimits.ForHardware(cores, ramGb * Gb);
        Assert.Equal(tier, limits.Tier);
        Assert.Equal(30, limits.MaxSampleSeconds); // The model's need, the same on every PC.
    }

    [Fact]
    public void TierValuesAndUnknownHardware()
    {
        Assert.Equal((15_000, 4), (VoiceLimits.ForTier("low").MaxTextChars, VoiceLimits.ForTier("low").MaxJobsPerApp));
        Assert.Equal((20_000, 8), (VoiceLimits.ForTier("mid").MaxTextChars, VoiceLimits.ForTier("mid").MaxJobsPerApp));
        Assert.Equal((40_000, 12), (VoiceLimits.ForTier("high").MaxTextChars, VoiceLimits.ForTier("high").MaxJobsPerApp));
        Assert.Equal("mid", VoiceLimits.ForHardware((HardwareProfile?)null).Tier);
    }

    [Fact]
    public void MeasuredMedianReplacesTheHardwareGuessAndKeepsTheLastTwenty()
    {
        var est = new VoiceEstimator(VoiceLimits.ForTier("mid"));
        Assert.Equal((12.0, "hardware"), est.For(false));
        est.Record(false, 1000, 4);
        est.Record(false, 1000, 6);
        Assert.Equal("hardware", est.For(false).Source); // Too few to trust yet.
        est.Record(false, 1000, 100);
        Assert.Equal((6.0, "measured"), est.For(false)); // Median, not mean.
        Assert.Equal((25.0, "hardware"), est.For(true)); // Cloned is tracked on its own.

        est.Record(false, 10, 5); // Too short to count.
        for (var i = 0; i < 20; i++)
        {
            est.Record(false, 1000, 2);
        }

        Assert.Equal(2.0, est.For(false).SecondsPer1000);
    }

    [Fact]
    public void EstimatesSurviveARestartAndADamagedFileIsIgnored()
    {
        using var dir = new TempDir();
        var path = dir.File("state/voice-estimates.json");
        var first = new VoiceEstimator(VoiceLimits.ForTier("low"), path);
        for (var i = 0; i < 3; i++)
        {
            first.Record(true, 2000, 10);
        }

        var second = new VoiceEstimator(VoiceLimits.ForTier("low"), path);
        Assert.Equal((5.0, "measured"), second.For(true));
        Assert.Equal("hardware", second.For(false).Source);
        Assert.Empty(Directory.EnumerateFiles(dir.File("state"), "*" + Core.Settings.AtomicFile.TempSuffix));

        File.WriteAllText(path, "{ not json ]");
        var damaged = new VoiceEstimator(VoiceLimits.ForTier("low"), path);
        Assert.Equal((60.0, "hardware"), damaged.For(true));
        damaged.Record(true, 1000, 7); // Writes a fresh, readable file over the damaged one.
        Assert.Single(System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("cloned").EnumerateArray());
    }
}

public sealed class VoiceLimitsEnforcementTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly FakeSpeechEngine _engine = new();
    private VoiceService _voice = null!;

    public ValueTask InitializeAsync()
    {
        _voice = new VoiceService(new VoiceServiceOptions
        {
            VoicesDirectory = _dir.File("voices"),
            OutputDirectory = _dir.File("out"),
            Limits = VoiceLimits.ForTier("low"),
            EstimatesPath = _dir.File("voice-estimates.json"),
        }, _engine);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _voice.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public void CapabilitiesCarryTheTierLimitsAndEstimates()
    {
        var caps = _voice.Capabilities();
        Assert.Equal(30, caps.MaxSampleSeconds);
        Assert.Equal(15_000, caps.MaxTextChars);
        Assert.Equal(4, caps.MaxJobsPerApp);
        Assert.Equal(30, caps.SecondsPer1000Chars);
        Assert.Equal(60, caps.SecondsPer1000CharsCloned);
        Assert.Equal("hardware", caps.EstimateSource);
    }

    [Fact]
    public void TextSampleAndJobLimitsAreEnforced()
    {
        var big = new SpeechJobRequest { BuiltinVoice = "af_heart", Text = new string('a', 15_001) };
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<ApiException>(() => _voice.Jobs.Create("app", big)).Code);

        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<ApiException>(() =>
            _voice.Library.Create("Anna", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(31, 24000, 1))).Code);
        _voice.Library.Create("Anna", VoiceConsent.OwnVoice, "app", VoiceTestAudio.Tone(30, 24000, 1));

        _engine.Gate = new SemaphoreSlim(0);
        for (var i = 0; i < 4; i++)
        {
            _voice.Jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hello." });
        }

        Assert.Equal(ErrorCodes.QueueFull, Assert.Throws<ApiException>(() =>
            _voice.Jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = "Hello." })).Code);
        _engine.Gate.Release(10);
    }

    [Fact]
    public async Task JobShowsEstimatedSecondsWhileWaitingAndTheMeasuredRateAfterwards()
    {
        _engine.Gate = new SemaphoreSlim(0);
        var text = string.Join(' ', Enumerable.Repeat("This is a sentence.", 30)); // 569 characters
        var first = _voice.Jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = text });
        await _engine.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _voice.Jobs.Create("app", new SpeechJobRequest { BuiltinVoice = "af_heart", Text = text });

        var running = _voice.Jobs.Get("app", first.Id, true)!;
        var queued = _voice.Jobs.Get("app", second.Id, true)!;
        var one = (int)Math.Ceiling(text.Length / 1000.0 * 30);
        Assert.Equal(one, running.EstimatedSeconds);
        Assert.InRange(queued.EstimatedSeconds!.Value, 2 * one - 1, 2 * one + 1); // The job ahead counts too.

        _engine.Gate.Release(1000);
        for (var i = 0; i < 400 && _voice.Jobs.Get("app", second.Id, true)!.Status != SpeechJobStatuses.Completed; i++)
        {
            await Task.Delay(25);
        }

        var done = _voice.Jobs.Get("app", second.Id, true)!;
        Assert.Null(done.EstimatedSeconds);
        Assert.Equal("hardware", _voice.Capabilities().EstimateSource); // Two samples are not enough yet.
        Assert.True(File.Exists(_dir.File("voice-estimates.json")));
    }
}
