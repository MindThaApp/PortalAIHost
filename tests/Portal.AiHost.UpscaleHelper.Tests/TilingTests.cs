using Xunit;

namespace Portal.AiHost.UpscaleHelper.Tests;

public sealed class TilingTests
{
    [Theory]
    [InlineData(100, 64, 8)]
    [InlineData(64, 64, 8)]
    [InlineData(30, 64, 8)]
    [InlineData(1000, 128, 16)]
    [InlineData(129, 128, 0)]
    public void Starts_cover_the_axis_and_end_on_the_edge(int dim, int tile, int overlap)
    {
        var starts = Tiler.Starts(dim, tile, overlap);
        var len = Math.Min(tile, dim);
        Assert.Equal(0, starts[0]);
        Assert.Equal(dim - len, starts[^1]);
        for (var i = 1; i < starts.Count; i++)
        {
            // Every pair of neighbours overlaps by at least the overlap and never leaves a gap.
            Assert.True(starts[i] > starts[i - 1]);
            Assert.True(starts[i - 1] + len - starts[i] >= overlap);
        }
    }

    [Fact]
    public void Plan_marks_inner_sides_only()
    {
        var plan = Tiler.Plan(100, 40, 64, 8);
        Assert.Equal(2, plan.Count);
        Assert.False(plan[0].Left);
        Assert.True(plan[0].Right);
        Assert.True(plan[1].Left);
        Assert.False(plan[1].Right);
        Assert.All(plan, t => Assert.False(t.Top || t.Bottom));
        Assert.All(plan, t => Assert.Equal(40, t.Height));
    }

    [Theory]
    [InlineData(32, 16)]
    [InlineData(16, 0)]
    [InlineData(64, -1)]
    public void Plan_rejects_bad_numbers(int tile, int overlap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Tiler.Plan(100, 100, tile, overlap));

    [Theory]
    [InlineData(2, 64, 8)]
    [InlineData(4, 48, 12)]
    [InlineData(2, 100, 0)]
    public void Identical_tiles_blend_to_the_exact_picture(int scale, int tile, int overlap)
    {
        const int w = 150;
        const int h = 97;
        var rgb = TestImages.Gradient(w, h);
        var runner = new NearestRunner(scale);
        var result = Upscaler.Run(runner, rgb, w, h, scale, tile, overlap, null, CancellationToken.None, out var ow, out var oh);
        Assert.Equal(w * scale, ow);
        Assert.Equal(h * scale, oh);
        var expected = TestImages.NearestExpected(rgb, w, h, scale);
        // Rounding of the 0-1 floats may move a value by one at most.
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(Math.Abs(result[i] - expected[i]), 0, 1);
        }

        Assert.True(runner.Calls > 1);
    }

    [Fact]
    public void Seam_between_tiles_with_different_results_is_a_smooth_ramp()
    {
        // Two tiles side by side; the first returns the picture plus 0.2, the second exactly the picture. A flat grey
        // input makes any step visible: without blending there would be a jump of about 51 grey levels.
        const int w = 96;
        const int h = 32;
        var rgb = Enumerable.Repeat((byte)100, w * h * 3).ToArray();
        var runner = new NearestRunner(1) { BiasPerOddCall = 0.2f };
        var result = Upscaler.Run(runner, rgb, w, h, 1, 64, 16, null, CancellationToken.None, out _, out _);
        var row = Enumerable.Range(0, w).Select(x => (int)result[(10 * w + x) * 3]).ToArray();
        Assert.True(row[0] >= 150, "left side is the biased tile");
        Assert.True(row[^1] <= 102, "right side is the plain tile");
        var maxStep = 0;
        for (var x = 1; x < w; x++)
        {
            maxStep = Math.Max(maxStep, Math.Abs(row[x] - row[x - 1]));
        }

        Assert.True(maxStep <= 6, $"largest step was {maxStep}");
    }

    [Fact]
    public void Half_scale_from_a_4x_model_averages_pairs()
    {
        var rgb = TestImages.Gradient(40, 40);
        var runner = new NearestRunner(4);
        var result = Upscaler.Run(runner, rgb, 40, 40, 2, 32, 4, null, CancellationToken.None, out var ow, out var oh);
        Assert.Equal((80, 80), (ow, oh));
        var expected = TestImages.NearestExpected(rgb, 40, 40, 2);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(Math.Abs(result[i] - expected[i]), 0, 1);
        }
    }

    [Fact]
    public void A_scale_the_model_cannot_do_is_refused()
    {
        var rgb = TestImages.Gradient(40, 40);
        Assert.Throws<ArgumentException>(() => Upscaler.Run(new NearestRunner(2), rgb, 40, 40, 4, 32, 4, null, CancellationToken.None, out _, out _));
    }

    [Fact]
    public void Cancel_stops_between_tiles_and_reports_progress()
    {
        var rgb = TestImages.Gradient(200, 100);
        using var cts = new CancellationTokenSource();
        var runner = new NearestRunner(2) { OnCall = n => { if (n == 2) { cts.Cancel(); } } };
        var seen = new List<int>();
        Assert.Throws<OperationCanceledException>(() =>
            Upscaler.Run(runner, rgb, 200, 100, 2, 64, 8, (d, _) => seen.Add(d), cts.Token, out _, out _));
        Assert.Equal(2, runner.Calls);
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void Png_round_trips()
    {
        var rgb = TestImages.Gradient(37, 23);
        var png = Png.Encode(rgb, 37, 23);
        var (back, w, h) = Png.Decode(png);
        Assert.Equal((37, 23), (w, h));
        Assert.Equal(rgb, back);
    }

    [Fact]
    public void Png_rejects_garbage()
    {
        Assert.Throws<InvalidDataException>(() => Png.Decode(new byte[100]));
    }

    [Fact]
    public void WriteNew_never_overwrites()
    {
        var dir = TestImages.TempDir();
        try
        {
            var a = UpscaleSession.WriteNew(dir, "pic", [1]);
            var b = UpscaleSession.WriteNew(dir, "pic", [2]);
            var c = UpscaleSession.WriteNew(dir, "pic", [3]);
            Assert.Equal("pic.png", Path.GetFileName(a));
            Assert.Equal("pic (2).png", Path.GetFileName(b));
            Assert.Equal("pic (3).png", Path.GetFileName(c));
            Assert.Equal([1], File.ReadAllBytes(a));
            Assert.Equal([2], File.ReadAllBytes(b));
            Assert.Equal(3, Directory.GetFiles(dir).Length); // No temporary file left.
        }
        finally
        {
            Directory.Delete(dir, true); // Test folder created above.
        }
    }
}
