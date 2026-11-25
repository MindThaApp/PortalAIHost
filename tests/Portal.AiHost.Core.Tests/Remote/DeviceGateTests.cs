using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Remote.Client;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class DeviceGateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly DeviceStore _devices;
    private readonly DeviceStatsStore _stats;
    private readonly FakeBusy _busy = new();
    private bool _gaming;
    private readonly DeviceGate _gate;
    private readonly List<DeviceAlert> _alerts = [];

    private sealed class FakeBusy : IBusyDetector
    {
        public bool Busy;
        public bool IsBusy() => Busy;
    }

    public DeviceGateTests()
    {
        _devices = new DeviceStore(_dir.Path, _time);
        _stats = new DeviceStatsStore(_dir.Path, id => _devices.Find(id) is not null, _time, TimeZoneInfo.Utc);
        _gate = new DeviceGate(_devices, _stats, _time, TimeZoneInfo.Utc, () => _gaming, _busy);
        _gate.Alert += _alerts.Add;
    }

    public void Dispose() => _dir.Dispose();

    private PairedDevice NewDevice(string name = "Laptop") =>
        _devices.Add(name, System.Security.Cryptography.RandomNumberGenerator.GetBytes(91));

    private PairedDevice Fresh(PairedDevice d) => _devices.Find(d.Id)!;

    [Theory]
    [InlineData(null, "Unknown app")]
    [InlineData("", "Unknown app")]
    [InlineData("  \t ", "Unknown app")]
    [InlineData("Notes\r\nApp\u0007", "NotesApp")]
    public void AppNamesAreCleanedLabels(string? raw, string expected) =>
        Assert.Equal(expected, RemoteBackend.SanitizeAppName(raw));

    [Fact]
    public void AppNamesAreCutToSixtyFourCharacters() =>
        Assert.Equal(64, RemoteBackend.SanitizeAppName(new string('a', 200)).Length);

    [Fact]
    public void ADeviceIsLetInByDefault() => Assert.Null(_gate.Check(NewDevice(), "Notes"));

    [Fact]
    public void ADevicePauseRefusesEveryAppAndResumeLetsThemIn()
    {
        var d = NewDevice();
        _devices.Update(d.Id, x => x.Paused = true);
        var refusal = _gate.Check(Fresh(d), "Notes");
        Assert.Equal(ErrorCodes.DevicePaused, refusal!.Code);
        Assert.True(refusal.RetryAfterMs > 0);
        Assert.Equal(ErrorCodes.DevicePaused, _gate.Check(Fresh(d), "Mail")!.Code);
        _devices.Update(d.Id, x => x.Paused = false);
        Assert.Null(_gate.Check(Fresh(d), "Notes"));
    }

    [Fact]
    public void AnAppPauseRefusesOnlyThatApp()
    {
        var d = NewDevice();
        _devices.Update(d.Id, x => x.PausedApps.Add("Notes"));
        Assert.Equal(ErrorCodes.DevicePaused, _gate.Check(Fresh(d), "Notes")!.Code);
        Assert.Null(_gate.Check(Fresh(d), "Mail"));
    }

    [Theory]
    [InlineData("09:00", "17:00", 12, 0, true)]
    [InlineData("09:00", "17:00", 17, 0, false)] // the end is outside the window
    [InlineData("09:00", "17:00", 8, 59, false)]
    [InlineData("22:00", "07:00", 23, 30, true)] // across midnight
    [InlineData("22:00", "07:00", 3, 0, true)]
    [InlineData("22:00", "07:00", 12, 0, false)]
    [InlineData("10:00", "10:00", 10, 0, false)] // equal times: no window
    public void QuietHoursStartInclusiveEndExclusiveAndCrossMidnight(string start, string end, int hour, int minute, bool refused)
    {
        _time.Advance(new DateTimeOffset(2026, 11, 20, hour, minute, 0, TimeSpan.Zero) - _time.GetUtcNow());
        var d = NewDevice();
        _devices.Update(d.Id, x => { x.QuietStart = start; x.QuietEnd = end; });
        var refusal = _gate.Check(Fresh(d), "Notes");
        Assert.Equal(refused, refusal is not null);
        if (refusal is not null)
        {
            Assert.Equal(ErrorCodes.DeviceQuietHours, refusal.Code);
            Assert.True(refusal.RetryAfterMs > 0);
        }
    }

    [Fact]
    public void QuietHoursRetryAfterIsTheTimeUntilTheyEnd()
    {
        _time.Advance(new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero) - _time.GetUtcNow());
        var d = NewDevice();
        _devices.Update(d.Id, x => { x.QuietStart = "22:00"; x.QuietEnd = "07:00"; });
        Assert.Equal((int)TimeSpan.FromHours(8).TotalMilliseconds, _gate.Check(Fresh(d), "Notes")!.RetryAfterMs);
    }

    [Fact]
    public void TheRequestLimitRefusesUntilMidnight()
    {
        var d = NewDevice();
        _devices.Update(d.Id, x => x.MaxRequestsPerDay = 2);
        for (var i = 0; i < 2; i++)
        {
            Assert.Null(_gate.Check(Fresh(d), "Notes"));
            using var call = _gate.Begin(Fresh(d), "Notes", viaRelay: false, counts: true);
            call.Complete(5, null, null, null);
        }

        var refusal = _gate.Check(Fresh(d), "Notes");
        Assert.Equal(ErrorCodes.DeviceLimitReached, refusal!.Code);
        Assert.Equal((int)TimeSpan.FromHours(12).TotalMilliseconds, refusal.RetryAfterMs); // 12:00 to midnight
        _time.Advance(TimeSpan.FromHours(12));
        Assert.Null(_gate.Check(Fresh(d), "Notes")); // a new day
    }

    [Fact]
    public void TheTokenLimitRefuses()
    {
        var d = NewDevice();
        _devices.Update(d.Id, x => x.MaxTokensPerDay = 100);
        using (var call = _gate.Begin(Fresh(d), "Notes", false, true))
        {
            call.Complete(99, null, null, null);
        }

        Assert.Null(_gate.Check(Fresh(d), "Notes"));
        using (var call = _gate.Begin(Fresh(d), "Notes", false, true))
        {
            call.Complete(1, null, null, null);
        }

        Assert.Equal(ErrorCodes.DeviceLimitReached, _gate.Check(Fresh(d), "Notes")!.Code);
    }

    [Fact]
    public void GamingModeRefusesOnlyWhileOnAndBusy()
    {
        var d = NewDevice();
        _busy.Busy = true;
        Assert.Null(_gate.Check(d, "Notes")); // switch off: a busy PC is fine
        _gaming = true;
        Assert.Equal(ErrorCodes.HostBusyGaming, _gate.Check(d, "Notes")!.Code);
        Assert.True(_gate.GamingBusyNow);
        _busy.Busy = false;
        Assert.Null(_gate.Check(d, "Notes"));
        Assert.False(_gate.GamingBusyNow);
    }

    [Fact]
    public void StatsKeepSevenDaysAndDropOlderOnes()
    {
        var d = NewDevice();
        for (var day = 0; day < 9; day++)
        {
            using var call = _gate.Begin(Fresh(d), "Notes", false, true);
            call.Complete(10, null, "m", "M");
            _time.Advance(TimeSpan.FromDays(1));
        }

        var raw = File.ReadAllText(_stats.PathOf(d.Id)!);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(raw, "\"2026-11-\\d\\d\"").Count);
        var bars = _stats.View(d.Id).Last7Days["Notes"];
        Assert.Equal(7, bars.Length);
        Assert.Equal(0, bars[6]); // today has no call yet
        Assert.Equal(1, bars[5]);
    }

    [Fact]
    public void ForgetDeletesOnlyThatDevicesStatsFile()
    {
        var a = NewDevice("A");
        var b = NewDevice("B");
        foreach (var d in new[] { a, b })
        {
            using var call = _gate.Begin(Fresh(d), "Notes", false, true);
            call.Complete(5, null, null, null);
        }

        var other = Path.Combine(_dir.Path, "device-stats", "notes.txt");
        File.WriteAllText(other, "keep");
        var devicesFile = Path.Combine(_dir.Path, DeviceStore.FileName);
        Assert.True(File.Exists(_stats.PathOf(a.Id)!));
        _devices.Remove(a.Id);
        Assert.True(_stats.Delete(a.Id));
        Assert.False(File.Exists(_stats.PathOf(a.Id)!));
        Assert.True(File.Exists(_stats.PathOf(b.Id)!));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(devicesFile));
        Assert.Null(_stats.PathOf("../dev_x")); // an id never leaves the stats folder
        Assert.False(_stats.Delete("../devices"));
        Assert.True(File.Exists(devicesFile));
    }

    [Fact]
    public void AFinishedCallOfAForgottenDeviceWritesNothing()
    {
        var d = NewDevice();
        var call = _gate.Begin(Fresh(d), "Notes", false, true);
        _devices.Remove(d.Id);
        _stats.Delete(d.Id);
        call.Complete(5, null, null, null);
        call.Dispose();
        Assert.False(File.Exists(_stats.PathOf(d.Id)!));
    }

    [Fact]
    public void StatsHoldCountsOnly()
    {
        var d = NewDevice();
        using (var call = _gate.Begin(Fresh(d), "Notes", false, true))
        {
            call.Complete(5, null, "m1", "Model One");
        }

        var raw = File.ReadAllText(_stats.PathOf(d.Id)!);
        Assert.Contains("Notes", raw);
        Assert.DoesNotContain("prompt", raw);
        Assert.Equal(1, _stats.TodayTotal(d.Id).Requests);
        Assert.Equal(5, _stats.TodayTotal(d.Id).Tokens);
    }

    [Fact]
    public void RenameTrimsCleansAndRejectsEmpty()
    {
        var d = NewDevice("Old");
        Assert.True(_devices.Rename(d.Id, "  Kitchen\u0007 laptop "));
        Assert.Equal("Kitchen laptop", Fresh(d).Name);
        Assert.False(_devices.Rename(d.Id, "   "));
        Assert.Equal("Kitchen laptop", Fresh(d).Name);
        Assert.False(_devices.Rename("dev_missing", "x"));
        Assert.Equal(64, DeviceStore.CleanName(new string('n', 100)).Length);
    }

    [Fact]
    public void SettingsPersistAcrossARestart()
    {
        var d = NewDevice();
        _devices.Update(d.Id, x => { x.Paused = true; x.PausedApps.Add("Mail"); x.AllowedModels.Add("m1"); x.MaxTokensPerDay = 7; });
        var again = new DeviceStore(_dir.Path, _time).Find(d.Id)!;
        Assert.True(again.Paused);
        Assert.Equal(["Mail"], again.PausedApps);
        Assert.Equal(["m1"], again.AllowedModels);
        Assert.Equal(7, again.MaxTokensPerDay);
    }

    [Fact]
    public void ActiveCallsShowQueueAndStatus()
    {
        var d = NewDevice();
        Assert.Equal(DeviceStatuses.Offline, _gate.StatusOf(Fresh(d)));
        using var first = _gate.Begin(Fresh(d), "Notes", false, true);
        using var second = _gate.Begin(Fresh(d), "Notes", false, true);
        var apps = _gate.ActiveApps(d.Id);
        Assert.Equal(("Notes", 2, 1), apps.Single());
        Assert.Equal(DeviceStatuses.Online, _gate.StatusOf(Fresh(d)));
        second.Dispose();
        first.Dispose();
        Assert.Empty(_gate.ActiveApps(d.Id));
    }

    [Fact]
    public void AlertsFireForANewPairingAndOnceForARelaySession()
    {
        var d = NewDevice("Phone");
        Assert.Equal(["paired"], _alerts.Select(a => a.Kind));
        _gate.NoteArrival(d, viaRelay: false);
        _gate.NoteArrival(d, viaRelay: true);
        _gate.NoteArrival(d, viaRelay: true);
        Assert.Equal(["paired", "relay_connect"], _alerts.Select(a => a.Kind));
        _time.Advance(TimeSpan.FromHours(1));
        _gate.NoteArrival(d, viaRelay: true);
        Assert.Equal(3, _alerts.Count);
    }
}
