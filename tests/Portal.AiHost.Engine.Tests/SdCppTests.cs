using System.Collections.Concurrent;
using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

/// <summary>The sd.cpp command line per model family and placement (the flag table in SdCppArgs.cs).</summary>
public sealed class SdCppArgsTests
{
    private const string Out = @"C:\work\gen_00000000000000000000000000000000\output.png";

    private static GenerationLaunch Launch(string family, string kind = "image", GenerationPlacement? placement = null,
        params GenerationComponent[] files) => new()
    {
        Kind = kind,
        Family = family,
        Components = files,
        Prompt = "a lighthouse",
        Width = 512,
        Height = 512,
        Steps = 20,
        Seed = 42,
        CfgScale = 7,
        Sampler = "euler_a",
        OutputPath = kind == "video" ? Out.Replace(".png", ".avi", StringComparison.Ordinal) : Out,
        Placement = placement ?? new GenerationPlacement(),
    };

    private static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"{flag} missing");
        return args[i + 1];
    }

    [Fact]
    public void Sd15UsesTheWholeCheckpointAndTheRequestValues()
    {
        var args = SdCppArgs.Build(Launch(GenerationFamilies.Sd15, files: new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1)));
        Assert.Equal(["-M", "img_gen"], args.Take(2));
        Assert.Equal(@"C:\m\sd15.safetensors", ValueOf(args, "-m"));
        Assert.DoesNotContain("--diffusion-model", args);
        Assert.Equal("a lighthouse", ValueOf(args, "-p"));
        Assert.Equal("512", ValueOf(args, "-W"));
        Assert.Equal("512", ValueOf(args, "-H"));
        Assert.Equal("20", ValueOf(args, "--steps"));
        Assert.Equal("42", ValueOf(args, "-s"));
        Assert.Equal("7", ValueOf(args, "--cfg-scale"));
        Assert.Equal("euler_a", ValueOf(args, "--sampling-method"));
        Assert.Equal(Out, ValueOf(args, "-o"));
        Assert.DoesNotContain("--video-frames", args);
        Assert.DoesNotContain("--preview", args);
    }

    [Fact]
    public void SdxlTakesASeparateVae()
    {
        var args = SdCppArgs.Build(Launch(GenerationFamilies.Sdxl, files:
        [
            new(GenerationFileRoles.Model, @"C:\m\sdxl.safetensors", 1),
            new(GenerationFileRoles.Vae, @"C:\m\sdxl-vae.safetensors", 1),
        ]));
        Assert.Equal(@"C:\m\sdxl.safetensors", ValueOf(args, "-m"));
        Assert.Equal(@"C:\m\sdxl-vae.safetensors", ValueOf(args, "--vae"));
    }

    [Fact]
    public void FluxNamesEachPartAndPlacementFlagsFollowThePlan()
    {
        var placement = new GenerationPlacement { Backend = "vulkan", ClipOnCpu = true, VaeOnCpu = true, VaeTiling = true, OffloadToCpu = true };
        var args = SdCppArgs.Build(Launch(GenerationFamilies.Flux, placement: placement, files:
        [
            new(GenerationFileRoles.Diffusion, @"C:\m\flux1-schnell-q4_0.gguf", 1),
            new(GenerationFileRoles.ClipL, @"C:\m\clip_l.safetensors", 1),
            new(GenerationFileRoles.T5xxl, @"C:\m\t5xxl_q8.gguf", 1),
            new(GenerationFileRoles.Vae, @"C:\m\ae.safetensors", 1),
        ]));
        Assert.Equal(@"C:\m\flux1-schnell-q4_0.gguf", ValueOf(args, "--diffusion-model"));
        Assert.Equal(@"C:\m\clip_l.safetensors", ValueOf(args, "--clip_l"));
        Assert.Equal(@"C:\m\t5xxl_q8.gguf", ValueOf(args, "--t5xxl"));
        Assert.Equal(@"C:\m\ae.safetensors", ValueOf(args, "--vae"));
        Assert.DoesNotContain("-m", args);
        Assert.Contains("--offload-to-cpu", args);
        Assert.Contains("--vae-tiling", args);
        Assert.Equal("te=cpu,vae=cpu", ValueOf(args, "--backend"));
        Assert.Equal(1, args.Count(a => a == "--backend"));
        Assert.DoesNotContain("--clip-on-cpu", args); // deprecated in the pinned release
        Assert.DoesNotContain("--vae-on-cpu", args);

        Assert.Equal(2, args.Count(a => a == "--diffusion-model" || a == "--clip_l"));
    }

    [Fact]
    public void ProcessorRunsLeaveOutTheGraphicsCardFlags()
    {
        var placement = new GenerationPlacement { Backend = "cpu", ClipOnCpu = true, VaeOnCpu = true, OffloadToCpu = true, VaeTiling = true };
        var args = SdCppArgs.Build(Launch(GenerationFamilies.Sd15, placement: placement,
            files: new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1)) with { Threads = 8 });
        Assert.DoesNotContain("--offload-to-cpu", args);
        Assert.DoesNotContain("--max-vram", args);
        Assert.Equal("cpu", ValueOf(args, "--backend"));
        Assert.Contains("--vae-tiling", args);
        Assert.Equal("8", ValueOf(args, "-t"));
    }

    [Fact]
    public void WanVideoUsesVideoModeFramesAndTheStartPicture()
    {
        var launch = Launch(GenerationFamilies.Wan, "video", new GenerationPlacement { ClipOnCpu = true, FlashAttention = true, VaeTiling = true },
            new GenerationComponent(GenerationFileRoles.Diffusion, @"C:\m\wan2.1-t2v-1.3b-q8.gguf", 1),
            new GenerationComponent(GenerationFileRoles.T5xxl, @"C:\m\umt5-xxl-q8.gguf", 1),
            new GenerationComponent(GenerationFileRoles.Vae, @"C:\m\wan_2.1_vae.safetensors", 1),
            new GenerationComponent(GenerationFileRoles.ClipVision, @"C:\m\clip_vision_h.safetensors", 1)) with
        {
            Width = 832, Height = 480, Frames = 33, Fps = 16, FlowShift = 3, CfgScale = 6, Sampler = "euler",
            InitImagePath = @"C:\work\gen_00000000000000000000000000000000\init.png", Strength = 0.5,
        };
        var args = SdCppArgs.Build(launch);
        Assert.Equal(["-M", "vid_gen"], args.Take(2));
        Assert.Equal("33", ValueOf(args, "--video-frames"));
        Assert.Equal("16", ValueOf(args, "--fps"));
        Assert.Equal("3", ValueOf(args, "--flow-shift"));
        Assert.Equal(@"C:\work\gen_00000000000000000000000000000000\init.png", ValueOf(args, "-i"));
        Assert.Equal(@"C:\m\clip_vision_h.safetensors", ValueOf(args, "--clip_vision"));
        Assert.Equal("auto,diffusion=on", ValueOf(args, "--flash-attn"));
        Assert.DoesNotContain("--diffusion-fa", args);
        Assert.Equal("te=cpu", ValueOf(args, "--backend"));
        Assert.DoesNotContain("--strength", args); // image to image only
        Assert.EndsWith(".avi", ValueOf(args, "-o"), StringComparison.Ordinal);
        Assert.Equal(".avi", SdCppArgs.OutputExtension("video"));
    }

    [Fact]
    public void UnsupportedFamiliesAndUnsafeInputsAreRefused()
    {
        var sd = new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1);
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15, "video", files: sd)));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch("ltx-video", files: sd)));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15, files: sd) with { OutputPath = "output.png" }));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15)));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15,
            files: [sd, new GenerationComponent(GenerationFileRoles.Diffusion, @"C:\m\other.gguf", 1)])));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15, files: new GenerationComponent(GenerationFileRoles.Model, "rel.gguf", 1))));
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15, files: sd),
            new SdCppOptions { ExtraArgs = ["-o", @"C:\elsewhere.png"] }));
    }

    [Fact]
    public void PreviewFlagsAreSentOnlyWhenSwitchedOn()
    {
        var launch = Launch(GenerationFamilies.Sd15, files: new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1))
            with { PreviewPath = @"C:\work\gen_00000000000000000000000000000000\preview.png" };
        Assert.DoesNotContain("--preview", SdCppArgs.Build(launch));
        var args = SdCppArgs.Build(launch, new SdCppOptions { Preview = true });
        Assert.Equal(@"C:\work\gen_00000000000000000000000000000000\preview.png", ValueOf(args, "--preview-path"));
    }

    [Fact]
    public void EveryOptionHasExactlyOneFlagAndAllAreCheckedAgainstThePinnedRelease()
    {
        Assert.Equal(SdCppFlags.Table.Count, SdCppFlags.Table.Select(e => e.Option).Distinct().Count());
        Assert.Empty(SdCppFlags.Unverified);
        var gpu = SdCppArgs.Build(Launch(GenerationFamilies.Sd15, placement: new GenerationPlacement { MaxVramBytes = (long)(6.7 * (1L << 30)) },
            files: new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1)));
        Assert.Equal("6.7", ValueOf(gpu, "--max-vram"));
        Assert.DoesNotContain("--backend", gpu);
        Assert.Throws<ArgumentException>(() => SdCppArgs.Build(Launch(GenerationFamilies.Sd15,
            files: new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 1)), new SdCppOptions { ExtraArgs = ["--backend", "cuda0"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdCppFlags.Of("no-such-option"));
    }

    [Fact]
    public void FileRolesAreGuessedFromNamesWhenTheCatalogueHasNone()
    {
        Assert.Equal(GenerationFileRoles.Model, GenerationFileRoles.Guess("v1-5-pruned-emaonly.safetensors", 1));
        Assert.Equal(GenerationFileRoles.Diffusion, GenerationFileRoles.Guess("wan2.1_t2v_1.3B_fp16.safetensors", 3));
        Assert.Equal(GenerationFileRoles.T5xxl, GenerationFileRoles.Guess("umt5-xxl-encoder-Q8_0.gguf", 3));
        Assert.Equal(GenerationFileRoles.Vae, GenerationFileRoles.Guess("wan_2.1_vae.safetensors", 3));
        Assert.Equal(GenerationFileRoles.ClipL, GenerationFileRoles.Guess("clip_l.safetensors", 4));
        Assert.Equal(GenerationFileRoles.Vae, GenerationFileRoles.Guess("ae.safetensors", 4));
        Assert.Equal(GenerationFamilies.Sdxl, GenerationFamilies.Normalize("SDXL"));
        Assert.Equal(GenerationFamilies.Sd15, GenerationFamilies.Normalize("stable-diffusion-1.5"));
        Assert.Equal(GenerationFamilies.Wan, GenerationFamilies.Normalize("wan2.1"));
        Assert.Null(GenerationFamilies.Normalize("ltx-video"));
        Assert.Equal(GenerationFamilies.Sd15, GenerationFamilies.Normalize("stable-diffusion", "sd-1.5 Stable Diffusion 1.5"));
        Assert.Equal(GenerationFamilies.Sdxl, GenerationFamilies.Normalize("stable-diffusion", "sdxl-base"));

        // The catalogue's role words (model-categories schema).
        Assert.Equal(GenerationFileRoles.Model, GenerationFileRoles.FromCatalogue("checkpoint", "v1-5-pruned-emaonly-fp16.safetensors", 1));
        Assert.Equal(GenerationFileRoles.Diffusion, GenerationFileRoles.FromCatalogue("diffusion_model", "wan2.1_t2v_1.3B_fp16.safetensors", 3));
        Assert.Equal(GenerationFileRoles.HighNoise, GenerationFileRoles.FromCatalogue("diffusion_model", "wan2.2_high_noise_14B.gguf", 4));
        Assert.Equal(GenerationFileRoles.T5xxl, GenerationFileRoles.FromCatalogue("text_encoder", "umt5-xxl-encoder-Q4_K_M.gguf", 3));
        Assert.Equal(GenerationFileRoles.ClipL, GenerationFileRoles.FromCatalogue("text_encoder", "clip_l.safetensors", 4));
        Assert.Equal(GenerationFileRoles.Vae, GenerationFileRoles.FromCatalogue("vae", "wan_2.1_vae.safetensors", 3));
        Assert.Equal(GenerationFileRoles.Vae, GenerationFileRoles.FromCatalogue(null, "wan_2.1_vae.safetensors", 3));
    }
}

