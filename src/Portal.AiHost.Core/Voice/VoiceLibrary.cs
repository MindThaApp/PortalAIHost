using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Voice;

public sealed class VoiceLibraryOptions
{
    /// <summary>The voice engine wants samples at this rate: mono, 16-bit.</summary>
    public int SampleRate { get; init; } = 24000;

    public double MinSampleSeconds { get; init; } = 3;
    public double MaxSampleSeconds { get; init; } = VoiceLimits.SampleSeconds;
    public int MaxVoices { get; init; } = 50;
}

/// <summary>
/// The shared voice library, one folder per voice under a root the host owns. A voice folder holds exactly two files:
/// the converted sample (mono 16-bit WAV) and voice.json. The caller's original file is never kept, and its path is never
/// seen: samples arrive as bytes. Removing a voice deletes those two files and then its (empty) folder, only after the
/// folder is proven to be a direct child of the library root that the host created.
/// </summary>
public sealed partial class VoiceLibrary
{
    public const string SampleFileName = "sample.wav";
    public const string MetaFileName = "voice.json";
    public const int MaxNameLength = 64;

    private readonly string _root;
    private readonly VoiceLibraryOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public VoiceLibrary(string root, VoiceLibraryOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _options = options ?? new VoiceLibraryOptions();
        _time = time ?? TimeProvider.System;
    }

    public string Root => _root;

    public int SampleRate => _options.SampleRate;

    public double MaxSampleSeconds => _options.MaxSampleSeconds;

    [GeneratedRegex("^vc_[0-9a-f]{24}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Trims and checks a display name: 1 to 64 characters, no control characters.</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                $"name must be 1 to {MaxNameLength} characters without control characters.", "name");
        }

        return trimmed;
    }

