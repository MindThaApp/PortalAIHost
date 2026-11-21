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

        var shell = await HostShell.StartAsync(new HostShellOptions
        {
            Paths = paths,
            Supervisors = supervisors,
            KeyProtector = new DpapiHostKeyProtector(),
            CallerResolver = new WindowsCallerIdentityResolver(),
            Hardware = new WindowsHardwareProbe(),
            Store = OpenStore(paths),
            HostVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        }).ConfigureAwait(false);

        if (shell is null)
        {
            // Another host owns this user's pipe: ask it to show its window, then leave.
            foreach (var s in supervisors.Values)
            {
                await s.DisposeAsync().ConfigureAwait(false);
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
        TrayIcon? tray = null;
        if (!paths.TestMode || Environment.GetEnvironmentVariable(TrayInTestModeVariable) == "1")
        {
            tray = TrayIcon.Start();
            tray.OpenRequested += (_, _) => OpenWindow(paths, null);
            tray.QuitRequested += (_, _) => shell.RequestQuit();
            tray.Resumed += (_, _) => _ = shell.OnResumeAsync();
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
