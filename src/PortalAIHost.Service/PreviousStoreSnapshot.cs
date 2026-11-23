using Portal.AiHost.Core.Abstractions;

namespace PortalAIHost.Service;

/// <summary>
/// What an earlier models folder held when it was last looked at. The folder itself is not kept open, so an external
/// drive can be ejected and Explorer can delete the old folder. Changes go through a freshly opened store.
/// </summary>
internal sealed class PreviousStoreSnapshot : IModelStore
{
    private readonly IReadOnlyList<StoredModel> _models;

    private PreviousStoreSnapshot(StoreLocation location, IReadOnlyList<StoredModel> models)
    {
        Location = location;
        _models = models;
    }

    public StoreLocation Location { get; }

    /// <summary>Reads the store's model list now; the caller disposes the store afterwards.</summary>
    public static PreviousStoreSnapshot Capture(IModelStore store) =>
        new(store.Location, store.ListAsync(CancellationToken.None).GetAwaiter().GetResult());

    public Task<IReadOnlyList<StoredModel>> ListAsync(CancellationToken ct) => Task.FromResult(_models);

    public Task<StoredModel?> GetAsync(string modelId, CancellationToken ct) =>
        Task.FromResult(_models.FirstOrDefault(m => m.Id == modelId));

    private static NotSupportedException ReadOnly() => new("This is a read-only view of an earlier models folder.");

    public Task<string> BeginFileAsync(string modelId, string fileName, CancellationToken ct) => throw ReadOnly();
    public Task<StoredFile> CommitFileAsync(string modelId, string partPath, string sha256, CancellationToken ct) => throw ReadOnly();
    public Task SetInUseAsync(string modelId, bool inUse, CancellationToken ct) => throw ReadOnly();
    public Task<StoredModel> AddExternalAsync(string filePath, string kind, string engine, CancellationToken ct) => throw ReadOnly();
    public Task RemoveAsync(string modelId, CancellationToken ct) => throw ReadOnly();
    public Task MoveStoreAsync(string newParentFolder, IProgress<double>? progress, CancellationToken ct) => throw ReadOnly();
}
