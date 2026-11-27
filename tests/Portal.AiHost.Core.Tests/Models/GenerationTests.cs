using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;
using Portal.AiHost.Core.Planning;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>The generation planner (docs/media-and-agents-plan.md 6.3 and 6.4).</summary>
public sealed class GenerationPlannerTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    private static MemoryBudget Budget(long ramGb, double vramGb, bool gpu = true) =>
        new(ramGb * GiB, gpu ? new GpuProfile("AMD Radeon RX 6600", "amd", "0x1", "31.0", 8 * GiB, 0) : null,
            gpu ? (long)(vramGb * GiB) : 0, false, 250e9);

    private static GenerationNeeds Wan13(long diffusion = 1400 * MiB) => GenerationPlanner.Estimate(
    [
        new(GenerationFileRoles.Diffusion, @"C:\m\wan.gguf", diffusion),
        new(GenerationFileRoles.T5xxl, @"C:\m\umt5.gguf", 6 * GiB),
        new(GenerationFileRoles.Vae, @"C:\m\vae.safetensors", 250 * MiB),
    ], GenerationFamilies.Wan, video: true, 832, 480, 33);

    [Fact]
    public void ASmallPictureModelRunsWhollyOnTheCard()
    {
        var needs = GenerationPlanner.Estimate([new(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 2 * GiB)],
            GenerationFamilies.Sd15, video: false, 512, 512, 1);
        Assert.True(needs.EncoderBytes > 0 && needs.VaeBytes > 0, "a whole checkpoint is split by family");
        Assert.Equal(2 * GiB, needs.WeightsBytes);
        var plan = GenerationPlanner.Plan(needs, Budget(40, 6.7), "auto", gpuBuild: true, 512 * 512);
        Assert.True(plan.Fits);
        Assert.Equal("vulkan", plan.Placement!.Backend);
        Assert.False(plan.Placement.ClipOnCpu || plan.Placement.VaeOnCpu || plan.Placement.OffloadToCpu || plan.Placement.VaeTiling);
        Assert.Empty(plan.OnCpu);
    }

    [Fact]
    public void Wan13OnAn8GbCardMovesTheTextEncoderToTheProcessor()
    {
        var plan = GenerationPlanner.Plan(Wan13(), Budget(40, 6.7), "auto", gpuBuild: true, 832 * 480);
        Assert.True(plan.Fits);
        var p = plan.Placement!;
        Assert.Equal("vulkan", p.Backend);
        Assert.True(p.ClipOnCpu);
        Assert.False(p.OffloadToCpu);
        Assert.True(p.FlashAttention && p.VaeTiling);
        Assert.Equal(["text_encoder"], plan.OnCpu);
        Assert.True(plan.GpuBytes <= (long)(6.7 * GiB));
        Assert.True(plan.CpuBytes >= 6 * GiB);
    }

    [Fact]
    public void ABigVideoModelUsesOffloadAndNeverFallsBackToTheProcessor()
    {
        var plan = GenerationPlanner.Plan(Wan13(10 * GiB), Budget(40, 6.7), "auto", gpuBuild: true, 832 * 480);
        Assert.True(plan.Fits);
        Assert.True(plan.Placement!.OffloadToCpu);
        Assert.Contains("weights", plan.OnCpu);

        var tooBig = GenerationPlanner.Plan(Wan13(30 * GiB), Budget(40, 6.7), "auto", gpuBuild: true, 832 * 480);
        Assert.False(tooBig.Fits);
        Assert.Null(tooBig.Placement);

        var noCard = GenerationPlanner.Plan(Wan13(), Budget(40, 0, gpu: false), "auto", gpuBuild: true, 832 * 480);
        Assert.False(noCard.Fits);
        Assert.Contains("graphics card", noCard.Reason, StringComparison.Ordinal);

        // Only when the user chose the processor does video run there.
        var chosen = GenerationPlanner.Plan(Wan13(), Budget(40, 6.7), "cpu", gpuBuild: true, 832 * 480);
        Assert.True(chosen.Fits);
        Assert.Equal("cpu", chosen.Placement!.Backend);
    }

    [Fact]
    public void PicturesFallBackToTheProcessorWhenTheCardOrItsBuildIsMissing()
    {
        var needs = GenerationPlanner.Estimate([new(GenerationFileRoles.Model, @"C:\m\sdxl.safetensors", 7 * GiB)],
            GenerationFamilies.Sdxl, video: false, 1024, 1024, 1);
        var noBuild = GenerationPlanner.Plan(needs, Budget(40, 6.7), "auto", gpuBuild: false, 1024 * 1024);
        Assert.Equal("cpu", noBuild.Placement!.Backend);
        Assert.Equal(0, noBuild.GpuBytes);

        var noRam = GenerationPlanner.Plan(needs, Budget(4, 0, gpu: false), "auto", gpuBuild: true, 1024 * 1024);
        Assert.False(noRam.Fits);
    }

    [Fact]
    public void ComputeGrowsWithSizeAndFramesAndHonoursTheCatalogue()
    {
        var file = new GenerationComponent(GenerationFileRoles.Model, @"C:\m\sd15.safetensors", 2 * GiB);
        var small = GenerationPlanner.Estimate([file], GenerationFamilies.Sd15, false, 512, 512, 1);
        var large = GenerationPlanner.Estimate([file], GenerationFamilies.Sd15, false, 1024, 1024, 1);
        Assert.True(large.ComputeBytes > small.ComputeBytes);
        var fromCatalogue = GenerationPlanner.Estimate([file], GenerationFamilies.Sd15, false, 1024, 1024, 1, 8 * GiB, 512 * 512);
        Assert.Equal(32 * GiB, fromCatalogue.ComputeBytes); // 8 GB at 512x512, four times the pixels
        Assert.True(Wan13().ComputeBytes < GenerationPlanner.Estimate(
            [new(GenerationFileRoles.Diffusion, @"C:\m\wan.gguf", 1)], GenerationFamilies.Wan, true, 832, 480, 81).ComputeBytes);
    }
}

