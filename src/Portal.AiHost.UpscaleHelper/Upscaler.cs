using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Portal.AiHost.UpscaleHelper;

/// <summary>Runs a super-resolution model on one planar RGB tile (floats 0-1).</summary>
internal interface IModelRunner : IDisposable
{
    /// <summary>The size factor of the model (output size divided by input size).</summary>
    int Scale { get; }

    /// <summary>The device that really runs the model ("cpu" or "dml:N").</summary>
    string Device { get; }

    /// <summary>Returns planar RGB floats of size (width x Scale) by (height x Scale).</summary>
    float[] Run(float[] planar, int width, int height);
}

/// <summary>Where to run: the CPU, or a DirectML adapter by index.</summary>
internal readonly record struct DeviceChoice(bool Cpu, int Adapter)
{
    public static DeviceChoice Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceChoice(true, 0);
        }

        var t = text.Trim();
        if (t.Equals("dml", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceChoice(false, 0);
        }

        if (t.StartsWith("dml:", StringComparison.OrdinalIgnoreCase) && int.TryParse(t.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0)
        {
            return new DeviceChoice(false, i);
        }

        throw new ArgumentException("The device must be cpu, dml or dml:N (an adapter number).");
    }

    public override string ToString() => Cpu ? "cpu" : "dml:" + Adapter;
}

/// <summary>ONNX Runtime session over a model file. DirectML when asked and available; otherwise the CPU provider.</summary>
internal sealed class OnnxRunner : IModelRunner
{
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;

    private OnnxRunner(InferenceSession session, string device, int scale)
    {
        _session = session;
        _input = session.InputMetadata.Keys.First();
        _output = session.OutputMetadata.Keys.First();
        Device = device;
        Scale = scale;
    }

    public int Scale { get; }
    public string Device { get; }

    /// <summary>Set when DirectML was asked for but the CPU provider is in use.</summary>
    public string? FallbackReason { get; private init; }

    public static OnnxRunner Open(string modelPath, DeviceChoice device, int threads)
    {
        string? fallback = null;
        InferenceSession? session = null;
        var used = device.ToString();
        if (!device.Cpu)
        {
            try
            {
                using var o = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                o.AppendExecutionProvider_DML(device.Adapter);
                session = new InferenceSession(modelPath, o);
            }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                fallback = "DirectML did not start: " + e.Message;
                session?.Dispose();
                session = null;
            }
        }

        if (session is null)
        {
            using var o = new SessionOptions();
            if (threads > 0)
            {
                o.IntraOpNumThreads = threads;
            }

            session = new InferenceSession(modelPath, o);
            used = "cpu";
        }

        try
        {
            // Probe the size factor with a small tile.
            var runner = new OnnxRunner(session, used, 1);
            const int probe = 16;
            var res = runner.RunRaw(new float[3 * probe * probe], probe, probe);
            var side = Math.Sqrt(res.Length / 3.0);
            var scale = (int)Math.Round(side / probe);
            if (scale < 1 || scale * scale * probe * probe * 3 != res.Length)
            {
                throw new InvalidDataException("The model output is not a whole-number size factor of its input.");
            }

            return new OnnxRunner(session, used, scale) { FallbackReason = fallback };
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public float[] Run(float[] planar, int width, int height) => RunRaw(planar, width, height);

    private float[] RunRaw(float[] planar, int width, int height)
    {
        var tensor = new DenseTensor<float>(planar, [1, 3, height, width]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)], [_output]);
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Upscales a whole picture tile by tile.</summary>
internal static class Upscaler
{
    /// <summary>Upscales packed RGB. <paramref name="scale"/> is 2 or 4 (a 4x model serves 2x by averaging pairs; a 2x model only 2x).</summary>
    public static byte[] Run(IModelRunner runner, byte[] rgb, int width, int height, int scale, int tile, int overlap,
        Action<int, int>? progress, CancellationToken ct, out int outWidth, out int outHeight)
    {
        if (scale != runner.Scale && !(scale * 2 == runner.Scale))
        {
            throw new ArgumentException($"This model upscales {runner.Scale}x; {scale}x is not possible with it.");
        }

        var tiles = Tiler.Plan(width, height, tile, overlap);
        var acc = new TileAccumulator(width, height, runner.Scale, overlap);
        var done = 0;
        foreach (var t in tiles)
        {
            ct.ThrowIfCancellationRequested();
            var input = Extract(rgb, width, t);
            var result = runner.Run(input, t.Width, t.Height);
            acc.Add(t, result);
            progress?.Invoke(++done, tiles.Count);
        }

        ct.ThrowIfCancellationRequested();
        return acc.Finish(runner.Scale / scale, out outWidth, out outHeight);
    }

    private static float[] Extract(byte[] rgb, int width, TileRect t)
    {
        var plane = t.Width * t.Height;
        var f = new float[3 * plane];
        for (var y = 0; y < t.Height; y++)
        {
            for (var x = 0; x < t.Width; x++)
            {
                var s = ((t.Y + y) * width + t.X + x) * 3;
                var d = y * t.Width + x;
                f[d] = rgb[s] / 255f;
                f[plane + d] = rgb[s + 1] / 255f;
                f[2 * plane + d] = rgb[s + 2] / 255f;
            }
        }

        return f;
    }
}