    /// <summary>Converts the sample (WAV bytes, already decoded) and stores it as a new voice under a new unique id.</summary>
    public VoiceInfo Create(string name, string consent, string appId, byte[] wav)
    {
        var cleanName = NormalizeName(name);
        if (!VoiceConsent.IsValid(consent))
        {
            throw new ApiException(ErrorCodes.ConsentRequired,
                "consent must be \"own_voice\" or \"permission\": say whose voice this is.", "consent");
        }

        PcmAudio audio;
        try
        {
            audio = WavAudio.ConvertToMono(wav, _options.SampleRate);
        }
        catch (AudioFormatException ex)
        {
            throw new ApiException(ErrorCodes.UnsupportedAudio, ex.Message, "sample_base64");
        }

        var seconds = audio.Samples.Length / (double)_options.SampleRate;
        if (seconds < _options.MinSampleSeconds || seconds > _options.MaxSampleSeconds)
        {
            throw new ApiException(ErrorCodes.InvalidRequest,
                string.Create(CultureInfo.InvariantCulture,
                    $"The sample must be {_options.MinSampleSeconds:0} to {_options.MaxSampleSeconds:0} seconds long (10 to 30 works best)."),
                "sample_base64");
        }

        var sampleBytes = WavAudio.WritePcm16(audio);
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            if (ListCore().Count >= _options.MaxVoices)
            {
                throw new ApiException(ErrorCodes.InvalidRequest,
                    $"The library holds at most {_options.MaxVoices} voices. Remove one first.");
            }

            string id;
            string folder;
            do
            {
                id = TokenFormat.NewId("vc_", 12);
                folder = Path.Combine(_root, id);
            }
            while (Directory.Exists(folder) || File.Exists(folder));

            var info = new VoiceInfo
            {
                Id = id,
                Name = cleanName,
                Consent = consent,
                CreatedAt = _time.GetUtcNow(),
                CreatedBy = appId,
                DurationMs = audio.DurationMs,
                SampleRate = audio.SampleRate,
            };
            Directory.CreateDirectory(folder); // New folder, made by the host: the only place this call writes.
            try
            {
                AtomicFile.WriteAllBytes(Path.Combine(folder, SampleFileName), sampleBytes);
                WriteMeta(folder, info);
            }
            catch
            {
                RemoveFolderQuietly(folder);
                throw;
            }

            return info;
        }
    }

    public IReadOnlyList<VoiceInfo> List()
    {
        lock (_gate)
        {
            return ListCore();
        }
    }

    public VoiceInfo? Get(string id)
    {
        lock (_gate)
        {
            return ReadMeta(FolderFor(id));
        }
    }

    /// <summary>Full path of a voice's sample, for the engine to read. Null when the voice is unknown.</summary>
    public string? SamplePath(string id)
    {
        lock (_gate)
        {
            var folder = FolderFor(id);
            var path = Path.Combine(folder, SampleFileName);
            return ReadMeta(folder) is not null && File.Exists(path) ? path : null;
        }
    }

    public VoiceInfo? Rename(string id, string newName)
    {
        var clean = NormalizeName(newName);
        lock (_gate)
        {
            var folder = FolderFor(id);
            var info = ReadMeta(folder);
            if (info is null)
            {
                return null;
            }

            info.Name = clean;
            WriteMeta(folder, info);
            return info;
        }
    }

    /// <summary>
    /// Deletes one voice. Only that voice's own files go: the sample, voice.json and then its folder, which must be a direct
    /// child of the library root and carry a voice.json (proof the host made it). Nothing is removed recursively.
    /// Returns false when the voice does not exist.
    /// </summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            var folder = FolderFor(id);
            if (ReadMeta(folder) is null)
            {
                return false;
            }

            DeleteOwnFiles(folder);
            return true;
        }
    }

    // ---- internals -----------------------------------------------------------------------------

    /// <summary>The folder of an id. Throws unless the id has the exact shape the host generates and the folder is inside the root.</summary>
    internal string FolderFor(string id)
    {
        if (!IsValidId(id))
        {
            throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.");
        }

        var folder = Path.GetFullPath(Path.Combine(_root, id));
        if (!string.Equals(Path.GetDirectoryName(folder), _root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.VoiceNotFound, "Unknown voice.");
        }

        return folder;
    }

    private List<VoiceInfo> ListCore()
    {
        var list = new List<VoiceInfo>();
        if (!Directory.Exists(_root))
        {
            return list;
        }

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (!IsValidId(name))
            {
                continue; // Not made by the host: left alone.
            }

            if (ReadMeta(dir) is { } info)
            {
                list.Add(info);
            }
        }

        list.Sort((a, b) => a.CreatedAt != b.CreatedAt ? a.CreatedAt.CompareTo(b.CreatedAt) : string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    private static VoiceInfo? ReadMeta(string folder)
    {
        var file = Path.Combine(folder, MetaFileName);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(file), AiHostJsonContext.Default.VoiceInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteMeta(string folder, VoiceInfo info) =>
        AtomicFile.WriteAllBytes(Path.Combine(folder, MetaFileName),
            JsonSerializer.SerializeToUtf8Bytes(info, AiHostJsonContext.Default.VoiceInfo));

    /// <summary>Removes a folder this class just created after a failed write.</summary>
    private void RemoveFolderQuietly(string folder)
    {
        try
        {
            DeleteOwnFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DeleteOwnFiles(string folder)
    {
        // Re-check the path right before deleting, whatever the caller did.
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), _root, StringComparison.OrdinalIgnoreCase) ||
            !IsValidId(Path.GetFileName(full)))
        {
            throw new InvalidOperationException("Refusing to delete outside the voice library.");
        }

        if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Refusing to delete through a link.");
        }

        foreach (var known in new[] { SampleFileName, MetaFileName })
        {
            var file = Path.Combine(full, known);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        // Also remove leftovers of this class's own atomic writes (unique temp names with the .pah-tmp suffix).
        foreach (var temp in Directory.EnumerateFiles(full, "*" + AtomicFile.TempSuffix))
        {
            File.Delete(temp);
        }

        // Non-recursive: if anything else is in there, this fails and the folder stays.
        Directory.Delete(full, recursive: false);
    }
}
