using PortalAIHost.App.Services;

namespace PortalAIHost.App.Tests;

public class AppStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PortalAIHost.App.Tests-" + Guid.NewGuid().ToString("N"));

    public AppStateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only the folder this test created is removed.
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Remembers_first_run_and_writes_only_its_own_file()
    {
        var other = Path.Combine(_dir, "user-file.txt");
        File.WriteAllText(other, "keep me");
        var store = new AppStateStore(_dir);
        Assert.False(store.FirstRunDone);
        store.MarkFirstRunDone();
        Assert.True(new AppStateStore(_dir).FirstRunDone);
        Assert.Equal("keep me", File.ReadAllText(other));
        Assert.Equal(new[] { AppStateStore.FileName, "user-file.txt" }, Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void A_damaged_file_reads_as_not_done_and_is_not_deleted()
    {
        var path = Path.Combine(_dir, AppStateStore.FileName);
        File.WriteAllText(path, "{not json");
        Assert.False(new AppStateStore(_dir).FirstRunDone);
        Assert.Equal("{not json", File.ReadAllText(path));
    }

    [Fact]
    public void A_folder_that_cannot_be_created_does_not_throw()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var store = new AppStateStore(Path.Combine(blocker, "sub"));
        store.MarkFirstRunDone();
        Assert.True(store.FirstRunDone);
        Assert.Equal("x", File.ReadAllText(blocker));
    }
}

public class TextTests
{
    [Fact]
    public void Pseudo_loc_keeps_placeholders_and_grows_text()
    {
        var p = PseudoLoc.Convert("Allow {0} now");
        Assert.Contains("{0}", p);
        Assert.StartsWith("[", p);
        Assert.EndsWith("]", p);
        Assert.True(p.Length > "Allow {0} now".Length * 1.3);
        Assert.DoesNotContain("Allow", p);
    }

    [Fact]
    public void Sizes_and_stars_read_plainly()
    {
        Assert.Equal("9 GB", Fmt.Bytes(9L * 1024 * 1024 * 1024));
        Assert.Equal("100 MB", Fmt.Bytes(100L * 1024 * 1024));
        Assert.Equal("★★★★☆", Fmt.Stars(4.2));
    }
}
