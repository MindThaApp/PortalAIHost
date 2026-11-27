using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.ViewModels;

/// <summary>
/// The laptop's "Use my home PC" dialog (docs/client-mode-v1.md section 2): paste a pairing link, check the PC's name
/// and the short form of its fingerprint, confirm, then wait. Errors are plain sentences. The pairing call runs off the
/// UI thread and is cancelled when the dialog closes; nothing is saved by this window (the host saves a verified pairing).
/// </summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    public const int StepLink = 0;
    public const int StepConfirm = 1;
    public const int StepWorking = 2;

    private readonly IHostControl _host;
    private readonly IUiDispatcher _ui;
    private readonly IStrings _s;
    private CancellationTokenSource? _cts;
    private PairingLink? _link;

    public PairingViewModel(IHostControl host, IUiDispatcher ui, IStrings strings, string deviceName)
    {
        _host = host;
        _ui = ui;
        _s = strings;
        _deviceName = deviceName;
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanCheck))] private string _linkText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanConfirm))] private string _deviceName;
    [ObservableProperty] private string _pcName = "";
    [ObservableProperty] private string _fingerprintText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _clipboardHasLink;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnLink))]
    [NotifyPropertyChangedFor(nameof(OnConfirm))]
    [NotifyPropertyChangedFor(nameof(OnWorking))]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int _step = StepLink;

    public bool OnLink => Step == StepLink;
    public bool OnConfirm => Step == StepConfirm;
    public bool OnWorking => Step == StepWorking;
    public bool HasError => Error.Length > 0;
    public bool CanCheck => Step == StepLink && LinkText.Trim().Length > 0;
    public bool CanConfirm => Step == StepConfirm && DeviceName.Trim().Length is >= 1 and <= 64;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Raised on the UI thread after a verified pairing. The PC name is passed along.</summary>
    public event Action<string>? Paired;

    /// <summary>Called by the dialog when it opens: a link on the clipboard highlights "Paste link". Never fills anything by itself.</summary>
    public void NoteClipboard(string? text) =>
        ClipboardHasLink = PairingLink.TryParse(text?.Trim(), out _);

    /// <summary>Puts a pasted link into the box (the user pressed "Paste link").</summary>
    public void UseLink(string? text)
    {
        LinkText = text?.Trim() ?? "";
        Check();
    }

    /// <summary>Reads the link and shows whom this would pair with. Nothing is sent yet.</summary>
    [RelayCommand]
    private void Check()
    {
        Error = "";
        if (!PairingLink.TryParse(LinkText.Trim(), out var link) || link is null)
        {
            Error = _s.Get("Vm_PairErr_invalid_input");
            return;
        }

        _link = link;
        PcName = link.HostName.Length > 0 ? link.HostName : link.Address;
        FingerprintText = ShortFingerprint(link.Fingerprint);
        Step = StepConfirm;
    }

    /// <summary>The first 12 characters of the fingerprint in three groups, enough to compare with the other PC's screen.</summary>
    public static string ShortFingerprint(string fingerprint)
    {
        var head = fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
        return string.Join(' ', Enumerable.Range(0, (head.Length + 3) / 4).Select(i => head.Substring(i * 4, Math.Min(4, head.Length - i * 4))));
    }

    [RelayCommand]
    private void Back()
    {
        if (Step == StepConfirm)
        {
            Error = "";
            Step = StepLink;
        }
    }

    /// <summary>Confirms the PC and pairs. Progress shows while the call runs; a failure goes back to the confirm step with a plain sentence.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm || _link is null) return;
        Error = "";
        Step = StepWorking;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var command = new PairCommand { Link = LinkText.Trim(), DeviceName = DeviceName.Trim() };
        PairResult result;
        try
        {
            result = await Task.Run(() => _host.PairClientAsync(command, cts.Token), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() => Step = StepConfirm);
            return;
        }
        catch (HostUnavailableException)
        {
            _ui.Post(() => { Error = _s.Get("Vm_PairErr_host"); Step = StepConfirm; });
            return;
        }
        catch (HostRequestException)
        {
            _ui.Post(() => { Error = _s.Get("Vm_PairErr_protocol_error"); Step = StepConfirm; });
            return;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }

        _ui.Post(() =>
        {
            if (result.Ok)
            {
                Paired?.Invoke(result.PcName ?? PcName);
                return;
            }

            Error = ErrorText(result);
            Step = StepConfirm;
        });
    }

    /// <summary>A plain sentence for a failed pairing. The reason the PC gave wins over the general error.</summary>
    public string ErrorText(PairResult result)
    {
        if (result.Error == PairErrors.Refused && result.Reason is { Length: > 0 } reason && IsKnownReason(reason))
        {
            return _s.Get("Vm_PairReason_" + reason);
        }

        return result.Error switch
        {
            PairErrors.InvalidInput or PairErrors.AddressNotAllowed or PairErrors.Unreachable or PairErrors.CertificateMismatch
                or PairErrors.HostProofInvalid or PairErrors.Refused or PairErrors.ProtocolError or PairErrors.SaveFailed
                or PairErrors.AlreadyPaired or PairErrors.NotAvailable => _s.Get("Vm_PairErr_" + result.Error),
            _ => _s.Get("Vm_PairErr_protocol_error"),
        };
    }

    private static bool IsKnownReason(string reason) =>
        reason is PairingReasons.Expired or PairingReasons.CodeVoided or PairingReasons.NoOffer
            or PairingReasons.TooManyDevices or PairingReasons.WrongCode;

    /// <summary>Stops a pairing in progress (the Cancel button, or the dialog closing).</summary>
    [RelayCommand]
    private void Cancel() => StopPairing();

    private void StopPairing()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        StopPairing();
    }
}
