using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>Try it page: one prompt box, the reply streams in below it.</summary>
public sealed partial class TryItViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CancellationTokenSource? _cts;

    public TryItViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _prompt = "";

    [ObservableProperty] private string _reply = "";
    [ObservableProperty] private string _error = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    private bool CanSend() => !IsBusy && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        Reply = "";
        Error = "";
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await foreach (var piece in _shell.Host.TryItAsync(Prompt.Trim(), _cts.Token).ConfigureAwait(false))
                _ui.Post(() => Reply += piece);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => Error = _s.Get("Vm_NoHostTitle"));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _ui.Post(() => Error = _s.Format("Vm_TryFailed", e.Message));
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _cts?.Cancel();
}
