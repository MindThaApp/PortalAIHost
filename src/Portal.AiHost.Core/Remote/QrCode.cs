using System.Text;

namespace Portal.AiHost.Core.Remote;

/// <summary>
/// Minimal QR Code encoder (ISO/IEC 18004): byte mode, error correction level M, versions 1 to 10 (up to 213 bytes).
/// Enough for the pairing code; no dependency.
/// </summary>
public sealed class QrCode
{
    private const int MaxVersion = 10;

    // Level M, versions 1..10: error correction codewords per block and number of blocks.
    private static readonly int[] EccPerBlock = [10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
    private static readonly int[] BlockCount = [1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    private readonly bool[,] _modules;
    private readonly bool[,] _function;

    private QrCode(int version, byte[] dataCodewords)
    {
        Version = version;
        Size = version * 4 + 17;
        _modules = new bool[Size, Size];
        _function = new bool[Size, Size];
        DrawFunctionPatterns();
        DrawCodewords(AddEcc(dataCodewords));
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(mask);
            DrawFormatBits(mask);
            var penalty = Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }

            ApplyMask(mask);
        }

        Mask = best;
        ApplyMask(best);
        DrawFormatBits(best);
    }

    public int Version { get; }

    public int Size { get; }

    public int Mask { get; }

    /// <summary>True for a dark module. (0,0) is the top-left corner.</summary>
    public bool this[int x, int y] => x >= 0 && x < Size && y >= 0 && y < Size && _modules[y, x];

