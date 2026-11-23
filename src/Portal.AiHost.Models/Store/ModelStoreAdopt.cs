using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

public sealed partial class ModelStore
{
    /// <summary>
    /// Adopts a model file the user already has somewhere else (for example a GGUF in Downloads) as the catalogue
    /// file <paramref name="fileName"/> of <paramref name="modelId"/>. The source is only ever read: it is copied
    /// into the store as a normal .part file, the copy is checked against <paramref name="size"/> and
    /// <paramref name="sha256"/>, and only then committed under its final name. The source is never moved, renamed,
    /// locked for write or deleted. On a size or hash mismatch the partial copy (a file this store created) is
    /// discarded and null is returned. A file already inside this store is refused (use
    /// <see cref="TryAdoptExistingAsync"/> for that).
    /// </summary>
    public async Task<StoredFile?> AdoptFromPathAsync(
        string modelId, string fileName, string sourcePath, string sha256, long size, CancellationToken ct)
    {
        var source = Path.GetFullPath(sourcePath);
        if (_folder.IsStrictlyInside(source))
        {
            throw new StoreRefusedException("That file is already inside the model store.");
        }

        var info = new FileInfo(source);
        if (!info.Exists || info.LinkTarget is not null || info.Length != size)
        {
            return null;
        }

        var existing = await GetAsync(modelId, ct).ConfigureAwait(false);
        if (existing?.Files.Any(f => f.Name == fileName && f.State != StoredFileState.Partial) == true)
        {
            return existing.Files.First(f => f.Name == fileName);
        }

        var part = await BeginFileAsync(modelId, fileName, ct).ConfigureAwait(false);
        var committed = false;
        try
        {
            await using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
            await using (var dst = await OpenPartForWriteAsync(modelId, part, 1 << 20, ct).ConfigureAwait(false))
            {
                dst.SetLength(0);
                await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
                await dst.FlushAsync(ct).ConfigureAwait(false);
            }

            if (new FileInfo(part).Length != size
                || !string.Equals(await HashFileAsync(part, ct).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var stored = await CommitFileAsync(modelId, part, sha256, ct).ConfigureAwait(false);
            committed = true;
            return stored;
        }
        finally
        {
            if (!committed)
            {
                await DiscardPartAsync(modelId, part, CancellationToken.None).ConfigureAwait(false);
                await ForgetIfEmptyAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
