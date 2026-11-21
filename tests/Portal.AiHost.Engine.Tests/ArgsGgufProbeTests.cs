using System.Runtime.InteropServices;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

public class ArgsTests
{
    private static EngineLaunchSpec Spec(Placement? p = null, params string[] files) => new()
    {
        ModelId = "m",
        ModelFiles = files.Length == 0 ? new[] { @"C:\models\a-00001-of-00003.gguf", @"C:\models\a-00002-of-00003.gguf" } : files,
        Placement = p ?? new Placement { Backend = "vulkan", GpuLayers = 30, TotalLayers = 40, ContextTokens = 8192, ParallelSlots = 2 },
    };

    private static readonly EngineEndpoint Ep = new(45123, "key123");

    [Fact]
    public void BindsLoopbackOnly_NeverWildcard()
    {
        var args = LlamaServerArgs.Build(Spec(), Ep);
        var i = args.ToList().IndexOf("--host");
        Assert.True(i >= 0);
        Assert.Equal("127.0.0.1", args[i + 1]);
        Assert.DoesNotContain("0.0.0.0", args);
        Assert.DoesNotContain("::", args);
        Assert.Equal(1, args.Count(a => a == "--host"));
        Assert.Contains("--no-webui", args);
        Assert.Equal("key123", args[args.ToList().IndexOf("--api-key") + 1]);
    }

    [Theory]
    [InlineData("--host", "0.0.0.0")]
    [InlineData("--host", "::")]
    [InlineData("--host", "192.168.1.5")]
    public void ExtraArgsCannotRebind(string flag, string value)
    {
        var opts = new LlamaServerArgsOptions { ExtraArgs = new[] { flag, value } };
        Assert.Throws<InvalidOperationException>(() => LlamaServerArgs.Build(Spec(), Ep, opts));
    }

    [Theory]
    [InlineData("--host=0.0.0.0")]
    [InlineData("--host=::")]
    public void EqualsFormIsRejected(string arg)
    {
        Assert.Throws<InvalidOperationException>(() =>
            LlamaServerArgs.AssertLoopbackOnly(new[] { "--host", "127.0.0.1", arg }));
    }

    [Fact]
    public void MissingHostIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => LlamaServerArgs.AssertLoopbackOnly(new[] { "--port", "1" }));

    [Fact]
    public void SplitGgufPassesFirstPartOnly()
    {
        var args = LlamaServerArgs.Build(Spec(), Ep);
        Assert.Equal(@"C:\models\a-00001-of-00003.gguf", args[args.ToList().IndexOf("-m") + 1]);
        Assert.DoesNotContain(@"C:\models\a-00002-of-00003.gguf", args);
    }

    [Fact]
    public void ContextIsTotalAcrossSlots_AndGpuLayersPassed()
    {
        var args = LlamaServerArgs.Build(Spec(), Ep).ToList();
        Assert.Equal("16384", args[args.IndexOf("--ctx-size") + 1]);
        Assert.Equal("2", args[args.IndexOf("--parallel") + 1]);
        Assert.Equal("30", args[args.IndexOf("--n-gpu-layers") + 1]);
    }

    [Fact]
    public void CpuBackendForcesZeroGpuLayers()
    {
        var p = new Placement { Backend = "cpu", GpuLayers = 30, ContextTokens = 4096 };
        var args = LlamaServerArgs.Build(Spec(p), Ep).ToList();
        Assert.Equal("0", args[args.IndexOf("--n-gpu-layers") + 1]);
    }

    [Fact]
    public void MoeAndOverrideTensorAndKvAndEmbeddings()
    {
        var p = new Placement { Backend = "vulkan", GpuLayers = 99, CpuMoeLayers = 12, ContextTokens = 4096, KvType = "q8_0" };
        var spec = Spec(p) with { Kind = ModelKinds.Embedding, SupportsTools = true };
        var args = LlamaServerArgs.Build(spec, Ep, new LlamaServerArgsOptions { OverrideTensor = "exps=CPU" }).ToList();
        Assert.Equal("12", args[args.IndexOf("--n-cpu-moe") + 1]);
        Assert.Equal("exps=CPU", args[args.IndexOf("--override-tensor") + 1]);
        Assert.Equal("q8_0", args[args.IndexOf("--cache-type-k") + 1]);
        Assert.Contains("--embeddings", args);
        Assert.Contains("--jinja", args);
    }

    [Fact]
    public void NoModelFileThrows() =>
        Assert.Throws<ArgumentException>(() => LlamaServerArgs.Build(new EngineLaunchSpec(), Ep));
}

