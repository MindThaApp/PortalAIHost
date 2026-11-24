using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>One bubble of the Try it conversation. Lives in memory only.</summary>
public sealed partial class TryItMessage : ObservableObject
{
    public TryItMessage(bool isUser, string text = "")
    {
        IsUser = isUser;
        _text = text;
    }

    public bool IsUser { get; }
    public bool IsAssistant => !IsUser;

    [ObservableProperty] private string _text;
    [ObservableProperty] private string _thinking = "";

    public bool HasThinking => Thinking.Length > 0;

    partial void OnThinkingChanged(string value) => OnPropertyChanged(nameof(HasThinking));
}

/// <summary>
/// Try it page: a small test bench for the model. One conversation kept in memory (never saved), streamed token by token,
/// with the time to the first words and the tokens per second after each answer. Not a chat product.
/// </summary>
public sealed partial class TryItViewModel : ObservableObject
{
    /// <summary>Turns sent back to the model with a new question; older ones are left out so the context stays small.</summary>
    public const int MaxHistoryTurns = 6;

    private readonly ShellViewModel _shell;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CancellationTokenSource? _cts;

    public TryItViewModel(ShellViewModel shell, IUiDispatcher ui, IStrings strings)
    {
        _shell = shell;
        _ui = ui;
        _s = strings;
        Suggestions = [_s.Get("Vm_TrySuggest1"), _s.Get("Vm_TrySuggest2"), _s.Get("Vm_TrySuggest3")];
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(ShowSuggestions));
            ClearCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>The conversation so far, oldest first.</summary>
    public ObservableCollection<TryItMessage> Messages { get; } = new();

    /// <summary>A few prompts to start with.</summary>
    public IReadOnlyList<string> Suggestions { get; }

    public bool HasMessages => Messages.Count > 0;
    public bool ShowSuggestions => Messages.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _prompt = "";

    /// <summary>The latest answer's text.</summary>
    [ObservableProperty] private string _reply = "";

    /// <summary>The latest answer's thinking text.</summary>
    [ObservableProperty] private string _thinking = "";
    [ObservableProperty] private string _error = "";

    /// <summary>Plain-language progress line: shown while waiting, or when the model ended without an answer.</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>After an answer: how long the first words took and how fast the rest came.</summary>
    [ObservableProperty] private string _stats = "";

    /// <summary>The numbers behind <see cref="Stats"/>, or null before the first finished answer.</summary>
    [ObservableProperty] private TryItStats? _lastStats;

    public bool HasThinking => Thinking.Length > 0;
    public bool HasStats => Stats.Length > 0;

    partial void OnThinkingChanged(string value) => OnPropertyChanged(nameof(HasThinking));

    partial void OnStatsChanged(string value) => OnPropertyChanged(nameof(HasStats));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand))]
    private bool _isBusy;

    private bool CanSend() => !IsBusy && Prompt.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Prompt.Trim();
        Prompt = "";
        Reply = "";
        Thinking = "";
        Error = "";
        Stats = "";
        LastStats = null;
        Status = _s.Get("Vm_TryWorking");
        IsBusy = true;

        // Earlier turns go back to the model so a follow-up makes sense; only the answer text, never the thinking.
        var turns = Messages.Where(m => m.Text.Length > 0).TakeLast(MaxHistoryTurns)
            .Select(m => new TryItTurn(m.IsUser, m.Text)).ToList();
        while (turns.Count > 0 && !turns[0].IsUser) turns.RemoveAt(0);
        turns.Add(new TryItTurn(true, text));
        Messages.Add(new TryItMessage(true, text));
        var answer = new TryItMessage(false);
        Messages.Add(answer);

        var cts = _cts = new CancellationTokenSource();
        string rawThinking = "", rawReply = "";
        try
        {
            await foreach (var piece in _shell.Host.TryItAsync(turns, cts.Token).ConfigureAwait(false))
            {
                if (piece.Stats is { } stats)
                {
                    _ui.Post(() => ShowStats(stats));
                    continue;
                }

                if (piece.IsThinking) rawThinking += piece.Text;
                else rawReply += piece.Text;
                var (th, ans) = TryItText.Split(rawThinking.Length > 0 ? rawThinking : null, rawReply);
                var thinkingNow = piece.IsThinking && ans.Length == 0;
                _ui.Post(() =>
                {
                    answer.Thinking = th;
                    answer.Text = ans;
                    Thinking = th;
                    Reply = ans;
                    Status = thinkingNow ? _s.Get("Vm_TryThinking") : "";
                });
            }

            _ui.Post(() =>
            {
                if (answer.Text.Trim().Length > 0)
                {
                    Status = "";
                    return;
                }

                Status = _s.Get(answer.Thinking.Length > 0 ? "Vm_TryOnlyThinking" : "Vm_TryEmpty");
                Messages.Remove(answer);
            });
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() => AfterStop(answer));
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() =>
            {
                Status = "";
                Error = _s.Get("Vm_NoHostTitle");
                Messages.Remove(answer);
            });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or HostRequestException)
        {
            _ui.Post(() =>
            {
                Status = "";
                Error = _s.Format("Vm_TryFailed", e.Message);
                Messages.Remove(answer);
            });
        }
        finally
        {
            _ui.Post(() =>
            {
                IsBusy = false;
                if (ReferenceEquals(_cts, cts)) _cts = null;
                cts.Dispose();
            });
        }
    }

    private void AfterStop(TryItMessage answer)
    {
        Status = "";
        // A stopped answer with no words yet leaves no empty bubble behind.
        if (answer.Text.Trim().Length == 0) Messages.Remove(answer);
    }

    private void ShowStats(TryItStats stats)
    {
        LastStats = stats;
        if (stats.FirstTokenMs is not { } first)
        {
            Stats = "";
            return;
        }

        var seconds = (first / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);
        var line = stats.TokensPerSecond is { } rate
            ? _s.Format("Vm_TryStats", seconds, rate.ToString("0.#", CultureInfo.CurrentCulture))
            : _s.Format("Vm_TryStatsShort", seconds);
        Stats = stats.Cancelled ? _s.Format("Vm_TryStatsStopped", line) : line;
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

    private bool CanClear() => Messages.Count > 0 || IsBusy;

    /// <summary>Forgets the conversation (it was never saved anywhere). A running answer is stopped first.</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync()
    {
        if (IsBusy) await StopAsync().ConfigureAwait(false);
        _ui.Post(() =>
        {
            Messages.Clear();
            Reply = "";
            Thinking = "";
            Error = "";
            Status = "";
            Stats = "";
            LastStats = null;
        });
    }

    /// <summary>A suggested prompt: puts it in the box and asks.</summary>
    [RelayCommand]
    private async Task UseSuggestionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy) return;
        Prompt = text;
        await SendAsync().ConfigureAwait(false);
    }
}
