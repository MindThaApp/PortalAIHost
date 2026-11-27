using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.CrashReports;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Services;

/// <summary>Builds and holds the few shared services. The window and pages read from here.</summary>
public static class AppServices
{
    public static IUiDispatcher Ui { get; private set; } = null!;
    public static IStrings Strings { get; private set; } = null!;
    public static WinUiDialogService Dialogs { get; private set; } = null!;
    public static IHostControl Host { get; private set; } = null!;
    public static IFolderOpener Folders { get; } = new ExplorerFolderOpener();
    public static WinUiModelFilePicker FilePicker { get; } = new();
    public static ShellViewModel Shell { get; private set; } = null!;
    /// <summary>Decides whether the "update available" bar shows. A no-op outside a packaged install.</summary>
    public static UpdateNudgeService UpdateNudge { get; private set; } = new(new NoUpdateSource(), new NoDismissState(), enabled: false);
    /// <summary>Opens a page by name (home, models, apps, tryit, settings) and moves the menu selection. Set by the window.</summary>
    public static Action<string>? NavigateTo { get; set; }

    /// <summary>Category id the Models page should filter to when it opens (from a portal-ai-host:models?category= link), or null.</summary>
    public static string? PendingModelsCategory { get; set; }

    /// <summary>Section of the Settings page to bring into view when it opens (for example "performance"), or null.</summary>
    public static string? PendingSettingsSection { get; set; }

    /// <summary>Raised when the main window is shown or hidden (minimized). Live pages stop reading while hidden.</summary>
    public static Action<bool>? MainVisibilityChanged { get; set; }
    public static IStartupControl Startup { get; private set; } = new UnavailableStartupControl();
    /// <summary>Applies appearance choices to the open window. Set by the window.</summary>
    public static IAppearanceApplier? Appearance { get; set; }

    public static void Build()
    {
        Ui = new WinUiDispatcher(DispatcherQueue.GetForCurrentThread());
        Strings = new ResourceStrings(AppOptions.PseudoLoc);
        Dialogs = new WinUiDialogService(Strings);
        IHostLauncher launcher;
        string stateFolder;
        if (AppOptions.Fake is { } scenario)
        {
            var fake = new FakeHostControl(scenario);
            Host = fake;
            launcher = new FakeHostLauncher(fake, succeeds: false);
            // A fake run never reads or writes the real app folder.
            stateFolder = Path.Combine(Path.GetTempPath(), "PortalAIHost.fake-" + Environment.ProcessId);
        }
        else
        {
            var pipe = new PipeHostControl(new PipeControlChannelFactory());
            Host = pipe;
            launcher = new ProcessHostLauncher(
                async ct => (await pipe.GetStatusAsync(ct).ConfigureAwait(false)).Connected,
                protocolStart: StartByProtocolAsync);
            Startup = new WindowsStartupControl();
            stateFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortalAIHost", "app");
            if (AppOptions.StateFolder is { Length: > 0 } custom) stateFolder = custom;
        }
        IAppState state = AppOptions.SkipFirstRun ? new FixedAppState(true) : new AppStateStore(stateFolder);
        UpdateNudge = new UpdateNudgeService(new StoreUpdateSource(), new UpdateNudgeStore(stateFolder),
            UpdateNudgeGate.IsEnabled(IsPackaged(), AppOptions.Fake is not null, AppOptions.SkipFirstRun, AppOptions.StateFolder is { Length: > 0 }, AppOptions.OffScreen || AppOptions.ShotDir is { Length: > 0 }));
        Shell = new ShellViewModel(Host, launcher, Ui, Strings, state)
        {
            CrashReportsApplier = CrashReporting.SetEnabled,
        };
        // Not for fake or check runs, which never send anything.
        if (AppOptions.Fake is null && !AppOptions.SkipFirstRun)
            CrashReporting.Start("app", typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", state.SendCrashReports);
    }

    private static bool IsPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Packaged installs register portal-ai-host:start; unpackaged runs have nothing to launch.</summary>
    private static async Task<bool> StartByProtocolAsync()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return await Windows.System.Launcher.LaunchUriAsync(new Uri("portal-ai-host:start"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private sealed class NoUpdateSource : IUpdateSource
    {
        public Task<UpdateQueryResult> QueryAsync(CancellationToken ct) => Task.FromResult(UpdateQueryResult.NoUpdate);
    }

    private sealed class NoDismissState : IUpdateNudgeState
    {
        public string? DismissedVersion => null;
        public void SaveDismissed(string version) { }
    }

    private sealed class FixedAppState : IAppState
    {
        public FixedAppState(bool done) => FirstRunDone = done;
        public bool FirstRunDone { get; private set; }
        public void MarkFirstRunDone() => FirstRunDone = true;
        public bool SendCrashReports { get; private set; } = true;
        public void SaveSendCrashReports(bool on) => SendCrashReports = on;
        public AppearancePrefs Appearance { get; private set; } = AppearancePrefs.Default;
        public void SaveAppearance(AppearancePrefs prefs) => Appearance = prefs;
        public string? LastSeenVersion { get; private set; } = Links.AppVersion; // dev runs never open What's new by themselves
        public void MarkVersionSeen(string version) => LastSeenVersion = version;
    }
}

public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess) action();
        else _queue.TryEnqueue(() => action());
    }
}