/// <summary>The host-owned output folder: unique names, never overwrite, deletes only its own partial files.</summary>
public sealed class GenerationOutputFolderTests : IDisposable
{
    private readonly TestRoot _root = new();

    public void Dispose() => _root.Dispose();

    private const string Job = "gen_0123456789abcdef0123456789abcdef";

    [Fact]
    public void PublishNeverReplacesAFileAndKeepsTheOriginal()
    {
        var output = new GenerationOutputFolder(Path.Combine(_root.Path, "generated"));
        var created = new DateTimeOffset(2026, 11, 27, 14, 3, 22, TimeSpan.Zero);

        var work1 = output.CreateWorkFolder(Job);
        var partial1 = GenerationOutputFolder.PartialPath(work1, ".png");
        File.WriteAllText(partial1, "first");
        var first = output.Publish(partial1, "image", Job, created);

        // Same job id and time (as if repeated): the second file gets a new name, the first stays as it was.
        Directory.Delete(work1);
        var work2 = output.CreateWorkFolder(Job);
        var partial2 = GenerationOutputFolder.PartialPath(work2, ".png");
        File.WriteAllText(partial2, "second");
        var second = output.Publish(partial2, "image", Job, created);

        Assert.NotEqual(first, second);
        Assert.EndsWith(" (2).png", second, StringComparison.Ordinal);
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
        Assert.False(File.Exists(partial2));
        Assert.Equal(output.FinishedFolder("image"), Path.GetDirectoryName(first));
        Assert.True(File.Exists(Path.Combine(output.Root, GenerationOutputFolder.MarkerName)));
    }

    [Fact]
    public void OnlyTheJobsOwnFilesAreEverDeleted()
    {
        var output = new GenerationOutputFolder(Path.Combine(_root.Path, "generated"));
        var work = output.CreateWorkFolder(Job);
        var partial = GenerationOutputFolder.PartialPath(work, ".avi");
        File.WriteAllText(partial, "partial");
        File.WriteAllText(GenerationOutputFolder.PreviewPath(work), "preview");
        var stray = Path.Combine(work, "something-else.txt");
        File.WriteAllText(stray, "not ours");
        var finished = output.FinishedFolder("video");
        Directory.CreateDirectory(finished);
        var kept = Path.Combine(finished, "earlier.avi");
        File.WriteAllText(kept, "earlier");

        output.DeletePartial(partial);
        output.CleanWorkFolder(work);
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(stray), "an unknown file is never deleted");
        Assert.True(Directory.Exists(work), "a folder that is not empty stays");
        Assert.Equal("earlier", File.ReadAllText(kept));

        // Paths outside a work folder, or not the fixed partial name, are refused.
        Assert.Throws<InvalidOperationException>(() => output.DeletePartial(kept));
        Assert.Throws<InvalidOperationException>(() => output.DeletePartial(stray));
        Assert.Throws<InvalidOperationException>(() => output.CleanWorkFolder(finished));
        Assert.Throws<ArgumentException>(() => output.CreateWorkFolder("..\\escape"));
        Assert.Throws<IOException>(() => output.CreateWorkFolder(Job));
    }

    [Fact]
    public void TheStartSweepRemovesOnlyLeftoversOfCrashedJobs()
    {
        var output = new GenerationOutputFolder(Path.Combine(_root.Path, "generated"));
        var work = output.CreateWorkFolder(Job);
        File.WriteAllText(GenerationOutputFolder.PartialPath(work, ".png"), "partial");
        output.WriteInitImage(work, [0x89, 0x50, 0x4E, 0x47], ".png");
        var foreign = Path.Combine(output.WorkRoot, "not-a-job");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "output.png"), "keep");

        Assert.Equal(1, output.SweepStaleWork());
        Assert.False(Directory.Exists(work));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(foreign, "output.png")));
    }
}
