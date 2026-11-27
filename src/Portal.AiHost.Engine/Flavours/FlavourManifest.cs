using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Portal.AiHost.Engine.Flavours;

/// <summary>One file of an engine flavour, with the size and SHA-256 it must have after extraction.</summary>
public sealed class FlavourFile
{
    /// <summary>Path inside the flavour folder, with forward slashes (for example "llama-server.exe").</summary>
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>Range of NVIDIA compute capabilities a build supports ("5.0" to "12.0"); informational for the selector.</summary>
public sealed class ComputeCapabilityRange
{
    public string? Min { get; init; }
    public string? Max { get; init; }
}

/// <summary>
/// One downloadable engine build (for example the llama.cpp CUDA 12 build). Pinned in the signed package
/// (engine\flavours.json), so its URLs and hashes change only with an app update.
/// </summary>
public sealed class FlavourEntry
{
    /// <summary>Flavour id: llama-cuda12, sd-cuda12, cudart12, upscale-best.</summary>
    public string Id { get; init; } = "";

    /// <summary>Engine the flavour belongs to (llama, sdcpp, cudart, upscale).</summary>
    public string Engine { get; init; } = "";
    public string Arch { get; init; } = "x64";
    public string Tag { get; init; } = "";
    public string Asset { get; init; } = "";

    /// <summary>Pinned download URLs, tried in order (upstream first, the project's own release copy second).</summary>
    public List<string> Urls { get; init; } = [];
    public string Sha256 { get; init; } = "";

    /// <summary>Size of the zip in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Total size of the extracted files in bytes.</summary>
    public long UnpackedSize { get; init; }
    public List<FlavourFile> Files { get; init; } = [];
    public string? MinDriver { get; init; }
    public ComputeCapabilityRange? ComputeCapability { get; init; }
    public string? Licence { get; init; }

    /// <summary>Heading in the third-party notices that the user is shown before the first download.</summary>
    public string? Notice { get; init; }

    /// <summary>Flavours that must be installed first (llama-cuda12 requires cudart12).</summary>
    public List<string> Requires { get; init; } = [];

    /// <summary>True when the build script found placeholder values; such entries are never installed.</summary>
    public bool Placeholder { get; init; }

    /// <summary>Folder name of this pinned version: "&lt;tag&gt;-&lt;first 8 of sha256&gt;".</summary>
    [JsonIgnore]
    public string VersionName => Tag + "-" + Sha256[..Math.Min(8, Sha256.Length)].ToLowerInvariant();
}

public sealed class FlavourManifestFile
{
    public int Schema { get; init; } = 1;
    public List<FlavourEntry> Flavours { get; init; } = [];
}

/// <summary>Reads and checks the flavour manifest that the package carries.</summary>
public static partial class FlavourManifest
{
    public const string FileName = "flavours.json";
    public const int SupportedSchema = 1;

    /// <summary>Path of the manifest inside the package: engine\flavours.json beside the host.</summary>
    public static string PackagedPath(string baseDirectory) => System.IO.Path.Combine(baseDirectory, "engine", FileName);

    /// <summary>Loads a manifest. Returns an empty list when the file is missing; throws on a damaged or newer file.</summary>
    public static IReadOnlyList<FlavourEntry> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return Parse(File.ReadAllBytes(path));
    }

    public static IReadOnlyList<FlavourEntry> Parse(ReadOnlySpan<byte> json)
    {
        var file = JsonSerializer.Deserialize(json, FlavourJsonContext.Default.FlavourManifestFile)
            ?? throw new InvalidDataException("The engine flavour list is empty.");
        if (file.Schema != SupportedSchema)
        {
            throw new InvalidDataException($"The engine flavour list has schema {file.Schema}; this host reads {SupportedSchema}.");
        }

        return file.Flavours;
    }

    /// <summary>
    /// Why an entry cannot be installed, or null when it is complete. Placeholders, missing hashes, non-https URLs,
    /// unsafe file paths and file lists that do not add up are all refused.
    /// </summary>
    public static string? Problem(FlavourEntry e)
    {
        if (e.Placeholder || ContainsPlaceholder(e))
        {
            return "placeholder";
        }

        if (!IdPattern().IsMatch(e.Id))
        {
            return "invalid id";
        }

        if (!TagPattern().IsMatch(e.Tag))
        {
            return "invalid tag";
        }

        if (!IsSha256(e.Sha256))
        {
            return "invalid sha256";
        }

        if (e.Size <= 0 || e.UnpackedSize <= 0)
        {
            return "missing size";
        }

        if (e.Urls.Count == 0)
        {
            return "no download address";
        }

        foreach (var url in e.Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo))
            {
                return "download address is not https";
            }
        }

        if (e.Files.Count == 0)
        {
            return "empty file list";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in e.Files)
        {
            if (!IsSafeRelativePath(f.Path) || !seen.Add(f.Path))
            {
                return "unsafe or duplicate file path";
            }

            if (!IsSha256(f.Sha256) || f.Size < 0)
            {
                return "invalid file hash or size";
            }

            total += f.Size;
        }

        if (total > e.UnpackedSize)
        {
            return "file sizes exceed the unpacked size";
        }

        foreach (var r in e.Requires)
        {
            if (!IdPattern().IsMatch(r) || r == e.Id)
            {
                return "invalid requirement";
            }
        }

        return null;
    }

    /// <summary>The entry and its requirements, requirements first. Throws on a missing or circular requirement.</summary>
    public static IReadOnlyList<FlavourEntry> InstallOrder(IReadOnlyList<FlavourEntry> all, string id)
    {
        var order = new List<FlavourEntry>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string current)
        {
            if (order.Any(o => o.Id == current))
            {
                return;
            }

            if (!visiting.Add(current))
            {
                throw new InvalidDataException($"Engine flavour '{current}' requires itself.");
            }

            var entry = all.FirstOrDefault(f => f.Id == current)
                ?? throw new InvalidDataException($"Engine flavour '{current}' is not in the list.");
            foreach (var r in entry.Requires)
            {
                Visit(r);
            }

            order.Add(entry);
        }

        Visit(id);
        return order;
    }

    /// <summary>
    /// A relative path with forward slashes and plain segments only: no "..", no root, no drive, no ':' (alternate
    /// streams), no reserved device names, no trailing dots or spaces, and never the store's marker name.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/'))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || !SegmentPattern().IsMatch(segment) || IsReservedDeviceName(segment))
            {
                return false;
            }
        }

        var leaf = path[(path.LastIndexOf('/') + 1)..];
        return !leaf.Equals(FlavourStore.MarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && HexPattern().IsMatch(value);

    private static bool ContainsPlaceholder(FlavourEntry e)
    {
        static bool P(string? s) => s?.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) == true;
        return P(e.Id) || P(e.Tag) || P(e.Asset) || P(e.Sha256) || P(e.MinDriver) || e.Urls.Any(P)
            || e.Files.Any(f => P(f.Path) || P(f.Sha256));
    }

    private static bool IsReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToLowerInvariant();
        return stem is "con" or "prn" or "aux" or "nul"
            || (stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal))
                && stem[3] is >= '1' and <= '9');
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9 ._+()-]{0,99}$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexPattern();
}

internal sealed class FlavourMarker
{
    public string Kind { get; init; } = FlavourStore.MarkerKind;
    public string Flavour { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public DateTimeOffset Created { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(FlavourManifestFile))]
[JsonSerializable(typeof(FlavourMarker))]
internal sealed partial class FlavourJsonContext : JsonSerializerContext;
