using System.Buffers.Binary;
using System.IO.Compression;

namespace Portal.AiHost.UpscaleHelper;

/// <summary>Minimal PNG reader and writer for 8-bit pictures (grey, grey+alpha, palette, RGB, RGBA; not interlaced). Pictures are held as packed RGB.</summary>
internal static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();
    public const long MaxPixels = 200_000_000;

    public static (byte[] Rgb, int Width, int Height) Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 33 || !data[..8].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int pos = 8, width = 0, height = 0, colorType = -1;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        var sawEnd = false;
        while (pos + 12 <= data.Length && !sawEnd)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (pos + 12L + len > data.Length)
            {
                throw new InvalidDataException("The PNG file is cut short.");
            }

            var type = data.Slice(pos + 4, 4);
            var body = data.Slice(pos + 8, (int)len);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (len < 13)
                {
                    throw new InvalidDataException("Bad PNG header.");
                }

                width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if (body[8] != 8 || body[12] != 0)
                {
                    throw new NotSupportedException("Only 8-bit, non-interlaced PNG files are supported.");
                }

                colorType = body[9];
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
            }

            pos += 12 + (int)len;
        }

        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException("The PNG size is not valid.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException("PNG colour type not supported."),
        };
        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("The PNG has no palette.");
        }

        var stride = width * channels;
        var raw = new byte[(long)(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            z.ReadExactly(raw);
        }

        var rgb = new byte[width * height * 3];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[(long)y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            Unfilter(filter, cur, prev, channels);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                var s = x * channels;
                switch (colorType)
                {
                    case 0:
                    case 4:
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = cur[s];
                        break;
                    case 2:
                    case 6:
                        rgb[o] = cur[s];
                        rgb[o + 1] = cur[s + 1];
                        rgb[o + 2] = cur[s + 2];
                        break;
                    default:
                        var idx = cur[s] * 3;
                        if (idx + 2 < palette!.Length)
                        {
                            rgb[o] = palette[idx];
                            rgb[o + 1] = palette[idx + 1];
                            rgb[o + 2] = palette[idx + 2];
                        }

                        break;
                }
            }

            (prev, cur) = (cur, prev);
        }

        return (rgb, width, height);
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        for (var i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            var add = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException("Bad PNG filter."),
            };
            cur[i] = (byte)(cur[i] + add);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("The buffer does not match the size.");
        }

        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            raw[row] = 1; // Sub filter: compresses smooth pictures well.
            var src = rgb.Slice(y * stride, stride);
            for (var i = 0; i < stride; i++)
            {
                raw[row + 1 + i] = (byte)(src[i] - (i >= 3 ? src[i - 3] : 0));
            }
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(ms, "IHDR"u8, ihdr);
        using var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        WriteChunk(ms, "IDAT"u8, comp.ToArray());
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] body)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)body.Length);
        s.Write(word);
        s.Write(type);
        s.Write(body);
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in body)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
        s.Write(word);
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }
}
