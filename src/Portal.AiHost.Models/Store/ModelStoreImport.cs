using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;

namespace Portal.AiHost.Core.Store;

/// <summary>Outcome of <see cref="IModelStoreImporter.ImportGgufAsync"/>.</summary>
public enum ImportStatus
{
    /// <summary>The file was copied into the store and recorded.</summary>
    Imported,

    /// <summary>The same file (same SHA-256) was already added; nothing new was stored.</summary>
    AlreadyStored,

    SourceMissing,

    /// <summary>The source is a link; links are never followed.</summary>
    SourceIsLink,

    /// <summary>The store drive does not have room for the copy; <see cref="ImportResult.NeededBytes"/> says how much.</summary>
    NotEnoughSpace,
}

public sealed record ImportResult(ImportStatus Status, StoredModel? Model = null, GgufInfo? Info = null, long NeededBytes = 0)
{
    public bool Succeeded => Status is ImportStatus.Imported or ImportStatus.AlreadyStored;
}

/// <summary>"Add your own model": copies a GGUF file the user has into the host's own store.</summary>
public interface IModelStoreImporter : IModelStore
{
    /// <summary>
    /// Checks that <paramref name="sourcePath"/> is a readable GGUF file (<see cref="GgufFormatException"/> otherwise),
    /// then copies it into the store under a new model record marked as user-added. The original is only read. The
    /// copy is written as a .part, its size and SHA-256 are checked, and only then renamed into place. A cancelled
    /// or failed import removes only the .part it created. <paramref name="onCopied"/> gets the bytes copied so far.
    /// </summary>
    Task<ImportResult> ImportGgufAsync(string sourcePath, Action<long>? onCopied, CancellationToken ct);
}

public sealed partial class ModelStore : IModelStoreImporter
{
    private static readonly HashSet<string> EmbeddingArchitectures = new(StringComparer.OrdinalIgnoreCase)
    {
        "bert", "nomic-bert", "nomic-bert-moe", "jina-bert-v2", "jina-bert-v3", "modern-bert", "neo-bert",
    };

    public async Task<ImportResult> ImportGgufAsync(string sourcePath, Action<long>? onCopied, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The source must be a full path.", nameof(sourcePath));
        }

        var source = Path.GetFullPath(sourcePath);
        if (_folder.IsStrictlyInside(source))
        {
            throw new StoreRefusedException("That file is already inside the model store.");
        }

        var info = new FileInfo(source);
        if (!info.Exists)
        {
            return new ImportResult(ImportStatus.SourceMissing);
        }

        if (info.LinkTarget is not null)
        {
            return new ImportResult(ImportStatus.SourceIsLink);
        }

        var header = GgufReader.Read(source);
        var size = info.Length;
        var needed = size + (long)(size * 0.02) + (256L << 20);
        if (_disk.GetFreeBytes(_folder.Root) < needed)
        {
            return new ImportResult(ImportStatus.NotEnoughSpace, Info: header, NeededBytes: needed);
        }

        var fileName = SafeStoreFileName(Path.GetFileNameWithoutExtension(source));
        var modelId = await Locked(ct, () => PickUserModelId(fileName)).ConfigureAwait(false);
        using var session = AcquireDownload(modelId);

        var architecture = header.Architecture ?? "";
        var kind = EmbeddingArchitectures.Contains(architecture) ? ModelKinds.Embedding : ModelKinds.Chat;
        var display = header.Name ?? Path.GetFileNameWithoutExtension(source);
        string? part = null;
        var committed = false;
        try
        {
            await Locked(ct, () =>
            {
                var m = GetOrCreateCatalogueModel(modelId);
                m.DisplayName = display;
                m.Kind = kind;
                m.Engine = EngineKinds.LlamaServer;
                m.UserAdded = true;
                SaveManifest();
            }).ConfigureAwait(false);

            await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);
            var (_, sourceId) = FileIdentity.Read(src.SafeFileHandle);
            if (sourceId is not null && await HasFileIdAsync(sourceId, ct).ConfigureAwait(false))
            {
                throw new StoreRefusedException("That file is already inside the model store.");
            }

            if (src.Length != size)
            {
                throw new IOException("The file changed while it was being read. Try again once it is finished.");
            }

            part = await BeginFileAsync(modelId, fileName, ct).ConfigureAwait(false);
            string sha;
            long copied = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var dst = await OpenPartForWriteAsync(modelId, part, 1 << 20, ct).ConfigureAwait(false);
                dst.SetLength(0); // a part this call just created, nothing to lose
                var buffer = new byte[1 << 20];
                var lastReport = Environment.TickCount64;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    copied += n;
                    if (onCopied is not null && Environment.TickCount64 - lastReport > 150)
                    {
                        lastReport = Environment.TickCount64;
                        onCopied(copied);
                    }
                }

                await dst.FlushAsync(ct).ConfigureAwait(false);
                dst.Flush(flushToDisk: true);
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (copied != size || new FileInfo(part).Length != size)
            {
                throw new IOException("The copy is not the same size as the original, so it was discarded.");
            }

            onCopied?.Invoke(copied);
            var twin = await Locked(ct, () => _manifest.Models.FirstOrDefault(m => m.UserAdded && m.Id != modelId
                && m.Files.Any(f => f.State != StoredFileState.Partial && string.Equals(f.Sha256, sha, StringComparison.OrdinalIgnoreCase)))).ConfigureAwait(false);
            if (twin is not null)
            {
                return new ImportResult(ImportStatus.AlreadyStored, ToStored(twin), header);
            }

            await CommitFileAsync(modelId, part, sha, size, ct).ConfigureAwait(false);
            committed = true;
            var stored = await GetAsync(modelId, ct).ConfigureAwait(false);
            return new ImportResult(ImportStatus.Imported, stored, header);
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    if (part is not null)
                    {
                        await DiscardPartAsync(modelId, part, CancellationToken.None).ConfigureAwait(false);
                    }

                    await ForgetIfEmptyAsync(modelId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
                {
                    // Cleanup trouble must not hide the real result; anything left is this store's own .part.
                }
            }
        }
    }

    /// <summary>A store file name from the user's file name: letters, digits, dot, dash and underscore, ending in .gguf.</summary>
    internal static string SafeStoreFileName(string stem)
    {
        var s = UnsafeNameChars().Replace(stem, "-").Trim('-', '.', '_');
        if (s.Length == 0 || OwnedFolder.IsReservedDeviceName(s))
        {
            s = "model";
        }

        if (s.Length > 150)
        {
            s = s[..150];
        }

        return s + ".gguf";
    }

    private string PickUserModelId(string fileName)
    {
        var stem = SafeIdChars().Replace(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), "-").Trim('-');
        if (stem.Length == 0)
        {
            stem = "model";
        }

        if (stem.Length > 60)
        {
            stem = stem[..60].Trim('-');
        }

        var baseId = "user-" + stem;
        for (var i = 1; ; i++)
        {
            var id = i == 1 ? baseId : baseId + "-" + i;
            if (Find(id) is null && !IsDownloading(id) && !Exists(Path.Combine(_folder.Root, id)))
            {
                return id;
            }
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeNameChars();
}
