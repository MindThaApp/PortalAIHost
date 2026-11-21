namespace Portal.AiHost.Client.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class TokenStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pah-tok-" + Guid.NewGuid().ToString("N"));

    public TokenStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task Round_trip_encrypts_the_token_on_disk()
    {
        var store = new DpapiTokenStore(_folder, "com.test.app");
        await store.SaveAsync("pah_secret_value", default);
        Assert.Equal("pah_secret_value", await store.LoadAsync(default));
        var raw = await File.ReadAllBytesAsync(store.FilePath);
        Assert.DoesNotContain("pah_secret_value", System.Text.Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public async Task Save_replaces_the_apps_own_token_only()
    {
        var a = new DpapiTokenStore(_folder, "com.a");
        var b = new DpapiTokenStore(_folder, "com.b");
        await a.SaveAsync("one", default);
        await b.SaveAsync("keep", default);
        await a.SaveAsync("two", default);
        Assert.Equal("two", await a.LoadAsync(default));
        Assert.Equal("keep", await b.LoadAsync(default));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Delete_removes_only_this_apps_token_file()
    {
        var a = new DpapiTokenStore(_folder, "com.a");
        var b = new DpapiTokenStore(_folder, "com.b");
        await a.SaveAsync("one", default);
        await b.SaveAsync("keep", default);
        var other = Path.Combine(_folder, "data.db");
        await File.WriteAllTextAsync(other, "user data");
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));

        await a.DeleteAsync(default);

        Assert.False(File.Exists(a.FilePath));
        Assert.Equal("keep", await b.LoadAsync(default));
        Assert.Equal("user data", await File.ReadAllTextAsync(other));
        Assert.True(Directory.Exists(Path.Combine(_folder, "sub")));
        await a.DeleteAsync(default); // deleting twice is harmless
    }

    [Fact]
    public async Task Missing_or_corrupt_file_loads_as_null()
    {
        var store = new DpapiTokenStore(_folder, "com.a");
        Assert.Null(await store.LoadAsync(default));
        await File.WriteAllBytesAsync(store.FilePath, [1, 2, 3, 4]);
        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public void App_id_cannot_escape_the_folder()
    {
        var store = new DpapiTokenStore(_folder, "..\\..\\evil/app");
        Assert.Equal(Path.GetFullPath(_folder), Path.GetDirectoryName(store.FilePath));
    }
}
