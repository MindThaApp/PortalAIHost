using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Remote;

/// <summary>Requests and tokens of one app on one day. Counts only: no prompt or answer text is ever stored.</summary>
public sealed class AppDayCounts
{
    public int Requests { get; set; }
    public long Tokens { get; set; }
}

/// <summary>The stats file of one device (device-stats\&lt;device id&gt;.json). Days are local dates, at most 7 are kept.</summary>
public sealed class DeviceStatsFile
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>"yyyy-MM-dd" to app label to counts.</summary>
    public Dictionary<string, Dictionary<string, AppDayCounts>> Days { get; set; } = [];

    public string? LastRoute { get; set; }
    public string? LastModel { get; set; }
    public string? LastModelName { get; set; }
    public double? LastTokensPerSecond { get; set; }
    public int? LastLatencyMs { get; set; }
    public DateTimeOffset? LastCall { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(DeviceStatsFile))]
internal sealed partial class DeviceStatsJsonContext : JsonSerializerContext
{
}

/// <summary>What the Devices view needs from the stats of one device.</summary>
public sealed record DeviceStatsView(
    IReadOnlyDictionary<string, (int Requests, long Tokens)> Today,
    IReadOnlyDictionary<string, int[]> Last7Days,
    string? LastRoute, string? LastModel, string? LastModelName, double? TokensPerSecond, int? LatencyMs);

/// <summary>
/// Usage stats, one small file per device under the host state folder (device-stats\). Seven days are kept: older days
/// are dropped whenever a day is added. Writes are atomic (temp file, then replace). <see cref="Delete"/> removes that
/// one device's file and nothing else; after it, nothing writes that device's file again because writes are skipped
/// for a device that is no longer paired.
/// </summary>
public sealed partial class DeviceStatsStore
{
    public const string FolderName = "device-stats";
    public const int DaysKept = 7;

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Func<string, bool> _isPaired;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceStatsFile> _cache = new(StringComparer.Ordinal);

    public DeviceStatsStore(string dataDirectory, Func<string, bool> isPaired, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(isPaired);
        _folder = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);
        _isPaired = isPaired;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    [GeneratedRegex("^dev_[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    /// <summary>The device's stats file. Only ids of the form dev_xxx map to a path, so no id can point elsewhere.</summary>
    public string? PathOf(string deviceId) =>
        IdPattern().IsMatch(deviceId ?? "") ? Path.Combine(_folder, deviceId + ".json") : null;

    /// <summary>Local calendar day on this PC (counts reset at local midnight).</summary>
    public DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime);

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Today's requests and tokens of a device, all apps together.</summary>
    public (int Requests, long Tokens) TodayTotal(string deviceId)
    {
        lock (_gate)
        {
            var file = Load(deviceId);
            if (file is null || !file.Days.TryGetValue(Key(Today()), out var apps))
            {
                return (0, 0);
            }

            return (apps.Values.Sum(a => a.Requests), apps.Values.Sum(a => a.Tokens));
        }
    }

    /// <summary>Counts a request when it is admitted (in memory; saved when the call ends).</summary>
    public void BeginRequest(string deviceId, string app)
    {
        lock (_gate)
        {
            var file = Load(deviceId);
            if (file is null)
            {
                return;
            }

            Day(file, app).Requests++;
        }
    }

    /// <summary>Adds the tokens of a finished call, remembers its route, model and speed, and saves.</summary>
    public void CompleteRequest(string deviceId, string app, long tokens, string route, string? model, string? modelName,
        double? tokensPerSecond, int? latencyMs)
    {
        lock (_gate)
        {
            var file = Load(deviceId);
            if (file is null || !_isPaired(deviceId))
            {
                return;
            }

            Day(file, app).Tokens += Math.Max(0, tokens);
            file.LastRoute = route;
            file.LastModel = model ?? file.LastModel;
            file.LastModelName = modelName ?? file.LastModelName;
            file.LastTokensPerSecond = tokensPerSecond ?? file.LastTokensPerSecond;
            file.LastLatencyMs = latencyMs ?? file.LastLatencyMs;
            file.LastCall = _time.GetUtcNow();
            Save(deviceId, file);
        }
    }

    public DeviceStatsView View(string deviceId)
    {
        lock (_gate)
        {
            var file = Load(deviceId);
            var today = Key(Today());
            var todayMap = new Dictionary<string, (int, long)>(StringComparer.Ordinal);
            var weeks = new Dictionary<string, int[]>(StringComparer.Ordinal);
            if (file is not null)
            {
                var start = Today().AddDays(-(DaysKept - 1));
                foreach (var (dayKey, apps) in file.Days)
                {
                    if (!DateOnly.TryParseExact(dayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                        day < start || day > Today())
                    {
                        continue;
                    }

                    foreach (var (app, c) in apps)
                    {
                        if (!weeks.TryGetValue(app, out var bars))
                        {
                            weeks[app] = bars = new int[DaysKept];
                        }

                        bars[day.DayNumber - start.DayNumber] = c.Requests;
                        if (dayKey == today)
                        {
                            todayMap[app] = (c.Requests, c.Tokens);
                        }
                    }
                }
            }

            return new DeviceStatsView(todayMap, weeks, file?.LastRoute, file?.LastModel, file?.LastModelName,
                file?.LastTokensPerSecond, file?.LastLatencyMs);
        }
    }

    /// <summary>
    /// Forget: deletes this device's stats file, and only that file. Returns false when there was none (or the id is
    /// not a device id).
    /// </summary>
    public bool Delete(string deviceId)
    {
        lock (_gate)
        {
            _cache.Remove(deviceId);
            var path = PathOf(deviceId);
            if (path is null || !File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    private AppDayCounts Day(DeviceStatsFile file, string app)
    {
        var key = Key(Today());
        if (!file.Days.TryGetValue(key, out var apps))
        {
            file.Days[key] = apps = new Dictionary<string, AppDayCounts>(StringComparer.Ordinal);
            Prune(file);
        }

        if (!apps.TryGetValue(app, out var counts))
        {
            apps[app] = counts = new AppDayCounts();
        }

        return counts;
    }

    /// <summary>Drops every day older than the 7 kept (today and the 6 before it) and any day that is not a date.</summary>
    private void Prune(DeviceStatsFile file)
    {
        var oldest = Today().AddDays(-(DaysKept - 1));
        foreach (var key in file.Days.Keys.ToList())
        {
            if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day < oldest)
            {
                file.Days.Remove(key);
            }
        }
    }

    private DeviceStatsFile? Load(string deviceId)
    {
        var path = PathOf(deviceId);
        if (path is null)
        {
            return null;
        }

        if (_cache.TryGetValue(deviceId, out var cached))
        {
            return cached;
        }

        var file = new DeviceStatsFile();
        try
        {
            if (File.Exists(path))
            {
                file = JsonSerializer.Deserialize(File.ReadAllBytes(path), DeviceStatsJsonContext.Default.DeviceStatsFile) ?? file;
                Prune(file);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable counts are disposable: start empty; the next save replaces the file atomically.
            file = new DeviceStatsFile();
        }

        _cache[deviceId] = file;
        return file;
    }

    private void Save(string deviceId, DeviceStatsFile file)
    {
        var path = PathOf(deviceId);
        if (path is null)
        {
            return;
        }

        try
        {
            AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, file, DeviceStatsJsonContext.Default.DeviceStatsFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stats are best effort; the counts stay in memory and the next call saves again.
        }
    }
}
