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
    [ObservableProperty] private string _thinking = "";
    [ObservableProperty] private string _error = "";

    /// <summary>Plain-language progress line: shown while waiting, or when the model ended without an answer.</summary>
    [ObservableProperty] private string _status = "";

    public bool HasThinking => Thinking.Length > 0;

    partial void OnThinkingChanged(string value) => OnPropertyChanged(nameof(HasThinking));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    private bool CanSend() => !IsBusy && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        Reply = "";
        Thinking = "";
        Error = "";
        Status = _s.Get("Vm_TryWorking");
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await foreach (var piece in _shell.Host.TryItAsync(Prompt.Trim(), _cts.Token).ConfigureAwait(false))
            {
                _ui.Post(() =>
                {
                    if (piece.IsThinking)
                        Thinking += piece.Text;
                    else
                        Reply += piece.Text;
                    Status = piece.IsThinking && Reply.Length == 0 ? _s.Get("Vm_TryThinking") : "";
                });
            }

            _ui.Post(() => Status = Reply.Trim().Length > 0 ? ""
                : _s.Get(Thinking.Length > 0 ? "Vm_TryOnlyThinking" : "Vm_TryEmpty"));
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() => Status = "");
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() =>
            {
                Status = "";
                Error = _s.Get("Vm_NoHostTitle");
            });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _ui.Post(() =>
            {
                Status = "";
                Error = _s.Format("Vm_TryFailed", e.Message);
            });
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private async Task StopAsync()
    {
        _cts?.Cancel();
        // Also have the host stop generating, so the processor is free again at once.
        try
        {
            await _shell.Host.CancelTryItAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or HostUnavailableException)
        {
        }
    }
}
