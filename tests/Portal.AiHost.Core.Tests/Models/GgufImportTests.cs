using System.Text;
using System.Text.Json;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Core.Tests.Models;

/// <summary>"Add your own model": GGUF validation, copy into the store, cancel cleanup, manifest field.</summary>
public class GgufImportTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A minimal GGUF v3 file: header, a few metadata values, then filler standing in for tensor data.</summary>
    internal static byte[] Gguf(string? name = "Test Model", string arch = "llama", long context = 8192, int filler = 4096, bool withTokens = true)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write("GGUF"u8.ToArray());
        w.Write(3u);
        w.Write(1UL); // tensors
        var kvCount = 2 + (name is null ? 0 : 1) + (withTokens ? 1 : 0);
        w.Write((ulong)kvCount);
        void Str(string v) { w.Write((ulong)Encoding.UTF8.GetByteCount(v)); w.Write(Encoding.UTF8.GetBytes(v)); }
        if (withTokens)
        {
            Str("tokenizer.ggml.tokens");
            w.Write(9u);
            w.Write(8u);
            w.Write(3UL);
            Str("a"); Str("bb"); Str("ccc");
        }

        Str("general.architecture"); w.Write(8u); Str(arch);
        if (name is not null)
        {
            Str("general.name"); w.Write(8u); Str(name);
        }

        Str(arch + ".context_length"); w.Write(4u); w.Write((uint)context);
        w.Write(new byte[filler]);
        return ms.ToArray();
    }

    private static ModelStore NewStore(TestRoot root) => ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"), new FakeDisk());

    [Fact]
    public void ReadsNameArchitectureAndContext()
    {
        var info = GgufReader.Read(new MemoryStream(Gguf()));
        Assert.Equal("Test Model", info.Name);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(8192, info.ContextLength);
        Assert.Equal(3u, info.Version);
    }

    [Fact]
    public void ContextLengthComesOnlyFromTheArchitectureKeyEvenWhenItIsListedFirst()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("GGUF"u8.ToArray()); w.Write(3u); w.Write(1UL); w.Write(3UL);
        void Str(string v) { w.Write((ulong)v.Length); w.Write(Encoding.UTF8.GetBytes(v)); }
        Str("other.context_length"); w.Write(4u); w.Write(111u);
        Str("llama.context_length"); w.Write(4u); w.Write(4096u);
        Str("general.architecture"); w.Write(8u); Str("llama");
        w.Write(new byte[64]);
        Assert.Equal(4096, GgufReader.Read(new MemoryStream(ms.ToArray())).ContextLength);
    }

    [Fact]
    public void MissingNameStillReads()
    {
        var info = GgufReader.Read(new MemoryStream(Gguf(name: null)));
        Assert.Null(info.Name);
        Assert.Equal("llama", info.Architecture);
    }

    [Fact]
    public void WrongMagicIsRefusedWithAClearMessage()
    {
        var bytes = Gguf();
        bytes[0] = (byte)'X';
        var ex = Assert.Throws<GgufFormatException>(() => GgufReader.Read(new MemoryStream(bytes)));
        Assert.Contains("not a GGUF", ex.Message);
        Assert.Throws<GgufFormatException>(() => GgufReader.Read(new MemoryStream([])));
    }

    [Fact]
    public void TruncatedHeaderIsRefused()
    {
        var bytes = Gguf(filler: 0);
        foreach (var cut in new[] { 3, 8, 20, 40, bytes.Length - 3 })
        {
            Assert.Throws<GgufFormatException>(() => GgufReader.Read(new MemoryStream(bytes[..cut])));
        }
    }

    [Fact]
    public void UnknownVersionIsRefused()
    {
        var bytes = Gguf();
        bytes[4] = 9;
        Assert.Throws<GgufFormatException>(() => GgufReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task ImportCopiesIntoTheStoreAndLeavesTheOriginalUntouched()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var src = Path.Combine(root.Sub("Downloads"), "My Model (Q4).gguf");
        var data = Gguf();
        File.WriteAllBytes(src, data);
        var stamp = File.GetLastWriteTimeUtc(src);

        var result = await store.ImportGgufAsync(src, null, Ct);

        Assert.Equal(ImportStatus.Imported, result.Status);
        var model = result.Model!;
        Assert.True(model.UserAdded);
        Assert.Equal("Test Model", model.DisplayName);
        Assert.Equal(ModelKinds_Chat, model.Kind);
        Assert.StartsWith("user-", model.Id);
        Assert.True(model.IsComplete);
        var stored = Assert.Single(model.Files);
        Assert.StartsWith(store.Location.Path, stored.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(data, File.ReadAllBytes(stored.Path));
        Assert.Equal(TestData.Sha(data), stored.Sha256);
        Assert.Empty(Directory.EnumerateFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));

        Assert.Equal(data, File.ReadAllBytes(src));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(src));
    }

    private const string ModelKinds_Chat = "chat";

    [Fact]
    public async Task EmbeddingArchitectureBecomesAnEmbeddingModel()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var src = Path.Combine(root.Sub("in"), "e.gguf");
        File.WriteAllBytes(src, Gguf(arch: "nomic-bert"));
        var result = await store.ImportGgufAsync(src, null, Ct);
        Assert.Equal("embedding", result.Model!.Kind);
    }

    [Fact]
    public async Task SameNameTwiceGivesTwoModelsAndSameContentIsNotStoredAgain()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var a = Path.Combine(root.Sub("a"), "m.gguf");
        var b = Path.Combine(root.Sub("b"), "m.gguf");
        File.WriteAllBytes(a, Gguf(filler: 100));
        File.WriteAllBytes(b, Gguf(filler: 200));

        var first = await store.ImportGgufAsync(a, null, Ct);
        var second = await store.ImportGgufAsync(b, null, Ct);
        var again = await store.ImportGgufAsync(a, null, Ct);

        Assert.Equal(ImportStatus.Imported, first.Status);
        Assert.Equal(ImportStatus.Imported, second.Status);
        Assert.NotEqual(first.Model!.Id, second.Model!.Id);
        Assert.Equal(ImportStatus.AlreadyStored, again.Status);
        Assert.Equal(first.Model.Id, again.Model!.Id);
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
        Assert.Empty(Directory.EnumerateFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NonGgufIsRefusedAndNothingIsLeftBehind()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var src = Path.Combine(root.Sub("in"), "notes.gguf");
        File.WriteAllText(src, "this is a text file");

        await Assert.ThrowsAsync<GgufFormatException>(() => store.ImportGgufAsync(src, null, Ct));

        Assert.Empty(await store.ListAsync(Ct));
        Assert.Equal("this is a text file", File.ReadAllText(src));
    }

    [Fact]
    public async Task TruncatedGgufIsRefused()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var src = Path.Combine(root.Sub("in"), "cut.gguf");
        File.WriteAllBytes(src, Gguf(filler: 0)[..30]);
        await Assert.ThrowsAsync<GgufFormatException>(() => store.ImportGgufAsync(src, null, Ct));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task MissingSourceAndNotEnoughSpaceAreReportedWithoutCopying()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        Assert.Equal(ImportStatus.SourceMissing, (await store.ImportGgufAsync(Path.Combine(root.Path, "nope.gguf"), null, Ct)).Status);

        var small = ModelStore.OpenOrCreate(Path.Combine(root.Path, "small"), new FakeDisk(1000));
        var src = Path.Combine(root.Sub("in"), "m.gguf");
        File.WriteAllBytes(src, Gguf());
        var result = await small.ImportGgufAsync(src, null, Ct);
        Assert.Equal(ImportStatus.NotEnoughSpace, result.Status);
        Assert.True(result.NeededBytes > 1000);
        Assert.Empty(await small.ListAsync(Ct));
    }

    [Fact]
    public async Task CancelRemovesOnlyItsOwnPartAndKeepsTheOriginal()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var keep = Path.Combine(root.Sub("in"), "keep.txt");
        File.WriteAllText(keep, "mine");
        var src = Path.Combine(root.Sub("in"), "big.gguf");
        var data = Gguf(filler: 6 << 20);
        File.WriteAllBytes(src, data);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ImportGgufAsync(src, copied => { if (copied > 0) cts.Cancel(); }, cts.Token));

        Assert.Empty(await store.ListAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));
        Assert.Equal(data, File.ReadAllBytes(src));
        Assert.Equal("mine", File.ReadAllText(keep));
    }

    [Fact]
    public async Task FileInsideTheStoreIsRefused()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        await Assert.ThrowsAsync<StoreRefusedException>(() => store.ImportGgufAsync(Path.Combine(store.Location.Path, "store.json"), null, Ct));
    }

    [Fact]
    public async Task RemovingAUserModelDeletesOnlyTheStoreCopy()
    {
        using var root = new TestRoot();
        var store = NewStore(root);
        var src = Path.Combine(root.Sub("in"), "m.gguf");
        var data = Gguf();
        File.WriteAllBytes(src, data);
        var model = (await store.ImportGgufAsync(src, null, Ct)).Model!;
        var copy = model.Files[0].Path;

        await store.RemoveAsync(model.Id, Ct);

        Assert.False(File.Exists(copy));
        Assert.Null(await store.GetAsync(model.Id, Ct));
        Assert.Equal(data, File.ReadAllBytes(src));
    }

    [Fact]
    public async Task UserAddedFlagSurvivesReopenAndAnOlderManifestStillLoads()
    {
        using var root = new TestRoot();
        var folder = Path.Combine(root.Path, "models");
        var store = ModelStore.OpenOrCreate(folder, new FakeDisk());
        var src = Path.Combine(root.Sub("in"), "m.gguf");
        File.WriteAllBytes(src, Gguf());
        var id = (await store.ImportGgufAsync(src, null, Ct)).Model!.Id;
        store.Dispose();

        var manifestPath = Path.Combine(store.Location.Path, "store.json");
        var text = File.ReadAllText(manifestPath);
        Assert.Contains("\"user_added\": true", text);

        var reopened = ModelStore.Open(store.Location.Path, new FakeDisk());
        Assert.True((await reopened.GetAsync(id, Ct))!.UserAdded);
        reopened.Dispose();

        // A manifest written before the field existed has no user_added and loads as a normal model.
        File.WriteAllText(manifestPath, text.Replace("\"user_added\": true,", ""));
        var old = ModelStore.Open(store.Location.Path, new FakeDisk());
        var loaded = await old.GetAsync(id, Ct);
        Assert.NotNull(loaded);
        Assert.False(loaded!.UserAdded);
        Assert.True(JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement.TryGetProperty("models", out _));
    }

    [Fact]
    public void StoreFileNamesAreMadeSafe()
    {
        Assert.Equal("My-Model-Q4.gguf", ModelStore.SafeStoreFileName("My Model (Q4)"));
        Assert.Equal("model.gguf", ModelStore.SafeStoreFileName("..."));
        Assert.Equal("Model.v2.gguf", ModelStore.SafeStoreFileName("Model..v2"));
        Assert.Equal("a.b.gguf", ModelStore.SafeStoreFileName(".a...b."));
        Assert.Equal("model.gguf", ModelStore.SafeStoreFileName("CON"));
        Assert.EndsWith(".gguf", ModelStore.SafeStoreFileName(new string('a', 400)));
    }
}
