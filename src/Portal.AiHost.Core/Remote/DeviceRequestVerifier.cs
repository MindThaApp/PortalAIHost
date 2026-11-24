using System.Globalization;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote;

/// <summary>Outcome of checking a signed request: the device, or one of <see cref="DeviceAuthReasons"/>.</summary>
public readonly record struct DeviceAuthResult(PairedDevice? Device, string? FailureReason)
{
    public bool IsValid => Device is not null;
}

/// <summary>
/// Checks the signature headers of a request from a paired device: known device, time within 2 minutes, valid ECDSA
/// signature over method, target, time, nonce and body hash, and a nonce not seen before. A removed device is simply
/// no longer known.
/// </summary>
public sealed class DeviceRequestVerifier
{
    private const int MaxTargetLength = 4096;

    /// <summary>How long a nonce is remembered: longer than the time a signed request stays acceptable (2 x skew).</summary>
    private static readonly TimeSpan NonceMemory = TimeSpan.FromSeconds(2 * RemoteApi.MaxClockSkewSeconds + 60);

    private const int MaxRememberedNonces = 100_000;

    private readonly DeviceStore _devices;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune;

    public DeviceRequestVerifier(DeviceStore devices, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="target">The request target exactly as sent (path and query, not decoded).</param>
    public DeviceAuthResult Verify(string method, string target, IReadOnlyDictionary<string, string> headers,
        ReadOnlySpan<byte> body)
    {
        if (target.Length is 0 or > MaxTargetLength ||
            !headers.TryGetValue(RemoteHeaders.Device, out var deviceId) ||
            !headers.TryGetValue(RemoteHeaders.Timestamp, out var timestamp) ||
            !headers.TryGetValue(RemoteHeaders.Nonce, out var nonce) ||
            !headers.TryGetValue(RemoteHeaders.Signature, out var signatureText) ||
            deviceId.Length is 0 or > 64 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !RemoteCodec.TryDecode(nonce, 48, out var nonceBytes) || nonceBytes.Length < 16 ||
            !RemoteCodec.TryDecode(signatureText, 64, out var signature) || signature.Length != 64)
        {
            return Fail(DeviceAuthReasons.Malformed);
        }

        var device = _devices.Find(deviceId);
        if (device is null)
        {
            return Fail(DeviceAuthReasons.UnknownDevice);
        }

        var now = _time.GetUtcNow();
        long nowSeconds = now.ToUnixTimeSeconds();
        if (Math.Abs(nowSeconds - seconds) > RemoteApi.MaxClockSkewSeconds)
        {
            return Fail(DeviceAuthReasons.StaleTimestamp);
        }

        if (!RemoteCodec.TryDecode(device.PublicKey, 256, out var spki) || RemoteSigning.ImportPublicKey(spki) is not { } key)
        {
            return Fail(DeviceAuthReasons.UnknownDevice);
        }

        using (key)
        {
            var signed = RemoteSigning.SigningString(method, target, timestamp, nonce, body);
            if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Fail(DeviceAuthReasons.BadSignature);
            }
        }

        // Only a request with a valid signature is remembered, so strangers cannot fill the memory.
        lock (_gate)
        {
            Prune(now);
            if (_seen.Count >= MaxRememberedNonces || !_seen.TryAdd(deviceId + "|" + nonce, now + NonceMemory))
            {
                return Fail(DeviceAuthReasons.Replayed);
            }
        }

        _devices.Touch(device.Id);
        return new DeviceAuthResult(device, null);
    }

    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromSeconds(30) && _seen.Count < MaxRememberedNonces)
        {
            return;
        }

        _lastPrune = now;
        foreach (var expired in _seen.Where(p => p.Value <= now).Select(p => p.Key).ToList())
        {
            _seen.Remove(expired);
        }
    }

    private static DeviceAuthResult Fail(string reason) => new(null, reason);
}
