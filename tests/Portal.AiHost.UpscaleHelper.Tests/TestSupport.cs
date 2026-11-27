using System.Text;

namespace Portal.AiHost.UpscaleHelper.Tests;

/// <summary>Stub model: repeats each pixel scale x scale times (nearest neighbour), optionally nudging values per call.</summary>
internal sealed class NearestRunner : IModelRunner
{
    public NearestRunner(int scale) => Scale = scale;

    public int Scale { get; }
    public string Device => "cpu";
    public int Calls { get; private set; }
    public Action<int>? OnCall { get; set; }
    public float BiasPerOddCall { get; set; }

    public float[] Run(float[] planar, int width, int height)
    {
        Calls++;
        OnCall?.Invoke(Calls);
        var ow = width * Scale;
        var oh = height * Scale;
        var o = new float[3 * ow * oh];
        var bias = Calls % 2 == 1 ? BiasPerOddCall : 0f;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < oh; y++)
            {
                for (var x = 0; x < ow; x++)
                {
                    o[c * ow * oh + y * ow + x] = planar[c * width * height + (y / Scale) * width + x / Scale] + bias;
                }
            }
        }

        return o;
    }

    public void Dispose()
    {
    }
}

internal static class TestImages
{
    /// <summary>A picture whose pixels all differ in a smooth, predictable way.</summary>
    public static byte[] Gradient(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                rgb[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                rgb[i + 2] = (byte)((x + y) % 256);
            }
        }

        return rgb;
    }

    public static byte[] NearestExpected(byte[] rgb, int w, int h, int scale)
    {
        var ow = w * scale;
        var oh = h * scale;
        var o = new byte[ow * oh * 3];
        for (var y = 0; y < oh; y++)
        {
            for (var x = 0; x < ow; x++)
            {
                Buffer.BlockCopy(rgb, ((y / scale) * w + x / scale) * 3, o, (y * ow + x) * 3, 3);
            }
        }

        return o;
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "upscale-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Builds a tiny ONNX model by hand: one Resize node (nearest) with a fixed scale on height and width. Needs no download.</summary>
internal static class TinyOnnx
{
    public static byte[] NearestResize(int scale)
    {
        var node = Cat(Str(1, "X"), Str(1, string.Empty), Str(1, "scales"), Str(2, "Y"), Str(3, "resize"), Str(4, "Resize"),
            Msg(5, Cat(Str(1, "mode"), Varint(20, 3), Bytes(4, "nearest"u8.ToArray()))),
            Msg(5, Cat(Str(1, "nearest_mode"), Varint(20, 3), Bytes(4, "floor"u8.ToArray()))));
        var scales = new byte[16];
        BitConverter.GetBytes(1f).CopyTo(scales, 0);
        BitConverter.GetBytes(1f).CopyTo(scales, 4);
        BitConverter.GetBytes((float)scale).CopyTo(scales, 8);
        BitConverter.GetBytes((float)scale).CopyTo(scales, 12);
        var init = Cat(Varint(1, 4), Varint(2, 1), Str(8, "scales"), Bytes(9, scales));
        var dims = Cat(Msg(1, Varint(1, 1)), Msg(1, Varint(1, 3)), Msg(1, Str(2, "h")), Msg(1, Str(2, "w")));
        var input = Cat(Str(1, "X"), Msg(2, Msg(1, Cat(Varint(1, 1), Msg(2, dims)))));
        var output = Cat(Str(1, "Y"), Msg(2, Msg(1, Varint(1, 1))));
        var graph = Cat(Msg(1, node), Str(2, "g"), Msg(5, init), Msg(11, input), Msg(12, output));
        return Cat(Varint(1, 8), Msg(8, Varint(2, 13)), Msg(7, graph));
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Raw(ulong v)
    {
        var l = new List<byte>();
        while (v >= 0x80)
        {
            l.Add((byte)(v | 0x80));
            v >>= 7;
        }

        l.Add((byte)v);
        return l.ToArray();
    }

    private static byte[] Varint(int field, ulong v) => Cat(Raw((ulong)field << 3), Raw(v));

    private static byte[] Bytes(int field, byte[] b) => Cat(Raw(((ulong)field << 3) | 2), Raw((ulong)b.Length), b);

    private static byte[] Str(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));

    private static byte[] Msg(int field, byte[] body) => Bytes(field, body);
}
