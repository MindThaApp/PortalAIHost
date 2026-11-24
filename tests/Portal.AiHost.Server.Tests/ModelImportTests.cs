using System.Collections.Concurrent;
using System.Text;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
using PortalAIHost.Service;

namespace Portal.AiHost.Server.Tests;

/// <summary>"Add your own model" through the model center: up-front refusals, progress events, cancel, listing.</summary>
public sealed class ModelImportTests
{
    private static byte[] Gguf(int filler)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("GGUF"u8.ToArray());
        w.Write(3u);
        w.Write(1UL);
        w.Write(2UL);
        void Str(string v) { w.Write((ulong)v.Length); w.Write(Encoding.UTF8.GetBytes(v)); }
        Str("general.architecture"); w.Write(8u); Str("llama");
        Str("general.name"); w.Write(8u); Str("Mine");
        w.Write(new byte[filler]);
        return ms.ToArray();
    }

    private static ModelCenter NewCenter(ModelStore store, ConcurrentQueue<StoreProgressEvent> events, Action<StoreProgressEvent>? onPush = null) =>
        new(store, null, null, null, () => new HostSettings(), _ => null, _ => Task.CompletedTask)
        {
            Hooks = new ModelCenterHooks
            {
                PushStore = e => { events.Enqueue(e); onPush?.Invoke(e); return Task.CompletedTask; },
            },
        };

    [Fact(Timeout = 30000)]
    public async Task Import_copies_reports_progress_and_lists_the_model_as_user_added()
    {
        using var root = new TestRoot();
        using var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var src = Path.Combine(root.Sub("in"), "mine.gguf");
        var data = Gguf(300_000);
        File.WriteAllBytes(src, data);
        var events = new ConcurrentQueue<StoreProgressEvent>();
        var center = NewCenter(store, events);

        await await center.StartImportAsync(src, CancellationToken.None);

        var done = events.Last();
        Assert.Equal(StoreOperations.Import, done.Operation);
        Assert.Equal(StoreStates.Done, done.State);
        Assert.NotNull(done.CatalogueId);
        var listed = (await center.ListAsync(CancellationToken.None)).Models.Single();
        Assert.True(listed.UserAdded);
        Assert.True(listed.Installed);
        Assert.Equal("Mine", listed.DisplayName);
        Assert.Equal(data, File.ReadAllBytes(src));
    }

    [Fact(Timeout = 30000)]
    public async Task Import_refuses_bad_files_before_anything_is_copied()
    {
        using var root = new TestRoot();
        using var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var events = new ConcurrentQueue<StoreProgressEvent>();
        var center = NewCenter(store, events);
        var text = Path.Combine(root.Sub("in"), "a.gguf");
        File.WriteAllText(text, "hello there, not a model");
        var wrongExt = Path.Combine(root.Sub("in"), "a.bin");
        File.WriteAllBytes(wrongExt, Gguf(10));

        var bad = await Assert.ThrowsAsync<Portal.AiHost.Platform.Windows.Pipes.ControlPipeException>(() => center.StartImportAsync(text, CancellationToken.None));
        Assert.Contains("not a GGUF", bad.Message);
        await Assert.ThrowsAsync<Portal.AiHost.Platform.Windows.Pipes.ControlPipeException>(() => center.StartImportAsync(wrongExt, CancellationToken.None));
        await Assert.ThrowsAsync<Portal.AiHost.Platform.Windows.Pipes.ControlPipeException>(() => center.StartImportAsync("relative.gguf", CancellationToken.None));
        await Assert.ThrowsAsync<Portal.AiHost.Platform.Windows.Pipes.ControlPipeException>(() => center.StartImportAsync(Path.Combine(root.Path, "missing.gguf"), CancellationToken.None));

        Assert.Empty(events);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact(Timeout = 30000)]
    public async Task Cancelling_an_import_leaves_no_model_and_the_original_alone()
    {
        using var root = new TestRoot();
        using var store = ModelStore.OpenOrCreate(Path.Combine(root.Path, "models"));
        var src = Path.Combine(root.Sub("in"), "big.gguf");
        var data = Gguf(8 << 20);
        File.WriteAllBytes(src, data);
        var events = new ConcurrentQueue<StoreProgressEvent>();
        ModelCenter? center = null;
        center = NewCenter(store, events, e =>
        {
            if (e.State == StoreStates.Running && e.BytesDone > 0)
            {
                center!.CancelOperation();
            }
        });

        await await center.StartImportAsync(src, CancellationToken.None);

        Assert.Equal(StoreStates.Cancelled, events.Last().State);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(store.Location.Path, "*.part*", SearchOption.AllDirectories));
        Assert.Equal(data, File.ReadAllBytes(src));
    }

    private sealed class ThrowingStore : IModelStoreImporter
    {
        public StoreLocation Location => new("X:\none", Guid.Empty, 0, "NTFS");
        public Task<ImportResult> ImportGgufAsync(string sourcePath, Action<long>? onCopied, CancellationToken ct) =>
            throw new InvalidOperationException("busy");
        public Task<IReadOnlyList<StoredModel>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredModel>>([]);
        public Task<StoredModel?> GetAsync(string modelId, CancellationToken ct) => Task.FromResult<StoredModel?>(null);
        public Task<string> BeginFileAsync(string modelId, string fileName, CancellationToken ct) => throw new NotSupportedException();
        public Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, CancellationToken ct) => throw new NotSupportedException();
        public Task SetInUseAsync(string modelId, bool inUse, CancellationToken ct) => Task.CompletedTask;
        public Task<StoredModel> AddExternalAsync(string filePath, string kind, string engine, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string modelId, CancellationToken ct) => Task.CompletedTask;
        public Task MoveStoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact(Timeout = 30000)]
    public async Task An_unexpected_store_error_still_ends_with_a_failed_event_and_frees_the_operation()
    {
        using var root = new TestRoot();
        var src = Path.Combine(root.Sub("in"), "m.gguf");
        File.WriteAllBytes(src, Gguf(100));
        var events = new ConcurrentQueue<StoreProgressEvent>();
        var center = new ModelCenter(new ThrowingStore(), null, null, null, () => new HostSettings(), _ => null, _ => Task.CompletedTask)
        {
            Hooks = new ModelCenterHooks { PushStore = e => { events.Enqueue(e); return Task.CompletedTask; } },
        };

        await await center.StartImportAsync(src, CancellationToken.None);

        Assert.Equal(StoreStates.Failed, events.Last().State);
        Assert.Equal(StoreOperations.Import, events.Last().Operation);
        await await center.StartImportAsync(src, CancellationToken.None); // the operation was released
    }
}
