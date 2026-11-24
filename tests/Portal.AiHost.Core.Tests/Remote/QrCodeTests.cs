using System.Text;
using Portal.AiHost.Core.Remote;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class QrCodeTests
{
    private const string Sample =
        "portalai://pair?h=192.168.100.100&p=48123&f=Zm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyZm9vYmFyMTIzNDU&c=042917&n=DESKTOP-1";

    [Theory]
    [InlineData("hi", 1)]
    [InlineData("0123456789012345678901234567890", 3)]
    [InlineData(Sample, 7)]
    public void PicksTheSmallestVersionThatFits(string text, int version)
    {
        var qr = QrCode.Encode(text);
        Assert.Equal(version, qr.Version);
        Assert.Equal(version * 4 + 17, qr.Size);
    }

    [Fact]
    public void TextOverTheCapacityIsRefused() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 300)));

    [Fact]
    public void FinderTimingAndFormatPatternsAreInPlace()
    {
        var qr = QrCode.Encode(Sample);
        foreach (var (cx, cy) in new[] { (3, 3), (qr.Size - 4, 3), (3, qr.Size - 4) })
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Assert.Equal(d is not 2, qr[cx + dx, cy + dy]);
                }
            }
        }

        for (var i = 8; i < qr.Size - 8; i++)
        {
            Assert.Equal(i % 2 == 0, qr[i, 6]);
            Assert.Equal(i % 2 == 0, qr[6, i]);
        }

        Assert.True(qr[8, qr.Size - 8]);
    }

    [Fact]
    public void FormatInformationIsValidAndRepeated()
    {
        var qr = QrCode.Encode(Sample);
        var first = 0;
        for (var i = 0; i <= 5; i++) first |= (qr[8, i] ? 1 : 0) << i;
        first |= (qr[8, 7] ? 1 : 0) << 6;
        first |= (qr[8, 8] ? 1 : 0) << 7;
        first |= (qr[7, 8] ? 1 : 0) << 8;
        for (var i = 9; i < 15; i++) first |= (qr[14 - i, 8] ? 1 : 0) << i;

        var second = 0;
        for (var i = 0; i < 8; i++) second |= (qr[qr.Size - 1 - i, 8] ? 1 : 0) << i;
        for (var i = 8; i < 15; i++) second |= (qr[8, qr.Size - 15 + i] ? 1 : 0) << i;

        Assert.Equal(first, second);
        var data = (first ^ 0x5412) >> 10;
        Assert.Equal(qr.Mask, data & 7);
        Assert.Equal(0, data >> 3); // level M
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        Assert.Equal(((first ^ 0x5412) & 0x3FF), rem & 0x3FF);
    }

    [Theory]
    [InlineData("hi")]
    [InlineData("0123456789012345678901234567890")]
    [InlineData(Sample)]
    [InlineData("portalai://pair?h=10.0.0.2&p=48123&f=x&c=000001&n=Maïé 中文")]
    public void ReadingTheCodeBackGivesTheTextAndEveryErrorCorrectionBlockChecks(string text)
    {
        var qr = QrCode.Encode(text);
        Assert.Equal(text, QrReader.Read(qr));
    }

    /// <summary>A reader written from the standard: format, unmask, zigzag, de-interleave, check the Reed-Solomon codewords.</summary>
    private static class QrReader
    {
        private static readonly int[] EccPerBlock = [10, 16, 26, 18, 24, 16, 18, 22, 22, 26];
        private static readonly int[] Blocks = [1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

        public static string Read(QrCode qr)
        {
            var size = qr.Size;
            var isFunction = new bool[size, size];
            Mark(isFunction, 0, 0, 9, 9);
            Mark(isFunction, size - 8, 0, 8, 9);
            Mark(isFunction, 0, size - 8, 9, 8);
            Mark(isFunction, 6, 0, 1, size);
            Mark(isFunction, 0, 6, size, 1);
            if (qr.Version >= 2)
            {
                var count = qr.Version / 7 + 2;
                var step = (qr.Version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
                var pos = new int[count];
                pos[0] = 6;
                for (int i = count - 1, p = size - 7; i >= 1; i--, p -= step) pos[i] = p;
                for (var i = 0; i < count; i++)
                {
                    for (var j = 0; j < count; j++)
                    {
                        if (!((i == 0 && j == 0) || (i == 0 && j == count - 1) || (i == count - 1 && j == 0)))
                        {
                            Mark(isFunction, pos[i] - 2, pos[j] - 2, 5, 5);
                        }
                    }
                }
            }

            if (qr.Version >= 7)
            {
                Mark(isFunction, size - 11, 0, 3, 6);
                Mark(isFunction, 0, size - 11, 6, 3);
            }

            var bits = new List<bool>();
            for (var right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (var v = 0; v < size; v++)
                {
                    for (var j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var y = ((right + 1) & 2) == 0 ? size - 1 - v : v;
                        if (!isFunction[y, x])
                        {
                            bits.Add(qr[x, y] ^ Masked(qr.Mask, x, y));
                        }
                    }
                }
            }

            var raw = bits.Count / 8;
            var codewords = new byte[raw];
            for (var i = 0; i < raw * 8; i++) if (bits[i]) codewords[i / 8] |= (byte)(0x80 >> (i % 8));

            var blocks = Blocks[qr.Version - 1];
            var ecc = EccPerBlock[qr.Version - 1];
            var shortBlocks = blocks - raw % blocks;
            var shortLen = raw / blocks;
            var data = new List<byte>[blocks];
            var check = new List<byte>[blocks];
            for (var i = 0; i < blocks; i++) { data[i] = []; check[i] = []; }
            var k = 0;
            for (var i = 0; i < shortLen - ecc + 1; i++)
            {
                for (var b = 0; b < blocks; b++)
                {
                    if (i < shortLen - ecc + (b < shortBlocks ? 0 : 1)) data[b].Add(codewords[k++]);
                }
            }

            for (var i = 0; i < ecc; i++) for (var b = 0; b < blocks; b++) check[b].Add(codewords[k++]);

            var all = new List<byte>();
            for (var b = 0; b < blocks; b++)
            {
                // Appending the check bytes to the data must leave a zero remainder under the generator polynomial.
                Assert.True(IsCodeword([.. data[b], .. check[b]], ecc), "Reed-Solomon check failed in block " + b);
                all.AddRange(data[b]);
            }

            var stream = new List<bool>();
            foreach (var cw in all) for (var i = 7; i >= 0; i--) stream.Add(((cw >> i) & 1) != 0);
            int At(int from, int n) { var r = 0; for (var i = 0; i < n; i++) r = (r << 1) | (stream[from + i] ? 1 : 0); return r; }
            Assert.Equal(0b0100, At(0, 4));
            var countBits = qr.Version <= 9 ? 8 : 16;
            var length = At(4, countBits);
            var text = new byte[length];
            for (var i = 0; i < length; i++) text[i] = (byte)At(4 + countBits + i * 8, 8);
            return Encoding.UTF8.GetString(text);
        }

        private static void Mark(bool[,] f, int x, int y, int w, int h)
        {
            for (var j = y; j < y + h; j++) for (var i = x; i < x + w; i++) f[j, i] = true;
        }

        private static bool Masked(int mask, int x, int y) => mask switch
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

        /// <summary>The syndromes at alpha^0..alpha^(ecc-1) of a valid Reed-Solomon codeword are all zero.</summary>
        private static bool IsCodeword(List<byte> word, int ecc)
        {
            var exp = new int[512];
            var x = 1;
            for (var i = 0; i < 255; i++)
            {
                exp[i] = x;
                exp[i + 255] = x;
                x <<= 1;
                if (x >= 256) x ^= 0x11D;
            }

            var log = new int[256];
            for (var i = 0; i < 255; i++) log[exp[i]] = i;
            for (var s = 0; s < ecc; s++)
            {
                var acc = 0;
                foreach (var b in word)
                {
                    // Horner: acc = acc * alpha^s + b
                    acc = (acc == 0 ? 0 : exp[log[acc] + s]) ^ b;
                }

                if (acc != 0) return false;
            }

            return true;
        }
    }
}
