using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Runtime;
using Portal.AiHost.Server;

namespace PortalAIHost.Service;

/// <summary>
/// The model store as Model control sees it. Read-only: it lists installed models and resolves one to an active
/// model. Nothing here writes, moves or deletes a model file.
/// </summary>
internal sealed class ShellModelHost(HostShell shell) : IModelControlHost
{
    public async Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct)
    {
        var listing = await shell.Models.ListAsync(ct).ConfigureAwait(false);
        return [.. listing.Models.Where(m => m.Installed && m.StoreFolder is null && !m.Damaged)
            .Select(m => new InstalledModel(m.Id, m.DisplayName, m.Kind, m.SizeBytes, m.FitsThisPc))];
    }

    public async Task<ActiveModel?> ResolveAsync(string modelId, CancellationToken ct)
    {
        if (shell.Store is not { } store || await store.GetAsync(modelId, ct).ConfigureAwait(false) is not { IsComplete: true } stored)
        {
            return null;
        }

        // The catalogue entry lets the planner check the fit and apply a device's placement.
        return shell.ToActiveModel(stored) with { Entry = shell.CatalogueEntry(modelId) };
    }

    public Task ChangedAsync() => shell.PushStatusAsync();
}
