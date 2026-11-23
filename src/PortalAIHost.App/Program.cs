using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PortalAIHost.App;

/// <summary>Single instance: a second launch hands its activation to the running window and exits.</summary>
public static class Program
{
    public const string InstanceKey = "main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        AppOptions.Parse(args);
        if (RedirectToRunningInstance())
            return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (keyInstance.IsCurrent)
            return false;

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirect off the STA thread; the wait keeps pumping COM so the call can complete.
        try
        {
            Task.Run(() => keyInstance.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }
        return true;
    }
}

/// <summary>Command-line switches. Only used for development and checks.</summary>
public static class AppOptions
{
    /// <summary>Use the scripted fake host instead of the real one: --fake or --fake=loaded.</summary>
    public static Services.FakeScenario? Fake { get; private set; }

    /// <summary>Show accented, longer text so missing or clipped strings are easy to spot.</summary>
    public static bool PseudoLoc { get; private set; }

    /// <summary>Move the window off the visible screen (used by automated checks).</summary>
    public static bool OffScreen { get; private set; }

    /// <summary>Open this page first: home, models, apps, tryit, settings.</summary>
    public static string? StartPage { get; private set; }

    /// <summary>Where the tray icon was clicked (screen pixels) when the host started this window for the pop-up.</summary>
    public static (int X, int Y)? TrayPopup { get; private set; }

    /// <summary>Folder watched for take-NAME.txt files; each one makes the window save NAME.png (used by automated checks).</summary>
    public static string? ShotDir { get; private set; }

    /// <summary>Check-only: force the theme (light or dark) for this run without saving it.</summary>
    public static string? ThemeOverride { get; private set; }

    /// <summary>Check-only: scale the welcome text (for example 2 for a 200 % text-size look) without changing Windows settings.</summary>
    public static double? TextScale { get; private set; }

    /// <summary>Applies the check-only theme override to the saved choices.</summary>
    public static Services.AppearancePrefs WithOverrides(Services.AppearancePrefs prefs) => ThemeOverride switch
    {
        "light" => prefs with { Theme = Services.ThemeChoice.Light },
        "dark" => prefs with { Theme = Services.ThemeChoice.Dark },
        _ => prefs,
    };

    /// <summary>Check-only: keep the window's own state (welcome done, appearance) in this folder instead of the per-user one.</summary>
    public static string? StateFolder { get; private set; }

    /// <summary>Treat first-run setup as already done.</summary>
    public static bool SkipFirstRun { get; private set; }

    public static void Parse(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) StartPage = a[7..];
            else if (Services.TrayPopupArgs.Find(a) is { } popupAt) TrayPopup = popupAt;
#if DEBUG
            // Development and automated-check switches; Release builds ignore them.
            else if (a.StartsWith("--fake", StringComparison.OrdinalIgnoreCase))
            {
                var name = a.Length > 7 && a[6] == '=' ? a[7..] : "loaded";
                Fake = Enum.TryParse<Services.FakeScenario>(name, true, out var s) ? s : Services.FakeScenario.Loaded;
            }
            else if (a.Equals("--pseudo-loc", StringComparison.OrdinalIgnoreCase)) PseudoLoc = true;
            else if (a.Equals("--offscreen", StringComparison.OrdinalIgnoreCase)) OffScreen = true;
            else if (a.Equals("--skip-first-run", StringComparison.OrdinalIgnoreCase)) SkipFirstRun = true;
            else if (a.StartsWith("--state-dir=", StringComparison.OrdinalIgnoreCase)) StateFolder = a[12..];
            else if (a.StartsWith("--shot-dir=", StringComparison.OrdinalIgnoreCase)) ShotDir = a[11..];
            else if (a.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase)) ThemeOverride = a[8..].ToLowerInvariant();
            else if (a.StartsWith("--text-scale=", StringComparison.OrdinalIgnoreCase)
                     && double.TryParse(a[13..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k))
                TextScale = Math.Clamp(k, 1, 3);
#endif
        }
    }
}
