using System.Text.Json;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Voice;

/// <summary>
/// The voice limits of this PC, derived from its hardware tier. The sample length is the same everywhere (it is what the
/// model needs, not what the hardware can do). A 30 second sample must still fit the 4 MB request cap (base64 adds a
/// third), so a client that sends a 24 kHz mono 16-bit WAV (about 1.4 MB) is always safe; longer or higher-rate files may
/// need converting first.
/// </summary>
public sealed record VoiceLimits(string Tier, int MaxSampleSeconds, int MaxTextChars, int MaxJobsPerApp,
    double SpeechSecondsPer1000Chars, double ClonedSecondsPer1000Chars)
{
    public const string Low = "low";
    public const string Mid = "mid";
    public const string High = "high";
    public const int SampleSeconds = 30;

    public static VoiceLimits ForTier(string tier) => tier switch
    {
        Low => new(Low, SampleSeconds, 15_000, 4, 30, 60),
        High => new(High, SampleSeconds, 40_000, 12, 5, 10),
        _ => new(Mid, SampleSeconds, 20_000, 8, 12, 25),
    };

    /// <summary>Low: 4 cores or fewer, or under 8 GB. High: 12 cores or more and 32 GB or more. Everything else is mid.</summary>
    public static VoiceLimits ForHardware(int cpuCores, long ramBytes)
    {
        const long gb = 1024L * 1024 * 1024;
        if (cpuCores <= 4 || ramBytes < 8 * gb)
        {
            return ForTier(Low);
        }

        return cpuCores >= 12 && ramBytes >= 32 * gb ? ForTier(High) : ForTier(Mid);
    }

    public static VoiceLimits ForHardware(HardwareProfile? hardware) =>
        hardware is null ? ForTier(Mid) : ForHardware(hardware.CpuCores, hardware.RamBytes);
}

/// <summary>
/// How long reading takes per 1000 characters, for speech and for cloned speech. Starts from the hardware tier's rough guess
/// and switches to the median of the last 20 completed jobs once there are a few. Saved atomically so it survives restarts;
/// a missing or damaged file just means starting from the hardware guess again.
/// </summary>
public sealed class VoiceEstimator
{
    public const int Window = 20;
    public const int MinMeasured = 3;
    public const string Measured = "measured";
    public const string Hardware = "hardware";

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly VoiceLimits _limits;
    private readonly List<double> _speech = new();
    private readonly List<double> _cloned = new();

    public VoiceEstimator(VoiceLimits limits, string? path = null)
    {
        _limits = limits;
        _path = path;
        Load();
    }

    public (double SecondsPer1000, string Source) For(bool cloned)
    {
        lock (_gate)
        {
            var list = cloned ? _cloned : _speech;
            if (list.Count >= MinMeasured)
            {
                return (Median(list), Measured);
            }

            return (cloned ? _limits.ClonedSecondsPer1000Chars : _limits.SpeechSecondsPer1000Chars, Hardware);
        }
    }

    /// <summary>Remembers one completed job. Very short texts are ignored: their time is mostly start-up.</summary>
    public void Record(bool cloned, int chars, double seconds)
    {
        if (chars < 100 || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        lock (_gate)
        {
            var list = cloned ? _cloned : _speech;
            list.Add(seconds * 1000.0 / chars);
            while (list.Count > Window)
            {
                list.RemoveAt(0);
            }

            Save();
        }
    }

    private static double Median(List<double> values)
    {
        var s = values.Order().ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            Read(doc.RootElement, "speech", _speech);
            Read(doc.RootElement, "cloned", _cloned);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _speech.Clear();
            _cloned.Clear(); // A damaged file is ignored; the next job writes a fresh one.
        }
    }

    private static void Read(JsonElement root, string name, List<double> into)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var v) && v > 0 && double.IsFinite(v))
            {
                into.Add(v);
            }
        }

        if (into.Count > Window)
        {
            into.RemoveRange(0, into.Count - Window);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            AtomicFile.Write(_path, stream =>
            {
                using var w = new Utf8JsonWriter(stream);
                w.WriteStartObject();
                WriteList(w, "speech", _speech);
                WriteList(w, "cloned", _cloned);
                w.WriteEndObject();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The estimate is a convenience: failing to save it must never fail a job.
        }
    }

    private static void WriteList(Utf8JsonWriter w, string name, List<double> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteNumberValue(Math.Round(v, 3));
        }

        w.WriteEndArray();
    }
}