    /// <summary>Encodes the text as UTF-8 bytes. Throws ArgumentException when it does not fit version 10.</summary>
    public static QrCode Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = Encoding.UTF8.GetBytes(text);
        for (var version = 1; version <= MaxVersion; version++)
        {
            var capacity = DataCodewords(version);
            var countBits = version <= 9 ? 8 : 16;
            if (4 + countBits + data.Length * 8 > capacity * 8)
            {
                continue;
            }

            var bits = new List<bool>();
            Append(bits, 0b0100, 4);
            Append(bits, data.Length, countBits);
            foreach (var b in data)
            {
                Append(bits, b, 8);
            }

            Append(bits, 0, Math.Min(4, capacity * 8 - bits.Count));
            while (bits.Count % 8 != 0)
            {
                bits.Add(false);
            }

            var codewords = new byte[capacity];
            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    codewords[i / 8] |= (byte)(0x80 >> (i % 8));
                }
            }

            for (var i = bits.Count / 8; i < capacity; i++)
            {
                codewords[i] = (i - bits.Count / 8) % 2 == 0 ? (byte)0xEC : (byte)0x11;
            }

            return new QrCode(version, codewords);
        }

        throw new ArgumentException("The text is too long for a QR code of this encoder.", nameof(text));
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private static int RawModules(int version)
    {
        var size = version * 4 + 17;
        var result = size * size - 8 * 8 * 3 - (15 * 2 + 1) - (size - 16) * 2;
        if (version >= 2)
        {
            var align = version / 7 + 2;
            result -= (align - 1) * (align - 1) * 25 + (align - 2) * 20;
            if (version >= 7)
            {
                result -= 6 * 3 * 2;
            }
        }

        return result;
    }

    private static int DataCodewords(int version) =>
        RawModules(version) / 8 - EccPerBlock[version - 1] * BlockCount[version - 1];

    // ---- error correction ---------------------------------------------------------------------

    private byte[] AddEcc(byte[] data)
    {
        var blocks = BlockCount[Version - 1];
        var eccLen = EccPerBlock[Version - 1];
        var raw = RawModules(Version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLen = raw / blocks;
        var divisor = Divisor(eccLen);
        var dataBlocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        for (int i = 0, offset = 0; i < blocks; i++)
        {
            var length = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
            var block = data[offset..(offset + length)];
            offset += length;
            dataBlocks.Add(block);
            eccBlocks.Add(Remainder(block, divisor));
        }

        var result = new List<byte>();
        for (var i = 0; i < dataBlocks[^1].Length; i++)
        {
            foreach (var block in dataBlocks)
            {
                if (i < block.Length)
                {
                    result.Add(block[i]);
                }
            }
        }

        for (var i = 0; i < eccLen; i++)
        {
            foreach (var block in eccBlocks)
            {
                result.Add(block[i]);
            }
        }

        return [.. result];
    }

    private static byte[] Divisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] Remainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }

        return result;
    }

    private static byte Multiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    // ---- drawing -----------------------------------------------------------------------------

    private void SetFunction(int x, int y, bool dark)
    {
        _modules[y, x] = dark;
        _function[y, x] = true;
    }

    private void DrawFunctionPatterns()
    {
        for (var i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        var positions = AlignmentPositions();
        for (var i = 0; i < positions.Length; i++)
        {
            for (var j = 0; j < positions.Length; j++)
            {
                if (!((i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0)))
                {
                    DrawAlignment(positions[i], positions[j]);
                }
            }
        }

        DrawFormatBits(0);
        DrawVersion();
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1)
        {
            return [];
        }

        var count = Version / 7 + 2;
        var step = (Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, pos = Size - 7; i >= 1; i--, pos -= step)
        {
            result[i] = pos;
        }

        return result;
    }

    private void DrawFinder(int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int x = cx + dx, y = cy + dy;
                if (x >= 0 && x < Size && y >= 0 && y < Size)
                {
                    SetFunction(x, y, distance is not (2 or 4));
                }
            }
        }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        }
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private void DrawFormatBits(int mask)
    {
        // Level M has format bits 00.
        var data = mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        }

        var bits = ((data << 10) | rem) ^ 0x5412;
        for (var i = 0; i <= 5; i++)
        {
            SetFunction(8, i, Bit(bits, i));
        }

        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (var i = 9; i < 15; i++)
        {
            SetFunction(14 - i, 8, Bit(bits, i));
        }

        for (var i = 0; i < 8; i++)
        {
            SetFunction(Size - 1 - i, 8, Bit(bits, i));
        }

        for (var i = 8; i < 15; i++)
        {
            SetFunction(8, Size - 15 + i, Bit(bits, i));
        }

        SetFunction(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7)
        {
            return;
        }

        var rem = Version;
        for (var i = 0; i < 12; i++)
        {
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        }

        var bits = (Version << 12) | rem;
        for (var i = 0; i < 18; i++)
        {
            var dark = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, dark);
            SetFunction(b, a, dark);
        }
    }

    private void DrawCodewords(byte[] data)
    {
        var i = 0;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var vertical = 0; vertical < Size; vertical++)
            {
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? Size - 1 - vertical : vertical;
                    if (!_function[y, x] && i < data.Length * 8)
                    {
                        _modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert && !_function[y, x])
                {
                    _modules[y, x] = !_modules[y, x];
                }
            }
        }
    }

    // ---- mask choice -------------------------------------------------------------------------

    private int Penalty()
    {
        var penalty = 0;
        var dark = 0;
        for (var a = 0; a < Size; a++)
        {
            for (var vertical = 0; vertical < 2; vertical++)
            {
                var run = 1;
                var history = 0;
                for (var b = 0; b < Size; b++)
                {
                    var module = vertical == 0 ? _modules[a, b] : _modules[b, a];
                    if (vertical == 0 && module)
                    {
                        dark++;
                    }

                    if (b > 0)
                    {
                        var previous = vertical == 0 ? _modules[a, b - 1] : _modules[b - 1, a];
                        if (module == previous)
                        {
                            run++;
                            if (run == 5)
                            {
                                penalty += 3;
                            }
                            else if (run > 5)
                            {
                                penalty++;
                            }
                        }
                        else
                        {
                            run = 1;
                        }
                    }

                    // 1:1:3:1:1 finder-like pattern with four light modules on either side.
                    history = ((history << 1) | (module ? 1 : 0)) & 0x7FF;
                    if (b >= 10 && (history == 0b10111010000 || history == 0b00001011101))
                    {
                        penalty += 40;
                    }
                }
            }
        }

        for (var y = 0; y < Size - 1; y++)
        {
            for (var x = 0; x < Size - 1; x++)
            {
                var c = _modules[y, x];
                if (c == _modules[y, x + 1] && c == _modules[y + 1, x] && c == _modules[y + 1, x + 1])
                {
                    penalty += 3;
                }
            }
        }

        var total = Size * Size;
        penalty += ((Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10;
        return penalty;
    }
}
