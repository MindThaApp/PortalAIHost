using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Platform.Windows.Hardware;
using Portal.AiHost.Platform.Windows.Power;

namespace Portal.AiHost.Platform.Windows.Tests;

public class HardwareProbeTests
{
    [Fact]
    public async Task ProbeReportsRamCoresAndDrives()
    {
        var probe = new WindowsHardwareProbe();
        var hw = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal("windows", hw.Os);
        Assert.True(hw.RamBytes > 1L << 30);
        Assert.True(hw.CpuCores >= 1);
        Assert.NotEmpty(hw.Drives);
        Assert.All(hw.Gpus, g => Assert.NotEqual("microsoft", g.Vendor));
        Assert.Equal(16, hw.Fingerprint.Length);
    }

    [Fact]
    public async Task FingerprintIsStableAcrossProbes()
    {
        var probe = new WindowsHardwareProbe();
        var a = await probe.ProbeAsync(CancellationToken.None);
        var b = await probe.ProbeAsync(CancellationToken.None);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void FingerprintChangesWithVramOrDriver()
    {
        var gpu = new GpuProfile("Radeon RX 6600", "amd", "0x1", "31.0.1", 8L << 30, 7L << 30);
        var baseFp = WindowsHardwareProbe.Fingerprint(64L << 30, 16, "x64", [gpu]);
        Assert.NotEqual(baseFp, WindowsHardwareProbe.Fingerprint(64L << 30, 16, "x64", [gpu with { Driver = "32.0.1" }]));
        Assert.NotEqual(baseFp, WindowsHardwareProbe.Fingerprint(128L << 30, 16, "x64", [gpu]));
        Assert.Equal(baseFp, WindowsHardwareProbe.Fingerprint((64L << 30) - (100L << 20), 16, "x64", [gpu]));
    }

    [Fact]
    public async Task LiveMemoryMatchesGpuList()
    {
        var probe = new WindowsHardwareProbe();
        var hw = await probe.ProbeAsync(CancellationToken.None);
        var mem = probe.GetMemory();
        Assert.InRange(mem.AvailableRamBytes, 1, mem.TotalRamBytes);
        Assert.Equal(hw.Gpus.Count, mem.FreeVramBytesPerGpu.Length);
    }

    [Fact]
    public void PowerCallsWork()
    {
        _ = PowerStatus.IsOnBattery();
        var pct = PowerStatus.BatteryPercent();
        Assert.True(pct is null or >= 0 and <= 100);

        var blocker = new PowerRequestSleepBlocker();
        var request = blocker.Acquire("Portal AI Host test");
        Assert.True(PowerRequestSleepBlocker.IsActive(request));
        request.Dispose();
        Assert.False(PowerRequestSleepBlocker.IsActive(request));
    }
}
