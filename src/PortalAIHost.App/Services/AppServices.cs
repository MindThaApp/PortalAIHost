using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Services;

/// <summary>Builds and holds the few shared services. The window and pages read from here.</summary>
public static class AppServices
{
    public static IUiDispatcher Ui { get; private set; } = null!;
    public static IStrings Strings { get; private set; } = null!;
    public static WinUiDialogService Dialogs { get; private set; } = null!;
    public static IHostControl Host { get; private set; } = null!;
    public static ShellViewModel Shell { get; private set; } = null!;
    /// <summary>Opens a page by name (home, models, apps, tryit, settings) and moves the menu selection. Set by the window.</summary>
    public static Action<string>? NavigateTo { get; set; }
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
        }
        IAppState state = AppOptions.SkipFirstRun ? new FixedAppState(true) : new AppStateStore(stateFolder);
        Shell = new ShellViewModel(Host, launcher, Ui, Strings, state);
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

    private sealed class FixedAppState : IAppState
    {
        public FixedAppState(bool done) => FirstRunDone = done;
        public bool FirstRunDone { get; private set; }
        public void MarkFirstRunDone() => FirstRunDone = true;
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

/// <summary>Shows confirmation and approval dialogs one at a time (WinUI allows only one open dialog).</summary>
public sealed class WinUiDialogService : IDialogService
{
    private readonly IStrings _s;
    private readonly SemaphoreSlim _one = new(1, 1);

    public WinUiDialogService(IStrings strings) => _s = strings;

    /// <summary>The dialog on screen right now, if any (lets automated checks capture it).</summary>
    public ContentDialog? Current { get; private set; }

    /// <summary>Set by the window once it has content.</summary>
    public XamlRoot? Root { get; set; }

    public async Task<bool> ConfirmAsync(string title, string body, string confirmText, string cancelText)
    {
        if (Root is null) return false;
        await _one.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
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
            panel.Children.Add(new TextBlock { Text = _s.Format("Vm_ApprovalBody", a.AppName), TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(a.Reason))
                panel.Children.Add(new TextBlock { Text = _s.Format("Vm_ApprovalReason", a.Reason), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = ApprovalText.Publisher(_s, a), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Text = ApprovalText.Identity(_s, a),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = a.CallerVerified ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
            });
            var dialog = new ContentDialog
            {
                XamlRoot = Root,
                Title = _s.Format("Vm_ApprovalTitle", a.AppName),
                Content = panel,
                PrimaryButtonText = _s.Get("Vm_Allow"),
                SecondaryButtonText = _s.Get("Vm_Deny"),
                CloseButtonText = _s.Get("Vm_Later"),
                DefaultButton = ContentDialogButton.Secondary,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, _s.Format("Vm_ApprovalTitle", a.AppName));
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
