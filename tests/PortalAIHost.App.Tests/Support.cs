using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>Returns the key plus its arguments, so tests can check which text was chosen.</summary>
internal sealed class KeyStrings : IStrings
{
    public string Get(string key) => key;
    public string Format(string key, params object[] args) => args.Length == 0 ? key : key + ":" + string.Join("|", args);
}

internal sealed class ScriptedDialogs : IDialogService
{
    public bool Answer { get; set; }
    public int Asked { get; private set; }

    public Task<bool> ConfirmAsync(string title, string body, string confirmText, string cancelText)
    {
        Asked++;
        return Task.FromResult(Answer);
    }
}

internal sealed class MemoryState : IAppState
{
    public bool FirstRunDone { get; private set; }
    public void MarkFirstRunDone() => FirstRunDone = true;
}

internal sealed class Rig : IDisposable
{
    public Rig(FakeScenario scenario, bool launcherSucceeds = false)
    {
        Host = new FakeHostControl(scenario, autoTick: false);
        Launcher = new FakeHostLauncher(Host, launcherSucceeds);
        State = new MemoryState();
        Shell = new ShellViewModel(Host, Launcher, new InlineDispatcher(), Strings, State);
    }

    public FakeHostControl Host { get; }
    public FakeHostLauncher Launcher { get; }
    public MemoryState State { get; }
    public ShellViewModel Shell { get; }
    public KeyStrings Strings { get; } = new();
    public InlineDispatcher Ui { get; } = new();
    public ScriptedDialogs Dialogs { get; } = new();

    public void Dispose()
    {
        Shell.Dispose();
        Host.Dispose();
    }
}
