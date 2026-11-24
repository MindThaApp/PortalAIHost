using System.Globalization;
using System.Security.Cryptography;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote;

/// <summary>Outcome of checking a signed request: the device, or one of <see cref="DeviceAuthReasons"/>.</summary>
public readonly record struct DeviceAuthResult(PairedDevice? Device, string? FailureReason, int RetryAfterMs = 0)
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

    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly DeviceStore _devices;
    private readonly TimeProvider _time;
    private readonly DeviceRequestLimits _limits;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset Expires, string DeviceId, string Nonce)> _expiry = new();

    public DeviceRequestVerifier(DeviceStore devices, TimeProvider? time = null, DeviceRequestLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
        _time = time ?? TimeProvider.System;
        _limits = limits ?? new DeviceRequestLimits();
    }

    private sealed class DeviceState
    {
        public HashSet<string> Nonces { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset WindowStart { get; set; }
        public int WindowCount { get; set; }
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

        // Only a request with a valid signature is counted or remembered, so strangers cannot use up a device's
        // allowance or fill the memory. Every device has its own request rate and its own nonce budget.
        lock (_gate)
        {
            ForgetExpired(now);
            if (!_states.TryGetValue(deviceId, out var state))
            {
                _states[deviceId] = state = new DeviceState { WindowStart = now };
            }

            if (now - state.WindowStart >= RateWindow)
            {
                state.WindowStart = now;
                state.WindowCount = 0;
            }

            if (state.WindowCount >= _limits.MaxRequestsPerMinute)
            {
                return Fail(DeviceAuthReasons.RateLimited, (int)Math.Max(1000, (RateWindow - (now - state.WindowStart)).TotalMilliseconds));
            }

            state.WindowCount++;
            if (state.Nonces.Contains(nonce))
            {
                return Fail(DeviceAuthReasons.Replayed);
            }

            // A record is never dropped before it expires: when the budget is used up the request is refused.
            if (state.Nonces.Count >= _limits.MaxNoncesPerDevice)
            {
                return Fail(DeviceAuthReasons.RateLimited, 5000);
            }

            state.Nonces.Add(nonce);
            _expiry.Enqueue((now + NonceMemory, deviceId, nonce));
        }

        _devices.Touch(device.Id);
        return new DeviceAuthResult(device, null);
    }

    /// <summary>Forgets nonces whose time is up, oldest first, by reading only the front of the queue.</summary>
    private void ForgetExpired(DateTimeOffset now)
    {
        while (_expiry.TryPeek(out var entry) && entry.Expires <= now)
        {
            _expiry.Dequeue();
            if (_states.TryGetValue(entry.DeviceId, out var state))
            {
                state.Nonces.Remove(entry.Nonce);
                if (state.Nonces.Count == 0 && now - state.WindowStart >= RateWindow)
                {
                    _states.Remove(entry.DeviceId);
                }
            }
        }
    }

    private static DeviceAuthResult Fail(string reason, int retryAfterMs = 0) => new(null, reason, retryAfterMs);
}

/// <summary>Per-device limits of the LAN listener. The defaults suit a person using a chat app.</summary>
public sealed record DeviceRequestLimits(int MaxRequestsPerMinute = 600, int MaxNoncesPerDevice = 4000);