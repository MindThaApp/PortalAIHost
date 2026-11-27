namespace Portal.AiHost.UpscaleHelper;

/// <summary>One tile of the input picture, in input pixels, with which sides touch a neighbouring tile.</summary>
internal readonly record struct TileRect(int X, int Y, int Width, int Height, bool Left, bool Right, bool Top, bool Bottom);

/// <summary>Tile grid and seam blending. The grid depends only on the picture size, tile size and overlap, so it is the same for every frame of a clip.</summary>
internal static class Tiler
{
    public const int MinTile = 32;

    public static IReadOnlyList<TileRect> Plan(int width, int height, int tile, int overlap)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (tile < MinTile)
        {
            throw new ArgumentOutOfRangeException(nameof(tile), "The tile size must be at least " + MinTile + ".");
        }

        if (overlap < 0 || overlap * 2 >= tile)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "The overlap must be at least 0 and under half the tile size.");
        }

        var xs = Starts(width, tile, overlap);
        var ys = Starts(height, tile, overlap);
        var list = new List<TileRect>(xs.Count * ys.Count);
        var tw = Math.Min(tile, width);
        var th = Math.Min(tile, height);
        for (var yi = 0; yi < ys.Count; yi++)
        {
            for (var xi = 0; xi < xs.Count; xi++)
            {
                list.Add(new TileRect(xs[xi], ys[yi], tw, th, xi > 0, xi < xs.Count - 1, yi > 0, yi < ys.Count - 1));
            }
        }

        return list;
    }

    /// <summary>Tile start positions along one axis: a stride of tile minus overlap, the last tile pulled back to end on the edge.</summary>
    internal static List<int> Starts(int dim, int tile, int overlap)
    {
        var starts = new List<int>();
        if (dim <= tile)
        {
            starts.Add(0);
            return starts;
        }

        var stride = tile - overlap;
        for (var s = 0; ; s += stride)
        {
            if (s + tile >= dim)
            {
                starts.Add(dim - tile);
                break;
            }

            starts.Add(s);
        }

        return starts;
    }

    /// <summary>Blend weight at <paramref name="p"/> (0-based, output pixels) along a tile of <paramref name="len"/> pixels.</summary>
    internal static float AxisWeight(int p, int len, bool nearStart, bool nearEnd, int ramp)
    {
        var w = 1f;
        if (ramp > 0)
        {
            if (nearStart)
            {
                w = Math.Min(w, (p + 0.5f) / ramp);
            }

            if (nearEnd)
            {
                w = Math.Min(w, (len - 1 - p + 0.5f) / ramp);
            }
        }

        return w;
    }
}

/// <summary>Collects upscaled tiles into one picture with weighted blending across seams.</summary>
internal sealed class TileAccumulator
{
    private readonly int _w;
    private readonly int _h;
    private readonly int _scale;
    private readonly int _ramp;
    private readonly float[] _acc;
    private readonly float[] _weight;

    public TileAccumulator(int inWidth, int inHeight, int scale, int overlap)
    {
        _scale = scale;
        _w = inWidth * scale;
        _h = inHeight * scale;
        if (3L * _w * _h > int.MaxValue / 2)
        {
            throw new InvalidOperationException("The result is too large.");
        }

        _ramp = overlap * scale;
        _acc = new float[3 * _w * _h];
        _weight = new float[_w * _h];
    }

    public int OutWidth => _w;
    public int OutHeight => _h;

    /// <summary>Adds a tile result: planar RGB floats 0-1 of size (tile width x scale) by (tile height x scale).</summary>
    public void Add(TileRect t, ReadOnlySpan<float> planar)
    {
        var tw = t.Width * _scale;
        var th = t.Height * _scale;
        if (planar.Length != 3 * tw * th)
        {
            throw new InvalidDataException("The model returned a tile of the wrong size.");
        }

        var plane = tw * th;
        var total = _w * _h;
        var ox = t.X * _scale;
        var oy = t.Y * _scale;
        for (var y = 0; y < th; y++)
        {
            var wy = Tiler.AxisWeight(y, th, t.Top, t.Bottom, _ramp);
            for (var x = 0; x < tw; x++)
            {
                var w = wy * Tiler.AxisWeight(x, tw, t.Left, t.Right, _ramp);
                var di = (oy + y) * _w + ox + x;
                var si = y * tw + x;
                _weight[di] += w;
                _acc[di] += planar[si] * w;
                _acc[total + di] += planar[plane + si] * w;
                _acc[2 * total + di] += planar[2 * plane + si] * w;
            }
        }
    }

    /// <summary>Normalises and converts to packed RGB bytes. A <paramref name="reduce"/> of 2 halves the size (2x2 average) so a 4x model can serve 2x.</summary>
    public byte[] Finish(int reduce, out int width, out int height)
    {
        if (reduce is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(reduce));
        }

        width = _w / reduce;
        height = _h / reduce;
        var rgb = new byte[width * height * 3];
        var plane = _w * _h;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    float sum = 0;
                    for (var dy = 0; dy < reduce; dy++)
                    {
                        for (var dx = 0; dx < reduce; dx++)
                        {
                            var i = (y * reduce + dy) * _w + x * reduce + dx;
                            var wt = _weight[i];
                            sum += wt > 0 ? _acc[c * plane + i] / wt : 0;
                        }
                    }

                    var v = sum / (reduce * reduce);
                    rgb[(y * width + x) * 3 + c] = (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
                }
            }
        }

        return rgb;
    }
}