/// <summary>Progress from the engine's output lines.</summary>
public sealed class SdCppProgressTests
{
    [Fact]
    public void StagesMoveForwardAndStepsMapIntoSampling()
    {
        var p = new SdCppProgressParser();
        Assert.Null(p.Parse("[INFO ] stable-diffusion.cpp:100 - loading model from 'x'"));
        Assert.Equal(GenerationStages.Loading, p.Stage);
        Assert.Equal(GenerationStages.Encoding, p.Parse("[INFO ] stable-diffusion.cpp:1 - get_learned_condition completed, taking 5 ms")!.Stage);
        var first = p.Parse("  |=>                 | 1/20 - 1.25s/it")!;
        Assert.Equal(GenerationStages.Sampling, first.Stage);
        Assert.Equal(1, first.Step);
        Assert.Equal(20, first.Steps);
        var mid = p.Parse("  |==========>        | 10/20 - 3.10it/s")!;
        Assert.True(mid.Fraction > first.Fraction);
        var decode = p.Parse("[INFO ] stable-diffusion.cpp:2 - sampling completed, taking 25.3s")!;
        Assert.Equal(GenerationStages.Decoding, decode.Stage);
        Assert.True(decode.Fraction >= mid.Fraction);

        // A tiled decode prints its own bar: it stays in decoding and never goes backwards.
        var tile = p.Parse("  |==>  | 1/12 - 0.20s/it")!;
        Assert.Equal(GenerationStages.Decoding, tile.Stage);
        Assert.True(tile.Fraction >= decode.Fraction);
        var save = p.Parse("save result PNG image to 'C:\\work\\output.png'")!;
        Assert.Equal(GenerationStages.Saving, save.Stage);
        Assert.InRange(save.Fraction, 0.97, 1.0);
        Assert.Null(p.Parse("[INFO ] stable-diffusion.cpp:3 - loading model again"));
    }

