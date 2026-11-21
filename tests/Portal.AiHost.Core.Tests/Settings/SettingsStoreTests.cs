using System.Text;
using System.Text.Json.Nodes;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Settings;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string[] FilesInDir() =>
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var store = HostSettingsFile.Create(_dir.Path);
        Assert.Equal(80, store.Load().MaxMemoryPercent);
        Assert.Equal(SettingsLoadState.Defaults, store.LoadState);

        HostSettings? changed = null;
        store.Changed += (_, s) => changed = s;
        store.Save(new HostSettings { MaxMemoryPercent = 60, Backend = "cpu" });
        Assert.Equal(60, changed!.MaxMemoryPercent);

        var reopened = HostSettingsFile.Create(_dir.Path);
        var loaded = reopened.Load();
        Assert.Equal(SettingsLoadState.Loaded, reopened.LoadState);
        Assert.Equal(60, loaded.MaxMemoryPercent);
        Assert.Equal("cpu", loaded.Backend);

        var json = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        Assert.Equal(1, json["schema_version"]!.GetValue<int>());
        Assert.Equal(60, json["settings"]!["max_memory_percent"]!.GetValue<int>());
    }

    [Fact]
    public void FailedWriteKeepsTheExistingFileAndLeavesNoTemp()
    {
        var target = _dir.File("state.json");
        File.WriteAllText(target, "original");
        File.WriteAllText(_dir.File("neighbour.txt"), "keep me");
        var before = FilesInDir();

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(target, stream =>
        {
            stream.Write("partial"u8);
            throw new InvalidOperationException("disk full");
        }));

        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal("keep me", File.ReadAllText(_dir.File("neighbour.txt")));
        Assert.Equal(before, FilesInDir());
    }

    [Fact]
    public void AtomicWriteReplacesOnlyTheTarget()
    {
        var target = _dir.File("state.json");
        File.WriteAllText(target, "old");
        File.WriteAllText(_dir.File("state.json.bak"), "user backup");
        File.WriteAllText(_dir.File("other.pah-tmp"), "not ours");
        var before = FilesInDir();

        AtomicFile.WriteAllBytes(target, "new"u8);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("user backup", File.ReadAllText(_dir.File("state.json.bak")));
        Assert.Equal("not ours", File.ReadAllText(_dir.File("other.pah-tmp")));
        Assert.Equal(before, FilesInDir());
    }

    [Fact]
    public void ReadOnlyTargetIsNotReplacedAndTempIsRemoved()
    {
        var target = _dir.File("locked.json");
        File.WriteAllText(target, "original");
        var before = FilesInDir();
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllBytes(target, "new"u8));
        }

        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(before, FilesInDir());
    }

    [Fact]
    public void UniquePathNeverReturnsAnExistingFile()
    {
        File.WriteAllText(_dir.File("a.bak"), "1");
        File.WriteAllText(_dir.File("a (2).bak"), "2");
        Assert.Equal(_dir.File("a (3).bak"), AtomicFile.UniquePath(_dir.Path, "a.bak"));

        var copy = AtomicFile.CopyToUniqueBackup(_dir.File("a.bak"), "a.bak");
        Assert.Equal(_dir.File("a (3).bak"), copy);
        Assert.Equal("1", File.ReadAllText(_dir.File("a.bak")));
        Assert.Equal("2", File.ReadAllText(_dir.File("a (2).bak")));
    }

    [Fact]
    public void CorruptFileIsBackedUpAndKeptUntilSaved()
    {
        var store = HostSettingsFile.Create(_dir.Path);
        File.WriteAllText(store.FilePath, "{ not json");

        store.Load();
        Assert.Equal(SettingsLoadState.Corrupt, store.LoadState);
        Assert.Equal("{ not json", File.ReadAllText(store.LastBackupPath!));
        Assert.Equal("{ not json", File.ReadAllText(store.FilePath));

        // A second corrupt load never overwrites the first backup.
        var first = store.LastBackupPath;
        store.Load();
        Assert.NotEqual(first, store.LastBackupPath);
        Assert.True(File.Exists(first));

        store.Save(new HostSettings());
        Assert.Equal(SettingsLoadState.Loaded, HostSettingsFile.Create(_dir.Path).Let(s => { s.Load(); return s.LoadState; }));
    }

    [Fact]
    public void NewerSchemaIsLeftUntouched()
    {
        var store = HostSettingsFile.Create(_dir.Path);
        const string newer = "{\"schema_version\":99,\"settings\":{\"future\":true}}";
        File.WriteAllText(store.FilePath, newer);

        store.Load();
        Assert.Equal(SettingsLoadState.NewerSchema, store.LoadState);
        Assert.Throws<InvalidOperationException>(() => store.Save(new HostSettings()));
        Assert.Equal(newer, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void MigrationBacksUpTheOldFileFirst()
    {
        var path = _dir.File("settings.json");
        const string v1 = "{\"schema_version\":1,\"settings\":{\"max_mem\":55}}";
        File.WriteAllText(path, v1);

        var store = new SettingsStore<HostSettings>(path,
            Portal.AiHost.Contracts.AiHostJsonContext.Default.HostSettings, 2, () => new HostSettings(), new RenameMigrator());
        var loaded = store.Load();

        Assert.Equal(SettingsLoadState.Migrated, store.LoadState);
        Assert.Equal(55, loaded.MaxMemoryPercent);
        Assert.Equal(v1, File.ReadAllText(store.LastBackupPath!, Encoding.UTF8));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["schema_version"]!.GetValue<int>());
    }

    private sealed class RenameMigrator : ISettingsMigrator
    {
        public JsonNode Migrate(int fromVersion, JsonNode settings)
        {
            var obj = settings.AsObject();
            var value = obj["max_mem"]!.GetValue<int>();
            obj.Remove("max_mem");
            obj["max_memory_percent"] = value;
            return obj;
        }
    }
}

internal static class LetExtensions
{
    public static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
