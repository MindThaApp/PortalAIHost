namespace Portal.AiHost.Core.Remote;

/// <summary>
/// One cancellation source per paired device, linked into every remote request the device has in flight. Removing the
/// device cancels them, so queued and running generations and open streams end at once. Admission and removal use
/// the same lock and admission re-checks the device store, so a request cannot slip in after the removal: the store
/// forgets the device first, then <see cref="Revoke"/> cancels what was admitted before that.
/// </summary>
public sealed class DeviceSessions(DeviceStore devices)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _sources = new(StringComparer.Ordinal);

    /// <summary>False when the device is not (or no longer) paired. Otherwise the token is cancelled on removal.</summary>
    public bool TryEnter(string deviceId, out CancellationToken revoked)
    {
        lock (_gate)
        {
            if (devices.Find(deviceId) is null)
            {
                revoked = default;
                return false;
            }

            if (!_sources.TryGetValue(deviceId, out var source))
            {
                _sources[deviceId] = source = new CancellationTokenSource();
            }

            revoked = source.Token;
            return true;
        }
    }

    /// <summary>Call after the device was removed from the store. Cancels everything it has in flight.</summary>
    public void Revoke(string deviceId)
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            _sources.Remove(deviceId, out source);
        }

        source?.Cancel();
    }
}
