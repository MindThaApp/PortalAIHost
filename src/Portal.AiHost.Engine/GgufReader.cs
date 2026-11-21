using System.Text;

namespace Portal.AiHost.Engine;

/// <summary>The few GGUF header fields the planner needs.</summary>
public sealed record GgufInfo(
    uint Version,
    string? Architecture,
    long? BlockCount,
    long? ContextLength,
    long? ExpertCount);

/// <summary>
/// Minimal GGUF metadata reader. Reads only the header key/value section, stops as soon as the
/// tokenizer section starts, and never writes. For split models pass the first part.
/// </summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"

    public static GgufInfo Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        return Read(fs);
    }

    public static GgufInfo Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a GGUF file.");
        }

        var version = r.ReadUInt32();
        if (version < 2 || version > 3)
        {
            throw new InvalidDataException($"Unsupported GGUF version {version}.");
        }

        r.ReadUInt64(); // tensor count
        var kvCount = r.ReadUInt64();
        if (kvCount > 1_000_000)
        {
            throw new InvalidDataException("Implausible GGUF key count.");
        }

        string? arch = null;
        var ints = new Dictionary<string, long>(StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(r);
            var type = r.ReadUInt32();
            if (key.StartsWith("tokenizer.", StringComparison.Ordinal))
            {
                break; // large arrays follow; everything we need precedes them
            }

            if (type == 8)
            {
                var s = ReadString(r);
                if (key == "general.architecture")
                {
                    arch = s;
                }
            }
            else if (type == 9)
            {
                SkipArray(r);
            }
            else
            {
                var v = ReadScalar(r, type);
                if (v is long l && (key.EndsWith(".block_count", StringComparison.Ordinal)
                    || key.EndsWith(".context_length", StringComparison.Ordinal)
                    || key.EndsWith(".expert_count", StringComparison.Ordinal)))
                {
                    ints[key] = l;
                }
            }
        }

        long? Get(string suffix) => arch != null && ints.TryGetValue(arch + suffix, out var v) ? v : null;
        return new GgufInfo(version, arch, Get(".block_count"), Get(".context_length"), Get(".expert_count"));
    }

    private static string ReadString(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Implausible GGUF string length.");
        }

        var bytes = r.ReadBytes((int)len);
        if (bytes.Length != (int)len)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ScalarSize(uint type) => type switch
    {
        0 or 1 or 7 => 1,
        2 or 3 => 2,
        4 or 5 or 6 => 4,
        10 or 11 or 12 => 8,
        _ => throw new InvalidDataException($"Unknown GGUF value type {type}."),
    };

    private static object ReadScalar(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: return (long)r.ReadByte();
            case 1: return (long)r.ReadSByte();
            case 2: return (long)r.ReadUInt16();
            case 3: return (long)r.ReadInt16();
            case 4: return (long)r.ReadUInt32();
            case 5: return (long)r.ReadInt32();
            case 6: return (double)r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 10: return unchecked((long)r.ReadUInt64());
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            default: throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }

    private static void SkipArray(BinaryReader r)
    {
        var elemType = r.ReadUInt32();
        var count = r.ReadUInt64();
        if (elemType == 8)
        {
            for (ulong i = 0; i < count; i++)
            {
                var len = r.ReadUInt64();
                r.BaseStream.Seek((long)len, SeekOrigin.Current);
            }
        }
        else if (elemType == 9)
        {
            for (ulong i = 0; i < count; i++)
            {
                SkipArray(r);
            }
        }
        else
        {
            r.BaseStream.Seek((long)count * ScalarSize(elemType), SeekOrigin.Current);
        }
    }
}
