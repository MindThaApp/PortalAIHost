using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Voice;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

public sealed class VoiceServerArgsTests
{
    private static readonly EngineEndpoint Ep = new(45999, "key999");

    private static EngineLaunchSpec Spec(int? threads = null) => new()
    {
        Engine = EngineKinds.VoiceServer,
        Kind = ModelKinds.Voice,
        ModelFiles = [@"C:\data\voice-models"],
        Threads = threads,
    };

    [Fact]
    public void ArgumentsComeFromTheTableAndBindLoopbackOnly()
    {
        var args = VoiceServerArgs.Build(Spec(6), Ep).ToList();
        Assert.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        Assert.Equal("45999", args[args.IndexOf("--port") + 1]);
        Assert.Equal("key999", args[args.IndexOf("--api-key") + 1]);
        Assert.Equal(@"C:\data\voice-models", args[args.IndexOf("--model-dir") + 1]);
        Assert.Equal("6", args[args.IndexOf("--threads") + 1]);
        Assert.Equal("cpu", args[args.IndexOf("--provider") + 1]);
        Assert.Equal(1, args.Count(a => a == "--host"));
    }

    [Fact]
    public void ThreadsAreOnlyPassedWhenSet() =>
        Assert.DoesNotContain("--threads", VoiceServerArgs.Build(Spec(), Ep));

    [Fact]
    public void ADifferentBindAddressIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => VoiceServerArgs.Build(Spec(), Ep,
            new VoiceServerArgsOptions { ExtraArgs = ["--host", "0.0.0.0"] }));

    [Fact]
    public void AModelFolderIsRequired() =>
        Assert.Throws<ArgumentException>(() => VoiceServerArgs.Build(new EngineLaunchSpec(), Ep));

    [Fact]
    public void AdapterReportsTheVoiceKind() =>
        Assert.Equal(EngineKinds.VoiceServer, new VoiceServerAdapter().EngineKind);

    [Fact]
    public void FfmpegReadsFromAndWritesToPipesOnly()
    {
        var args = FfmpegAudioDecoder.BuildArgs(24000);
        Assert.Equal("pipe:0", args[args.ToList().IndexOf("-i") + 1]);
        Assert.Equal("pipe:1", args[^1]);
        Assert.Equal("24000", args[args.ToList().IndexOf("-ar") + 1]);
    }
}

public sealed class VoiceEngineClientTests : IDisposable
{
    private readonly string _models = Path.Combine(Path.GetTempPath(), "pah-voice-engine-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Only the empty model folders this test created (non-recursive, deepest first).
        foreach (var sub in new[] { VoiceEngineOptions.CloningFolder, VoiceEngineOptions.FixedFolder })
        {
            var p = Path.Combine(_models, sub);
            if (Directory.Exists(p))
            {
                Directory.Delete(p, recursive: false);
            }
        }

        if (Directory.Exists(_models))
        {
            Directory.Delete(_models, recursive: false);
        }
    }

    private VoiceEngineOptions Options(bool cloning, bool fixedVoices, string? exe = null)
    {
        Directory.CreateDirectory(_models);
        if (cloning)
        {
            Directory.CreateDirectory(Path.Combine(_models, VoiceEngineOptions.CloningFolder));
        }

        if (fixedVoices)
        {
            Directory.CreateDirectory(Path.Combine(_models, VoiceEngineOptions.FixedFolder));
        }

        return new VoiceEngineOptions { ExePath = exe ?? Stub.Exe, ModelRoot = _models };
    }

    private static EngineSupervisor Supervisor() => new(ModelKinds.Voice, new VoiceServerAdapter(), Stub.Options());

    [Fact]
    public void AvailabilityFollowsTheFilesOnDisk()
    {
        var none = new VoiceEngineClient(Supervisor(), Options(false, false)).Availability;
        Assert.False(none.Speech);

        var cloneOnly = new VoiceEngineClient(Supervisor(), Options(true, false)).Availability;
        Assert.True(cloneOnly.Cloning);
        Assert.False(cloneOnly.BuiltIn);
        Assert.Empty(cloneOnly.BuiltInVoices);

        var both = new VoiceEngineClient(Supervisor(), Options(true, true)).Availability;
        Assert.True(both.Cloning && both.BuiltIn);
        Assert.NotEmpty(both.BuiltInVoices);

        var missingExe = new VoiceEngineClient(Supervisor(), Options(true, true, Path.Combine(_models, "nope.exe"))).Availability;
        Assert.False(missingExe.Speech);
    }

    [Fact]
    public async Task ReadsAChunkThroughTheSupervisedProcess()
    {
        await using var sup = Supervisor();
        var client = new VoiceEngineClient(sup, Options(true, true));

        var audio = await client.SynthesizeAsync(new SpeechChunkRequest("Hello.", 1.0, "af_heart", null), CancellationToken.None);

        Assert.Equal(24000, audio.SampleRate);
        Assert.Equal(2400, audio.Samples.Length);
        Assert.Equal(EngineState.Ready, sup.State);
        var clone = await client.SynthesizeAsync(new SpeechChunkRequest("Again.", 1.0, null, @"C:\voices\vc_x\sample.wav"), CancellationToken.None);
        Assert.Equal(2400, clone.Samples.Length);
    }

    [Fact]
    public async Task AnEngineErrorBecomesAnEngineException()
    {
        await using var sup = Supervisor();
        var client = new VoiceEngineClient(sup, Options(true, true));
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            client.SynthesizeAsync(new SpeechChunkRequest("FAIL please", 1.0, "af_heart", null), CancellationToken.None));
        Assert.Equal(ErrorCodes.EngineError, ex.ErrorCode);
    }

    [Fact]
    public void RequestBodyNamesTheVoiceKind()
    {
        var clone = System.Text.Encoding.UTF8.GetString(VoiceEngineClient.BuildBody(new SpeechChunkRequest("Hi \"you\"", 1.5, null, @"C:\v\sample.wav")));
        Assert.Contains("\"kind\":\"clone\"", clone);
        Assert.Contains("\"sample_path\":\"C:\\\\v\\\\sample.wav\"", clone);
        var builtin = System.Text.Encoding.UTF8.GetString(VoiceEngineClient.BuildBody(new SpeechChunkRequest("Hi", 1, "af_heart", null)));
        Assert.Contains("\"kind\":\"builtin\"", builtin);
        Assert.Contains("\"id\":\"af_heart\"", builtin);
    }
}
