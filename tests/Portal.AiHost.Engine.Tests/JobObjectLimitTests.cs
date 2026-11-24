using System.Diagnostics;
using Portal.AiHost.Engine;

namespace Portal.AiHost.Engine.Tests;

public class JobObjectLimitTests
{
    [Fact]
    public void MemoryLimitCanBeSetChangedAndRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var job = new JobObject();
        Assert.Null(job.MemoryLimitBytes);
        Assert.True(job.SetMemoryLimit(8L << 30));
        Assert.Equal(8L << 30, job.MemoryLimitBytes);
        Assert.True(job.SetMemoryLimit(4L << 30));
        Assert.Equal(4L << 30, job.MemoryLimitBytes);
        Assert.True(job.SetMemoryLimit(null));
        Assert.Null(job.MemoryLimitBytes);
    }

    [Fact]
    public void CpuRateCanBeSetChangedAndRemovedWithoutLosingTheMemoryCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var job = new JobObject();
        Assert.True(job.SetMemoryLimit(8L << 30));
        Assert.Null(job.CpuRate);
        Assert.True(job.SetCpuRate(5000));
        Assert.Equal(5000, job.CpuRate);
        Assert.True(job.SetCpuRate(2000));
        Assert.Equal(2000, job.CpuRate);
        Assert.True(job.SetCpuRate(null));
        Assert.Null(job.CpuRate);
        Assert.Equal(8L << 30, job.MemoryLimitBytes);
    }

    [Fact]
    public void AProcessCanJoinAJobThatHasALimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var job = new JobObject();
        Assert.True(job.SetMemoryLimit(2L << 30));
        var info = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var child = Process.Start(info)!;
        try
        {
            job.Assign(child);
            Assert.False(child.HasExited);
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }
}
