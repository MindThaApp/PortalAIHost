using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Planning;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Settings;

public sealed class PerformanceSettingsStoreTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pah-core-tests", Guid.NewGuid().ToString("N"));

    public PerformanceSettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            File.Delete(Path.Combine(_dir, HostSettingsFile.FileName));
            Directory.Delete(_dir, recursive: false);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void NewFieldsSaveAndLoadThroughTheSettingsStore()
    {
        var store = HostSettingsFile.Create(_dir);
        store.Save(new HostSettings
        {
            Preset = PerformancePresets.Custom, MemoryLimitBytes = 20 * GiB, KeepFreeBytes = 6 * GiB, Compute = "cpu",
            GpuMemoryLimitBytes = 4 * GiB, CpuThreads = 5, Priority = PerformancePriorities.Normal,
        });

        var loaded = HostSettingsFile.Create(_dir).Load();
        Assert.Equal("custom", loaded.Preset);
        Assert.Equal(20 * GiB, loaded.MemoryLimitBytes);
        Assert.Equal(6 * GiB, loaded.KeepFreeBytes);
        Assert.Equal("cpu", loaded.Compute);
        Assert.Equal(4 * GiB, loaded.GpuMemoryLimitBytes);
        Assert.Equal(5, loaded.CpuThreads);
        Assert.Equal("normal", loaded.Priority);
    }

    [Fact]
    public void AnOlderFileWithOnlyAMemoryShareMapsToBytesOnFirstRead()
    {
        File.WriteAllText(Path.Combine(_dir, HostSettingsFile.FileName),
            "{\"schema_version\":1,\"settings\":{\"max_memory_percent\":50,\"backend\":\"auto\"}}");
        var loaded = HostSettingsFile.Create(_dir).Load();
        Assert.Null(loaded.MemoryLimitBytes);
        Assert.Equal("balanced", loaded.Preset);

        var hw = new HardwareProfile("windows", "x64", 8, 32 * GiB, [], [], "fp");
        var limits = PerformanceModel.Resolve(loaded, hw);
        Assert.Equal(16 * GiB, limits.MemoryLimitBytes);
    }
}