public class GgufTests
{
    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return BitConverter.GetBytes((ulong)b.Length).Concat(b).ToArray();
    }

    [Fact]
    public void ReadsArchitectureLayersContextAndExperts()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(0x46554747u);
            w.Write(3u);
            w.Write(0UL);
            w.Write(7UL);
            w.Write(Str("general.architecture")); w.Write(8u); w.Write(Str("qwen3moe"));
            w.Write(Str("general.file_type")); w.Write(4u); w.Write(15u);
            w.Write(Str("qwen3moe.block_count")); w.Write(4u); w.Write(48u);
            w.Write(Str("qwen3moe.context_length")); w.Write(4u); w.Write(40960u);
            w.Write(Str("qwen3moe.expert_count")); w.Write(4u); w.Write(128u);
            w.Write(Str("qwen3moe.rope.dimension_sections")); w.Write(9u); w.Write(5u); w.Write(2UL); w.Write(1); w.Write(2);
            w.Write(Str("tokenizer.ggml.tokens")); w.Write(9u); // never read past here
        }

        ms.Position = 0;
        var info = GgufReader.Read(ms);
        Assert.Equal("qwen3moe", info.Architecture);
        Assert.Equal(48, info.BlockCount);
        Assert.Equal(40960, info.ContextLength);
        Assert.Equal(128, info.ExpertCount);
        Assert.Equal(3u, info.Version);
    }

    [Fact]
    public void DenseModelHasNoExperts_AndBadMagicThrows()
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write(2UL);
            w.Write(Str("general.architecture")); w.Write(8u); w.Write(Str("llama"));
            w.Write(Str("llama.block_count")); w.Write(4u); w.Write(32u);
        }

        ms.Position = 0;
        var info = GgufReader.Read(ms);
        Assert.Null(info.ExpertCount);
        Assert.Equal(32, info.BlockCount);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(new MemoryStream(new byte[16])));
    }
}

public class ProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portalaihost-wp2-probe-" + Guid.NewGuid().ToString("N"));

    public ProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "vulkan"));
        Directory.CreateDirectory(Path.Combine(_root, "cpu"));
        File.WriteAllText(Path.Combine(_root, "vulkan", "llama-server.exe"), "x");
        File.WriteAllText(Path.Combine(_root, "cpu", "llama-server.exe"), "x");
    }

    public void Dispose()
    {
        // Only the temp folder created by this test class.
        if (Directory.Exists(_root) && _root.Contains("portalaihost-wp2-probe-", StringComparison.Ordinal))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void ParsesListDevices()
    {
        var r = RuntimeProbe.ParseListDevices("Available devices:\n  Vulkan0: AMD Radeon RX 7900 XTX (24560 MiB, 24000 MiB free)\n  Vulkan1: Intel(R) UHD (128 MiB, 100 MiB free)\n", 0);
        Assert.True(r.Ok);
        Assert.Equal(24560, r.TotalMiB);
        Assert.Contains("7900", r.DeviceName);
    }

    [Fact]
    public void ZeroMemoryOrNonZeroExitOrNoDeviceFails()
    {
        Assert.False(RuntimeProbe.ParseListDevices("  Vulkan0: X (0 MiB, 0 MiB free)", 0).Ok);
        Assert.False(RuntimeProbe.ParseListDevices("", 0).Ok);
        Assert.False(RuntimeProbe.ParseListDevices("  Vulkan0: X (8000 MiB, 7000 MiB free)", 1).Ok);
    }

    private static readonly VulkanProbeResult Good = new(true, "GPU", 8000, 7000, null);

    [Fact]
    public void AutoPicksVulkanWhenProbeOk()
    {
        var b = RuntimeProbe.Select(_root, BackendPreference.Auto, Good, false, Architecture.X64);
        Assert.Equal("vulkan", b.Flavour);
    }

    [Theory]
    [InlineData(true, false)]   // probe failed
    [InlineData(false, true)]   // marked bad
    public void FallsBackToCpu(bool probeFails, bool markedBad)
    {
        var probe = probeFails ? new VulkanProbeResult(false, null, 0, 0, "x") : Good;
        Assert.Equal("cpu", RuntimeProbe.Select(_root, BackendPreference.Auto, probe, markedBad, Architecture.X64).Flavour);
    }

    [Fact]
    public void CpuPreferenceArm64AndMissingFolderAllMeanCpu()
    {
        Assert.Equal("cpu", RuntimeProbe.Select(_root, BackendPreference.Cpu, Good, false, Architecture.X64).Flavour);
        Assert.Equal("cpu", RuntimeProbe.Select(_root, BackendPreference.Auto, Good, false, Architecture.Arm64).Flavour);
        File.Delete(Path.Combine(_root, "vulkan", "llama-server.exe"));
        Assert.Equal("cpu", RuntimeProbe.Select(_root, BackendPreference.Auto, Good, false, Architecture.X64).Flavour);
    }
}
