using Windows.Services.Store;

namespace PortalAIHost.App.Services;

/// <summary>Asks the Microsoft Store for pending updates of this app. It never downloads or installs anything.</summary>
internal sealed class StoreUpdateSource : IUpdateSource
{
    public const string StoreProductUri = "ms-windows-store://pdp/?ProductId=9MW5MTZ8LWP8";

    public Task<UpdateQueryResult> QueryAsync(CancellationToken ct) => Task.Run(QueryCoreAsync, ct);

    private static async Task<UpdateQueryResult> QueryCoreAsync()
    {
        try
        {
            var updates = await StoreContext.GetDefault().GetAppAndOptionalStorePackageUpdatesAsync();
            if (updates.Count == 0) return UpdateQueryResult.NoUpdate;
            var best = updates.Select(u => u.Package.Id.Version)
                .OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor).ThenByDescending(v => v.Build).ThenByDescending(v => v.Revision)
                .First();
            return UpdateQueryResult.Found($"{best.Major}.{best.Minor}.{best.Build}.{best.Revision}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return UpdateQueryResult.Failed;
        }
    }

    public static void OpenStorePage()
    {
        try
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(StoreProductUri));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UriFormatException)
        {
        }
    }
}
