using System.Buffers.Binary;
using System.Text;

namespace Portal.AiHost.Core.Store;

/// <summary>The file is not a readable GGUF model. The message is plain language for the window.</summary>
public sealed class GgufFormatException(string message) : IOException(message);

/// <summary>What the header of a GGUF file says. Name, architecture and context length are null when the file does not state them.</summary>
public sealed record GgufInfo(uint Version, ulong TensorCount, ulong MetadataCount, string? Name, string? Architecture, long? ContextLength);

/// <summary>
/// Reads the header and the few metadata values the host shows from a GGUF file (versions 2 and 3). Read-only: the
/// file is opened for reading and shared, never written. Large values (token lists) are skipped, not loaded.
/// </summary>
public static class GgufReader
{
    private const int MaxKeyBytes = 4096;
    private const int MaxStringBytes = 1 << 20;
    private const ulong MaxMetadataCount = 1_000_000;

    public static GgufInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(stream);
    }

    public static GgufInfo Read(Stream stream)
    {
        var r = new Cursor(stream);
        Span<byte> magic = stackalloc byte[4];
        if (!r.TryFill(magic) || !magic.SequenceEqual("GGUF"u8))
        {
            throw new GgufFormatException("This is not a GGUF model file. Pick a file that ends in .gguf.");
        }

        var version = r.U32();
        if (version is < 2 or > 3)
        {
            throw new GgufFormatException($"This GGUF file uses format version {version}, which the host cannot read.");
        }

        var tensors = r.U64();
        var count = r.U64();
        if (tensors == 0 || count > MaxMetadataCount)
        {
            throw new GgufFormatException("The GGUF header looks damaged.");
        }

        string? name = null, arch = null;
        long? context = null;
        var candidates = new Dictionary<string, long>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            var key = r.Str(MaxKeyBytes);
            var type = r.U32();
            var wantsString = key is "general.name" or "general.architecture";
            var wantsNumber = key.EndsWith(".context_length", StringComparison.Ordinal);
            if (wantsString && type == 8)
            {
                var v = r.Str(MaxStringBytes);
                if (key == "general.name")
                {
                    name = v.Trim();
                }
                else
                {
                    arch = v.Trim();
                }
            }
            else if (wantsNumber && IsInteger(type))
            {
                var v = r.Integer(type);
                candidates[key] = v; // resolved against the architecture once it is known
                if (arch is not null && candidates.TryGetValue(arch + ".context_length", out var exact))
                {
                    context = exact;
                }
            }
            else
            {
                r.Skip(type);
            }

            if (arch is not null && candidates.TryGetValue(arch + ".context_length", out var found))
            {
                context = found;
            }

            if (name is not null && arch is not null && context is not null)
            {
                break;
            }
        }

        return new GgufInfo(version, tensors, count, string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(arch) ? null : arch, context);
    }

    private static bool IsInteger(uint type) => type is <= 5 or 10 or 11;

    private sealed class Cursor(Stream s)
    {
        public bool TryFill(Span<byte> buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = s.Read(buffer[read..]);
                if (n == 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }

        private void Fill(Span<byte> buffer)
        {
            if (!TryFill(buffer))
            {
                throw new GgufFormatException("The GGUF file is cut short: its header ends too early.");
            }
        }

        public uint U32()
        {
            Span<byte> b = stackalloc byte[4];
            Fill(b);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        public ulong U64()
        {
            Span<byte> b = stackalloc byte[8];
            Fill(b);
            return BinaryPrimitives.ReadUInt64LittleEndian(b);
        }

        public string Str(int maxBytes)
        {
            var len = U64();
            if (len > (ulong)maxBytes)
            {
                throw new GgufFormatException("The GGUF header looks damaged (a value is far too long).");
            }

            var bytes = new byte[(int)len];
            Fill(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        public long Integer(uint type)
        {
            switch (type)
            {
                case 0: return (long)Fixed(1);
                case 1: return (sbyte)Fixed(1);
                case 2: return (ushort)Fixed(2);
                case 3: return (short)Fixed(2);
                case 4: return (uint)Fixed(4);
                case 5: return (int)Fixed(4);
                default: return (long)Fixed(8);
            }
        }

        private ulong Fixed(int size)
        {
            Span<byte> b = stackalloc byte[8];
            b.Clear();
            Fill(b[..size]);
            return BinaryPrimitives.ReadUInt64LittleEndian(b);
        }

        private static int FixedSize(uint type) => type switch
        {
            0 or 1 or 7 => 1,
            2 or 3 => 2,
            4 or 5 or 6 => 4,
            10 or 11 or 12 => 8,
            _ => 0,
        };

        /// <summary>Steps over one value of the given type without keeping it.</summary>
        public void Skip(uint type, int depth = 0)
        {
            if (type == 8)
            {
                SkipBytes(U64());
                return;
            }

            if (type == 9)
            {
                if (depth > 0)
                {
                    throw new GgufFormatException("The GGUF header looks damaged (arrays inside arrays).");
                }

                var elem = U32();
                var n = U64();
                var size = FixedSize(elem);
                if (size > 0)
                {
                    if (n > long.MaxValue / 8)
                    {
                        throw new GgufFormatException("The GGUF header looks damaged.");
                    }

                    SkipBytes(n * (ulong)size);
                }
                else if (elem == 8)
                {
                    for (ulong i = 0; i < n; i++)
                    {
                        SkipBytes(U64());
                    }
                }
                else
                {
                    throw new GgufFormatException("The GGUF header uses a value type the host does not know.");
                }

                return;
            }

            var fixedSize = FixedSize(type);
            if (fixedSize == 0)
            {
                throw new GgufFormatException("The GGUF header uses a value type the host does not know.");
            }

            SkipBytes((ulong)fixedSize);
        }

        private void SkipBytes(ulong count)
        {
            if (count == 0)
            {
                return;
            }

            if (s.CanSeek)
            {
                if (count > (ulong)(s.Length - s.Position))
                {
                    throw new GgufFormatException("The GGUF file is cut short: its header ends too early.");
                }

                s.Seek((long)count, SeekOrigin.Current);
                return;
            }

            var scratch = new byte[8192];
            while (count > 0)
            {
                var step = (int)Math.Min((ulong)scratch.Length, count);
                Fill(scratch.AsSpan(0, step));
                count -= (ulong)step;
            }
        }
    }
}