/// <summary>Reads text from Strings\en-US\Resources.resw. With --pseudo-loc every value is made longer and accented.</summary>
public sealed class ResourceStrings : IStrings
{
    private readonly ResourceLoader _loader = new();
    private readonly bool _pseudo;

    public ResourceStrings(bool pseudo) => _pseudo = pseudo;

    public string Get(string key)
    {
        var text = _loader.GetString(key);
        if (string.IsNullOrEmpty(text)) return key;
        return _pseudo ? PseudoLoc.Convert(text) : text;
    }

    public string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch (FormatException)
        {
            return Get(key);
        }
    }
}

/// <summary>Client-mode flows that need the dialogs and the shared services.</summary>
public static class ClientFlows
{
    /// <summary>Opens the pairing dialog. True when a PC was paired (the link view then refreshes).</summary>
    public static async Task<bool> PairHomePcAsync()
    {
        using var vm = new PairingViewModel(AppServices.Host, AppServices.Ui, AppServices.Strings, Environment.MachineName);
        var ok = await AppServices.Dialogs.ShowPairingAsync(vm);
        if (ok) AppServices.Shell.ClientLink.Poke();
        return ok;
    }

    /// <summary>The one calm prompt: saves the choice and tells the host it was shown, so it is never raised again.</summary>
    public static async Task ShowFallbackPromptAsync()
    {
        var link = AppServices.Shell.ClientLink;
        var useLaptop = await AppServices.Dialogs.ShowFallbackPromptAsync(link.PcName);
        await link.AnswerFallbackPromptAsync(useLaptop);
    }
}

/// <summary>Shows confirmation and approval dialogs one at a time (WinUI allows only one open dialog).</summary>
public sealed partial class WinUiDialogService : IDialogService
{
    private readonly IStrings _s;
    private readonly SemaphoreSlim _one = new(1, 1);

    public WinUiDialogService(IStrings strings) => _s = strings;

    /// <summary>The dialog on screen right now, if any (lets automated checks capture it).</summary>
    public ContentDialog? Current { get; private set; }

    /// <summary>Set by the window once it has content.</summary>
    public XamlRoot? Root { get; set; }

    /// <summary>
    /// Dialogs live in their own popup layer and do not inherit the window's chosen theme, so they would follow
    /// Windows instead of the app. Reading the theme from the root element keeps them in step with Settings.
    /// </summary>
    private ElementTheme ThemeOf() => Root?.Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;

    public async Task<bool> ConfirmAsync(string title, string body, string confirmText, string cancelText)
    {
        if (Root is null) return false;
        await _one.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                FlowDirection = UiDirection.Current,
                RequestedTheme = ThemeOf(),
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirmText,
                CloseButtonText = cancelText,
                DefaultButton = ContentDialogButton.Close,
            };
            Current = dialog;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }

    /// <summary>A short information dialog with one Close button.</summary>
    public async Task ShowInfoAsync(string title, string body, string closeText)
    {
        if (Root is null) return;
        await _one.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                FlowDirection = UiDirection.Current,
                RequestedTheme = ThemeOf(),
                Title = title,
                Content = new ScrollViewer { Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Close,
            };
            Current = dialog;
            await dialog.ShowAsync();
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }

    /// <summary>The access prompt: app name, verified state, Allow or Deny. Closing it without a choice leaves the request waiting.</summary>
    public async Task<bool?> ShowApprovalAsync(PendingApproval a)
    {
        if (Root is null) return null;
        await _one.WaitAsync();
        try
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = _s.Format("Vm_ApprovalBody", ApprovalText.DisplayName(_s, a)), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(a.Reason))
                panel.Children.Add(new TextBlock { Text = _s.Format("Vm_ApprovalReason", a.Reason), TextWrapping = TextWrapping.Wrap });
            if (ApprovalText.PublisherLine(_s, a) is { Length: > 0 } publisher)
                panel.Children.Add(new TextBlock { Text = publisher, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Text = ApprovalText.IdentityShort(_s, a),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = a.CallerVerified ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (ApprovalText.IsPortalApp(a) && !string.IsNullOrWhiteSpace(a.CallerIdentity))
                panel.Children.Add(new Expander
                {
                    Header = _s.Get("Vm_ApprovalDetails"),
                    Content = new TextBlock { Text = a.CallerIdentity, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                FlowDirection = UiDirection.Current,
                RequestedTheme = ThemeOf(),
                Title = _s.Format("Vm_ApprovalTitle", ApprovalText.DisplayName(_s, a)),
                Content = panel,
                PrimaryButtonText = _s.Get("Vm_Allow"),
                SecondaryButtonText = _s.Get("Vm_Deny"),
                CloseButtonText = _s.Get("Vm_Later"),
                DefaultButton = ContentDialogButton.Secondary,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, _s.Format("Vm_ApprovalTitle", ApprovalText.DisplayName(_s, a)));
            Current = dialog;
            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => true,
                ContentDialogResult.Secondary => false,
                _ => null,
            };
        }
        finally
        {
            Current = null;
            _one.Release();
        }
    }
}
