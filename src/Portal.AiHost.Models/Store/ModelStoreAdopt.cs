using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

/// <summary>Outcome of <see cref="ModelStore.AdoptFromPathAsync"/>.</summary>
public enum AdoptStatus
{
    /// <summary>The file was copied, verified and stored.</summary>
    Adopted,

    /// <summary>The same file (same hash) was already stored; nothing was copied.</summary>
    AlreadyStored,

    /// <summary>The source file does not exist.</summary>
    SourceMissing,

    /// <summary>The source is a link; links are never followed.</summary>
    SourceIsLink,

    /// <summary>The source has a different size than expected.</summary>
    SizeMismatch,

    /// <summary>The copy did not match the expected SHA-256; the partial copy was discarded.</summary>
    HashMismatch,

    /// <summary>The model is being downloaded or adopted right now.</summary>
    Busy,

    /// <summary>The model has an unfinished download for that file; resume it instead.</summary>
    PartialDownloadExists,

    /// <summary>A different file is already stored under that name.</summary>
    DifferentFileStored,

    /// <summary>The store drive does not have room for the copy.</summary>
    NotEnoughSpace,
}

/// <summary>Result of an adopt: the status and, when a file is stored, the stored file.</summary>
public sealed record AdoptResult(AdoptStatus Status, StoredFile? File = null)
{
    public bool Succeeded => Status is AdoptStatus.Adopted or AdoptStatus.AlreadyStored;
}

public sealed partial class ModelStore
{
    /// <summary>
    /// Adopts a model file the user already has somewhere else (for example a GGUF in Downloads) as the catalogue
    /// file <paramref name="fileName"/> of <paramref name="modelId"/>. The source (a full path) is only ever read:
    /// it is copied into the store as a normal .part file, the copy is checked against <paramref name="size"/> and
    /// <paramref name="sha256"/>, and only then committed under its final name. The source is never moved, renamed,
    /// locked for write or deleted. Only the .part this call created is ever discarded. A file already inside this
    /// store is refused (see <see cref="TryAdoptExistingAsync"/> for that). Never touches an unfinished download.
    /// </summary>
    public async Task<AdoptResult> AdoptFromPathAsync(
        string modelId, string fileName, string sourcePath, string sha256, long size, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The source must be a full path.", nameof(sourcePath));
        }

        AssertSafeFileName(fileName);
        var source = Path.GetFullPath(sourcePath);
        if (_folder.IsStrictlyInside(source))
        {
            throw new StoreRefusedException("That file is already inside the model store.");
        }

        var info = new FileInfo(source);
        if (!info.Exists)
        {
            return new AdoptResult(AdoptStatus.SourceMissing);
        }

        if (info.LinkTarget is not null)
        {
            return new AdoptResult(AdoptStatus.SourceIsLink);
        }

        if (info.Length != size)
        {
            return new AdoptResult(AdoptStatus.SizeMismatch);
        }

        IDisposable session;
        try
        {
            session = AcquireDownload(modelId);
        }
        catch (InvalidOperationException)
        {
            return new AdoptResult(AdoptStatus.Busy);
        }

        using (session)
        {
            var existing = await GetAsync(modelId, ct).ConfigureAwait(false);
            var existingFile = existing?.Files.FirstOrDefault(f => f.Name == fileName);
            if (existingFile is not null)
            {
                if (existingFile.State == StoredFileState.Partial)
                {
                    return new AdoptResult(AdoptStatus.PartialDownloadExists);
                }

                return string.Equals(existingFile.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                    ? new AdoptResult(AdoptStatus.AlreadyStored, existingFile)
                    : new AdoptResult(AdoptStatus.DifferentFileStored);
            }

            if (_disk.GetFreeBytes(_folder.Root) < size + (long)(size * 0.02) + (256L << 20))
            {
                return new AdoptResult(AdoptStatus.NotEnoughSpace);
            }

            var modelExisted = existing is not null;
            string? part = null;
            var committed = false;
            try
            {
                await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
                var (_, sourceId) = FileIdentity.Read(src.SafeFileHandle);
                if (src.Length != size)
                {
                    return new AdoptResult(AdoptStatus.SizeMismatch);
                }

                if (sourceId is not null && await HasFileIdAsync(sourceId, ct).ConfigureAwait(false))
                {
                    throw new StoreRefusedException("That file is already inside the model store.");
                }

                part = await BeginFileAsync(modelId, fileName, ct).ConfigureAwait(false);
                await using (var dst = await OpenPartForWriteAsync(modelId, part, 1 << 20, ct).ConfigureAwait(false))
                {
                    dst.SetLength(0); // a part this call just created, nothing to lose
                    await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
                    await dst.FlushAsync(ct).ConfigureAwait(false);
                    dst.Flush(flushToDisk: true);
                }

                if (new FileInfo(part).Length != size)
                {
                    return new AdoptResult(AdoptStatus.SizeMismatch);
                }

                if (!string.Equals(await HashFileAsync(part, ct).ConfigureAwait(false), sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new AdoptResult(AdoptStatus.HashMismatch);
                }

                var stored = await CommitFileAsync(modelId, part, sha256, size, ct).ConfigureAwait(false);
                committed = true;
                return new AdoptResult(AdoptStatus.Adopted, stored);
            }
            finally
            {
                if (!committed && part is not null)
                {
                    try
                    {
                        await DiscardPartAsync(modelId, part, CancellationToken.None).ConfigureAwait(false);
                        if (!modelExisted)
                        {
                            await ForgetIfEmptyAsync(modelId, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // Cleanup trouble must not hide the real result; anything left is this store's own .part.
                    }
                }
            }
        }
    }
}
