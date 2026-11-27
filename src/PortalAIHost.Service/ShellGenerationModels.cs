using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Generation;

namespace PortalAIHost.Service;

/// <summary>
/// The model store as the generation engine sees it: installed, complete, undamaged models of a kind, with each file's
/// role from the catalogue (or guessed from its name). Read-only apart from the in-use mark the store keeps while a job
/// runs; nothing here writes, moves or deletes a model file.
/// </summary>
internal sealed class ShellGenerationModels(HostShell shell, Func<string, string?> damagedReason) : IGenerationModelSource
{
    public async Task<IReadOnlyList<GenerationModel>> ListAsync(string kind, CancellationToken ct)
    {
        if (shell.Store is not { } store)
        {
            return [];
        }

        var list = new List<GenerationModel>();
        foreach (var m in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (m.Kind != kind || !m.IsComplete || damagedReason(m.Id) is not null)
            {
                continue;
            }

            var entry = shell.CatalogueEntry(m.Id);
            var components = m.Files.Select(f =>
            {
                var role = entry?.Files.FirstOrDefault(cf => string.Equals(cf.Name, f.Name, StringComparison.OrdinalIgnoreCase))?.Role
                    ?? GenerationFileRoles.Guess(f.Name, m.Files.Count);
                return new GenerationComponent(role, f.Path, f.SizeBytes);
            }).ToList();
            list.Add(new GenerationModel(m.Id, m.DisplayName, m.Kind, entry?.Family ?? "", components, entry));
        }

        return list;
    }

    public async Task<IAsyncDisposable> UseAsync(string modelId, CancellationToken ct)
    {
        var store = shell.Store;
        if (store is not null)
        {
            await store.SetInUseAsync(modelId, true, ct).ConfigureAwait(false);
        }

        return new Release(store, modelId);
    }

    private sealed class Release(IModelStore? store, string modelId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (store is null)
            {
                return;
            }

            try
            {
                await store.SetInUseAsync(modelId, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Released at the next start (stale in-use marks are cleared then).
            }
        }
    }
}
