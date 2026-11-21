using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Store;
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
        _ = args;
        var paths = HostPaths.Resolve(Environment.GetEnvironmentVariable);
        if (paths is null)
        {
            return HostPaths.BadTestRootExitCode;
        }

        var supervisors = new Dictionary<string, IEngineSupervisor>(StringComparer.Ordinal)
        {
            [ModelKinds.Chat] = CreateSupervisor(ModelKinds.Chat),
            [ModelKinds.Embedding] = CreateSupervisor(ModelKinds.Embedding),
        };

        HostShell? shell;
        try
        {
            shell = await HostShell.StartAsync(new HostShellOptions
            {
                Paths = paths,
                Supervisors = supervisors,
                KeyProtector = new DpapiHostKeyProtector(),
                CallerResolver = new WindowsCallerIdentityResolver(),
                Hardware = new WindowsHardwareProbe(),
                OpenStore = () => OpenStore(paths), // Only after the single-instance lock is held.
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
                await client.ShowAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ControlPipeException or UnauthorizedAccessException)
            {
            }

            return 0;
        }

        shell.ShowRequested += (_, page) => OpenWindow(paths, page);
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

    private static EngineSupervisor CreateSupervisor(string kind) => new(kind, new LlamaServerAdapter(),
        new EngineSupervisorOptions
        {
            ResolveBinary = (spec, cpu) =>
            {
                var flavour = cpu || spec.Placement.Backend == RuntimeProbe.Cpu ? RuntimeProbe.Cpu : RuntimeProbe.Vulkan;
                return new EngineBinary(flavour, Path.Combine(AppContext.BaseDirectory, "engine", flavour, RuntimeProbe.ExeName));
            },
        });

    private static ModelStore? OpenStore(HostPaths paths)
    {
        try
        {
            return ModelStore.OpenOrCreate(Path.Combine(paths.DataRoot, "models"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts the window next to this exe; never in test mode.</summary>
    private static void OpenWindow(HostPaths paths, string? page)
    {
        if (paths.TestMode)
        {
            return;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "PortalAIHost.App.exe");
        if (!File.Exists(exe))
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (page is not null)
            {
                start.ArgumentList.Add("--page");
                start.ArgumentList.Add(page);
            }

            using var _ = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
