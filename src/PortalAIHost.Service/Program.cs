using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Download;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Power;
using Portal.AiHost.Engine;
using Portal.AiHost.Platform.Windows.Hardware;
using Portal.AiHost.Platform.Windows.Identity;
using Portal.AiHost.Platform.Windows.Pipes;
using Portal.AiHost.Platform.Windows.Tray;

namespace PortalAIHost.Service;

internal static class Program
{
    public const string TrayInTestModeVariable = "PORTALAIHOST_TEST_TRAY";

    /// <summary>The host could not start (no free port, unreadable data); everything it started was released.</summary>
    public const int StartFailedExitCode = 4;

    /// <summary>Another Windows user's process holds this user's pipe name.</summary>
    public const int PipeHeldByOtherUserExitCode = 5;

    /// <summary>How long a session end may take before Windows is let go.</summary>
    private static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(3);

    [STAThread]
    private static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var launch = LaunchArgs.Parse(args);
        var paths = HostPaths.Resolve(Environment.GetEnvironmentVariable);
        if (paths is null)
        {
            return HostPaths.BadTestRootExitCode;
        }

        HostShell? shell = null;
        var supervisors = new Dictionary<string, IEngineSupervisor>(StringComparer.Ordinal)
        {
            [ModelKinds.Chat] = CreateSupervisor(ModelKinds.Chat, () => shell?.IdleUnloadFor(ModelKinds.Chat)),
            [ModelKinds.Embedding] = CreateSupervisor(ModelKinds.Embedding, () => shell?.IdleUnloadFor(ModelKinds.Embedding)),
        };
        var catalogue = CatalogueSource.Load(AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
        try
        {
            shell = await HostShell.StartAsync(new HostShellOptions
            {
                Paths = paths,
                Supervisors = supervisors,
                KeyProtector = new DpapiHostKeyProtector(),
                CallerResolver = new WindowsCallerIdentityResolver(),
                Hardware = new WindowsHardwareProbe(),
                OpenStoreReporting = settings => OpenStore(paths, settings), // Only after the single-instance lock is held.
                OpenPreviousStores = OpenPreviousStores,
                Catalogue = catalogue,
                CreateDownloader = store => new ModelDownloader(Http.Value, store, options: null, sleep: new PowerRequestSleepBlocker()),
                HostVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await DisposeAllAsync(supervisors.Values).ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Portal AI Host could not start: " + ex.GetType().Name).ConfigureAwait(false);
            return StartFailedExitCode;
        }

        if (shell is null)
        {
            // Another host owns this user's pipe: ask it to show its window, then leave.
            await DisposeAllAsync(supervisors.Values).ConfigureAwait(false);
            if (await ControlPipeServer.ProbeHolderAsync(paths.PipeName, TimeSpan.FromSeconds(1)).ConfigureAwait(false)
                is PipeHolder.OtherUser)
            {
                await Console.Error.WriteLineAsync("Portal AI Host cannot start: another Windows user holds its admin pipe.")
                    .ConfigureAwait(false);
                return PipeHeldByOtherUserExitCode;
            }

            try
            {
                await using var client = await ControlPipeClient.ConnectAsync(paths.PipeName, TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
                if (launch == LaunchMode.Open)
                {
                    await client.ShowAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ControlPipeException or UnauthorizedAccessException)
            {
            }

            return 0;
        }

        shell.ShowRequested += (_, page) => OpenWindow(paths, page);
        if (launch == LaunchMode.Open && LaunchArgs.IsProtocol(args))
        {
            shell.RequestShow(null);
        }
        // The hidden window always runs (test mode too) so shutdown, logoff, update and resume reach the host;
        // the icon itself only outside test mode unless asked for.
        var showIcon = !paths.TestMode || Environment.GetEnvironmentVariable(TrayInTestModeVariable) == "1";
        TrayIcon? tray = null;
        try
        {
            tray = TrayIcon.Start(showIcon: showIcon);
            tray.OpenRequested += (_, _) => OpenWindow(paths, null);
            tray.QuitRequested += (_, _) => shell.RequestQuit();
            tray.Resumed += (_, _) => _ = shell.OnResumeAsync();
            tray.SessionEnding += (_, _) => shell.StopForSessionEnd(SessionEndBudget);
        }
        catch (InvalidOperationException)
        {
            // No window station (for example a service session): the host still serves; quit comes over the pipe.
        }

        if (!paths.TestMode)
        {
            TrayIcon.RegisterForRestartAfterUpdate();
        }

        try
        {
            await shell.Completion.ConfigureAwait(false);
        }
        finally
        {
            tray?.Dispose();
        }

        return 0;
    }

    private static async Task DisposeAllAsync(IEnumerable<IEngineSupervisor> supervisors)
    {
        foreach (var s in supervisors)
        {
            try
            {
                await s.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
    }

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static EngineSupervisor CreateSupervisor(string kind, Func<TimeSpan?> idle) => new(kind, new LlamaServerAdapter(),
        new EngineSupervisorOptions
        {
            IdleUnloadPolicy = idle,
            ResolveBinary = (spec, cpu) =>
            {
                var flavour = cpu || spec.Placement.Backend == RuntimeProbe.Cpu ? RuntimeProbe.Cpu : RuntimeProbe.Vulkan;
                return new EngineBinary(flavour, Path.Combine(AppContext.BaseDirectory, "engine", flavour, RuntimeProbe.ExeName));
            },
        });

    /// <summary>Folder name the host creates (and marks) inside a models folder the user picked.</summary>
    public const string StoreFolderName = ModelStore.UserFolderName;

    /// <summary>
    /// The store folder: the default LocalState\models, or a marked subfolder of the folder chosen in settings.
    /// A test run never leaves its test root. Changing the folder never moves or deletes the old store.
    /// </summary>
    internal static string StoreFolderFor(HostPaths paths, HostSettings settings)
    {
        var fallback = Path.Combine(paths.DataRoot, "models");
        if (settings.ModelsFolder is not { Length: > 0 } chosen || !Path.IsPathFullyQualified(chosen))
        {
            return fallback;
        }

        var folder = Path.GetFullPath(ModelStore.StoreFolderIn(chosen));
        if (paths.TestMode && !folder.StartsWith(Path.GetFullPath(paths.DataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return folder;
    }

    /// <summary>
    /// Opens the store. The default folder is created when missing; a chosen folder gets the same checks as a
    /// new store (parent must exist, no FAT32) and an existing unmarked or damaged folder is refused, never adopted.
    /// A refusal is reported (status store_problem) and downloads stay off.
    /// </summary>
    private static StoreOpenResult OpenStore(HostPaths paths, HostSettings settings)
    {
        var folder = StoreFolderFor(paths, settings);
        try
        {
            var isDefault = string.Equals(folder, Path.Combine(paths.DataRoot, "models"), StringComparison.OrdinalIgnoreCase);
            return new(OpenWithLockRetry(
                () => isDefault ? ModelStore.OpenOrCreate(folder) : ModelStore.OpenOrCreateInParent(settings.ModelsFolder!),
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(250)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(null, $"The models folder '{folder}' could not be used: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a store, retrying for up to <paramref name="maxWait"/> while another host that is shutting down still
    /// holds its lock. Any other refusal, and a lock that stays held, is reported as usual.
    /// </summary>
    internal static T OpenWithLockRetry<T>(Func<T> open, TimeSpan maxWait, TimeSpan step)
    {
        var until = DateTime.UtcNow + maxWait;
        while (true)
        {
            try
            {
                return open();
            }
            catch (StoreLockedException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(step);
            }
        }
    }

    /// <summary>Earlier models folders, opened only when they exist and carry the marker. Nothing is created.</summary>
    private static IReadOnlyList<IModelStore> OpenPreviousStores(HostSettings settings, IModelStore? current)
    {
        var stores = new List<IModelStore>();
        foreach (var path in settings.PreviousStoreFolders ?? [])
        {
            if (current is not null && string.Equals(Path.GetFullPath(path), current.Location.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    // Looked at once and closed again: no lock is kept on an old (maybe external) drive.
                    using var opened = ModelStore.Open(path);
                    stores.Add(PreviousStoreSnapshot.Capture(opened));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unmarked, damaged or unreadable: left alone and not listed.
            }
        }

        return stores;
    }

    /// <summary>Starts the window next to this exe; never in test mode.</summary>
    private static void OpenWindow(HostPaths paths, string? page)
    {
        if (paths.TestMode)
        {
            return;
        }

        var exe = new[] { "PortalAIHost.exe", "PortalAIHost.App.exe" }
            .SelectMany(n => new[] { Path.Combine(AppContext.BaseDirectory, n), Path.Combine(AppContext.BaseDirectory, "..", n) })
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (exe is null)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (page is not null)
            {
                start.ArgumentList.Add("--page=" + page);
            }

            using var _ = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
