using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote;

/// <summary>A pairing step was refused. <see cref="Reason"/> is one of <see cref="PairingReasons"/>.</summary>
public sealed class PairingException(string reason) : Exception("Pairing was refused: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>An open pairing code. The code is shown to the user once and never logged.</summary>
public sealed record PairingOffer(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// One-time pairing codes and the two-step handshake (docs/remote-v1.md section 3). A code lives 5 minutes and
/// serves one device. The client proves it knows the code in the first message, so wrong guesses are counted before the
/// host reveals anything derived from the code; the fifth wrong proof voids the code. All proofs are compared in
/// constant time.
/// </summary>
public sealed class PairingService
{
    private readonly DeviceStore _devices;
    private readonly string _hostName;
    private readonly byte[] _fingerprint;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Offer? _offer;

    /// <param name="fingerprint">SHA-256 of the host certificate (32 bytes).</param>
    public PairingService(DeviceStore devices, string hostName, byte[] fingerprint, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(fingerprint);
        _devices = devices;
        _hostName = hostName;
        _fingerprint = fingerprint;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a code is opened, voided, cancelled or used.</summary>
    public event Action? Changed;

    /// <summary>The open code, or null when there is none, it expired or it was voided.</summary>
    public PairingOffer? Current
    {
        get
        {
            lock (_gate)
            {
                var offer = LiveOfferLocked(out _);
                return offer is { Voided: false } ? new PairingOffer(offer.Code, offer.ExpiresAt) : null;
            }
        }
    }

    /// <summary>Opens a new code. An earlier open code stops working.</summary>
    public PairingOffer Begin()
    {
        PairingOffer result;
        lock (_gate)
        {
            var code = PairingCrypto.NewCode(_fingerprint);
            _offer = new Offer(code, PairingCrypto.DeriveKey(code),
                _time.GetUtcNow().AddMinutes(RemoteApi.CodeLifetimeMinutes));
            result = new PairingOffer(code, _offer.ExpiresAt);
        }

        Changed?.Invoke();
        return result;
    }

    public void Cancel()
    {
        bool had;
        lock (_gate)
        {
            had = _offer is not null;
            _offer = null;
        }

        if (had)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Step 1. Returns the host's answer; throws <see cref="PairingException"/> when refused.</summary>
    public PairStartResponse Start(PairStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var changed = false;
        try
        {
            lock (_gate)
            {
                var offer = LiveOfferLocked(out var expired) ??
                    throw new PairingException(expired ? PairingReasons.Expired : PairingReasons.NoOffer);
                if (offer.Voided)
                {
                    throw new PairingException(PairingReasons.CodeVoided);
                }

                if (!RemoteCodec.TryDecode(request.ClientNonce, 64, out var clientNonce) || clientNonce.Length < 16 ||
                    !RemoteCodec.TryDecode(request.DevicePublicKey, 256, out var publicKey) ||
                    !RemoteCodec.TryDecode(request.Mac, 32, out var mac) || mac.Length != 32 ||
                    NameOf(request.DeviceName) is not { } name)
                {
                    throw new PairingException(PairingReasons.Invalid);
                }

                using (var key = RemoteSigning.ImportPublicKey(publicKey))
                {
                    if (key is null)
                    {
                        throw new PairingException(PairingReasons.Invalid);
                    }
                }

                var nameBytes = Encoding.UTF8.GetBytes(name);
                var expected = PairingCrypto.Mac(offer.Key, PairingCrypto.StartLabel, clientNonce, nameBytes, publicKey);
                if (!CryptographicOperations.FixedTimeEquals(expected, mac))
                {
                    changed = WrongAttemptLocked(offer);
                    throw new PairingException(offer.Voided ? PairingReasons.CodeVoided : PairingReasons.WrongCode);
                }

                if (_devices.Count >= DeviceStore.MaxDevices)
                {
                    throw new PairingException(PairingReasons.TooManyDevices);
                }

                var hostNonce = RandomNumberGenerator.GetBytes(32);
                offer.Session = new Session(clientNonce, name, publicKey, hostNonce);
                return new PairStartResponse
                {
                    HostNonce = RemoteCodec.Encode(hostNonce),
                    Fingerprint = RemoteCodec.Encode(_fingerprint),
                    HostName = _hostName,
                    Mac = RemoteCodec.Encode(PairingCrypto.Mac(offer.Key, PairingCrypto.HostLabel,
                        clientNonce, nameBytes, publicKey, hostNonce, _fingerprint)),
                };
            }
        }
        finally
        {
            if (changed)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Step 2. On success the device is stored and the code is used up.</summary>
    public PairFinishResponse Finish(PairFinishRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PairFinishResponse response;
        var changed = false;
        try
        {
            lock (_gate)
            {
                var offer = LiveOfferLocked(out var expired) ??
                    throw new PairingException(expired ? PairingReasons.Expired : PairingReasons.NoOffer);
                if (offer.Voided)
                {
                    throw new PairingException(PairingReasons.CodeVoided);
                }

                if (!RemoteCodec.TryDecode(request.HostNonce, 64, out var hostNonce) ||
                    !RemoteCodec.TryDecode(request.Mac, 32, out var mac) || mac.Length != 32)
                {
                    throw new PairingException(PairingReasons.Invalid);
                }

                if (offer.Session is not { } session ||
                    !CryptographicOperations.FixedTimeEquals(session.HostNonce, hostNonce))
                {
                    throw new PairingException(PairingReasons.NoSession);
                }

                var nameBytes = Encoding.UTF8.GetBytes(session.DeviceName);
                var expected = PairingCrypto.Mac(offer.Key, PairingCrypto.FinishLabel,
                    session.ClientNonce, nameBytes, session.PublicKey, session.HostNonce, _fingerprint);
                if (!CryptographicOperations.FixedTimeEquals(expected, mac))
                {
                    changed = WrongAttemptLocked(offer);
                    throw new PairingException(offer.Voided ? PairingReasons.CodeVoided : PairingReasons.WrongCode);
                }

                var device = _devices.Add(session.DeviceName, session.PublicKey);
                var done = PairingCrypto.Mac(offer.Key, PairingCrypto.DoneLabel,
                    session.ClientNonce, nameBytes, session.PublicKey, session.HostNonce, _fingerprint,
                    Encoding.ASCII.GetBytes(device.Id));
                response = new PairFinishResponse { DeviceId = device.Id, Mac = RemoteCodec.Encode(done) };
                _offer = null;
                changed = true;
            }
        }
        finally
        {
            if (changed)
            {
                Changed?.Invoke();
            }
        }

        return response;
    }

    /// <summary>Counts a wrong proof; the fifth voids the code. True when the state shown to the user changed.</summary>
    private static bool WrongAttemptLocked(Offer offer)
    {
        offer.Wrong++;
        if (offer.Wrong >= RemoteApi.MaxWrongAttempts)
        {
            offer.Voided = true;
            offer.Session = null;
            return true;
        }

        return false;
    }

    private Offer? LiveOfferLocked(out bool expired)
    {
        expired = false;
        if (_offer is null)
        {
            return null;
        }

        if (_time.GetUtcNow() >= _offer.ExpiresAt)
        {
            _offer = null;
            expired = true;
            return null;
        }

        return _offer;
    }

    /// <summary>Trimmed name of 1 to 64 characters without control or format characters, else null.</summary>
    internal static string? NameOf(string? raw)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > RemoteApi.MaxDeviceNameLength || !Auth.TextRules.IsDisplaySafe(name))
        {
            return null;
        }

        return name;
    }

    private sealed class Offer(string code, byte[] key, DateTimeOffset expiresAt)
    {
        public string Code { get; } = code;
        public byte[] Key { get; } = key;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Wrong { get; set; }
        public bool Voided { get; set; }
        public Session? Session { get; set; }
    }

    private sealed record Session(byte[] ClientNonce, string DeviceName, byte[] PublicKey, byte[] HostNonce);
}