    [Fact]
    public void LoadLinesNamingTheDecoderDoNotSkipAhead()
    {
        var p = new SdCppProgressParser();
        Assert.Null(p.Parse("[DEBUG] model.cpp:9 - first_stage_model.decoder.conv_in.weight loaded"));
        Assert.Equal(GenerationStages.Loading, p.Stage);
    }

    [Fact]
    public void ErrorLinesAreKeptWithoutTheirSourceLocation()
    {
        var p = new SdCppProgressParser();
        p.Parse("[ERROR] ggml_vulkan.cpp:7 - ErrorOutOfDeviceMemory: failed to allocate buffer");
        Assert.Equal("ErrorOutOfDeviceMemory: failed to allocate buffer", p.LastError);
        Assert.True(SdCppEngine.LooksLikeOutOfMemory(p.LastError!));
        Assert.False(SdCppEngine.LooksLikeOutOfMemory("load model from 'x' failed"));
        Assert.Null(p.Parse(""));
        Assert.Null(p.Parse("random text"));
    }
}

/// <summary>The engine as a real child process, with a stand-in for the sd.cpp program.</summary>
public sealed class SdCppEngineTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "pah-sdcpp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _model;

    public SdCppEngineTests()
    {
        Directory.CreateDirectory(_work);
        _model = Path.Combine(_work, "model.safetensors");
        File.WriteAllText(_model, "model bytes");
    }

    public void Dispose()
    {
        if (_work.Contains("pah-sdcpp-tests-", StringComparison.Ordinal) && Directory.Exists(_work))
        {
            try
            {
                Directory.Delete(_work, recursive: true); // this test's own temp folder
            }
            catch (IOException)
            {
                // A virus scanner may still hold a file; the folder is in %TEMP%.
            }
        }
    }

    private static string FakeExe => Path.Combine(AppContext.BaseDirectory, "FakeSdCpp.exe");

    private static SdCppEngine Engine(bool cpu = false) => new(new SdCppEngineOptions
    {
        ResolveExe = flavour => flavour == SdCppEngine.Vulkan || cpu ? FakeExe : null,
        KillWait = TimeSpan.FromSeconds(5),
    });

    private GenerationLaunch Launch(string prompt, int steps = 6) => new()
    {
        Kind = "image",
        Family = GenerationFamilies.Sd15,
        Components = [new GenerationComponent(GenerationFileRoles.Model, _model, 11)],
        Prompt = prompt,
        Steps = steps,
        Seed = 7,
        OutputPath = Path.Combine(_work, "output.png"),
    };

    private sealed class Collect : IProgress<GenerationProgress>
    {
        public ConcurrentQueue<GenerationProgress> Items { get; } = new();

        public void Report(GenerationProgress value) => Items.Enqueue(value);
    }

    [Fact]
    public async Task ARunWritesTheOutputAndReportsProgressToTheEnd()
    {
        var progress = new Collect();
        var before = File.ReadAllText(_model);
        await Engine().RunAsync(Launch("a lighthouse"), progress, default);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_work, "output.png"));
        Assert.Equal(0x89, bytes[0]);
        var args = System.Text.Encoding.UTF8.GetString(bytes, 8, bytes.Length - 8).Split('\n');
        Assert.Contains("a lighthouse", args);
        Assert.Contains(_model, args);
        Assert.Equal(before, File.ReadAllText(_model));

        var seen = progress.Items.ToList();
        Assert.Contains(seen, p => p.Stage == GenerationStages.Sampling && p.Step == 6 && p.Steps == 6);
        Assert.Equal(1.0, seen[^1].Fraction);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].Fraction >= seen[i - 1].Fraction, "progress went backwards");
        }
    }

    [Fact]
    public async Task CancelKillsTheProcessAndLeavesThePartialForTheHostToRemove()
    {
        var progress = new Collect();
        using var cts = new CancellationTokenSource();
        var engine = Engine();
        var launch = Launch("hang pid");
        var run = engine.RunAsync(launch, new Progress(p =>
        {
            progress.Report(p);
            if (p.Stage == GenerationStages.Sampling)
            {
                cts.Cancel();
            }
        }), cts.Token);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));

        // The stand-in process is gone (killed with its whole tree).
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_work, "fake.pid")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(IsRunning(pid), "the engine process survived the cancel");
        Assert.True(File.Exists(launch.OutputPath)); // the engine never deletes; the job service removes its own partial
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals("FakeSdCpp", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class Progress(Action<GenerationProgress> report) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) => report(value);
    }

    [Fact]
    public async Task FailuresMapToStableCodes()
    {
        var engine = Engine();
        var failed = await Assert.ThrowsAsync<EngineException>(() => engine.RunAsync(Launch("fail"), null, default));
        Assert.Equal(ErrorCodes.EngineError, failed.ErrorCode);
        Assert.Contains("exit code 1", failed.Message, StringComparison.Ordinal);

        var oom = await Assert.ThrowsAsync<EngineException>(() => engine.RunAsync(Launch("oom"), null, default));
        Assert.Equal(ErrorCodes.InsufficientMemory, oom.ErrorCode);

        var none = await Assert.ThrowsAsync<EngineException>(() => engine.RunAsync(Launch("nooutput"), null, default));
        Assert.Equal(ErrorCodes.EngineError, none.ErrorCode);
    }

    [Fact]
    public async Task AMissingBinaryOrModelFileOrAnExistingOutputIsRefusedBeforeAnyProcessStarts()
    {
        var missing = new SdCppEngine(new SdCppEngineOptions { EngineRoot = Path.Combine(_work, "no-engine") });
        Assert.False(missing.Check().Present);
        var e = await Assert.ThrowsAsync<EngineException>(() => missing.RunAsync(Launch("x"), null, default));
        Assert.Equal(ErrorCodes.EngineMissing, e.ErrorCode);

        // Only the graphics-card build: a processor run uses it with --backend cpu.
        var cpuRun = Launch("x") with { Placement = new GenerationPlacement { Backend = "cpu" } };
        await Engine().RunAsync(cpuRun, null, default);
        var written = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(cpuRun.OutputPath));
        Assert.Contains("--backend\ncpu", written, StringComparison.Ordinal);
        File.Delete(cpuRun.OutputPath); // this test's own temp output
        Assert.True(Engine().Check().GpuBuild);

        e = await Assert.ThrowsAsync<EngineException>(() => Engine().RunAsync(Launch("x") with
        {
            Components = [new GenerationComponent(GenerationFileRoles.Model, Path.Combine(_work, "gone.gguf"), 1)],
        }, null, default));
        Assert.Equal(ErrorCodes.ModelNotInstalled, e.ErrorCode);

        var existing = Path.Combine(_work, "output.png");
        await File.WriteAllTextAsync(existing, "an earlier file");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine().RunAsync(Launch("x"), null, default));
        Assert.Equal("an earlier file", await File.ReadAllTextAsync(existing));
    }
}
