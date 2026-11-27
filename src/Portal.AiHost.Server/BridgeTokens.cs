using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace Portal.AiHost.Server;

/// <summary>
/// One-time admission tokens for the internal bridge listener. The relay bridge asks for a token before it opens each
/// local connection and writes the 32 bytes before the TLS handshake; the listener accepts the connection only when
/// the token is one it issued, is still fresh and has not been used. Anything else is closed without a reply, so a
/// program on this PC that finds the port cannot use up connection slots or probe the TLS stack.
/// </summary>
public sealed class BridgeTokens
{
    public const int TokenBytes = 32;
    private const int MaxOutstanding = 64;

    /// <summary>How long a token stays valid after it was issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly List<(byte[] Token, long Expires)> _issued = [];
    private readonly TimeProvider _time;

    public BridgeTokens(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>A fresh random token, valid once for <see cref="Lifetime"/>. The oldest is dropped past 64 outstanding.</summary>
    public byte[] Issue()
    {
        var token = RandomNumberGenerator.GetBytes(TokenBytes);
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            Purge(now);
            if (_issued.Count >= MaxOutstanding) Remove(0);
            _issued.Add(((byte[])token.Clone(), now + (long)(Lifetime.TotalSeconds * _time.TimestampFrequency)));
        }

        return token; // the list keeps its own copy, so the caller may wipe this one
    }

    /// <summary>True once for each issued, unexpired token; the token is removed whatever the outcome for a match.</summary>
    public bool TryConsume(ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length != TokenBytes) return false;
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            Purge(now);
            var hit = -1;
            // Compare against every entry (no early exit) so the time taken does not say which one matched.
            for (var i = 0; i < _issued.Count; i++)
            {
                if (CryptographicOperations.FixedTimeEquals(_issued[i].Token, candidate)) hit = i;
            }

            if (hit < 0) return false;
            Remove(hit);
            return true;
        }
    }

    // Best effort only: it clears the copy this list holds. Copies the runtime made while the bytes travelled are not tracked.
    private void Remove(int index)
    {
        CryptographicOperations.ZeroMemory(_issued[index].Token);
        _issued.RemoveAt(index);
    }

    private void Purge(long now)
    {
        for (var i = _issued.Count - 1; i >= 0; i--)
        {
            if (_issued[i].Expires <= now) Remove(i);
        }
    }

    /// <summary>
    /// Reads the first 32 bytes of a connection and checks them. Waits at most <paramref name="wait"/>; on any failure
    /// the caller closes the connection without writing anything.
    /// </summary>
    public async Task<bool> ReadAndConsumeAsync(PipeReader input, TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            var r = await input.ReadAtLeastAsync(TokenBytes, cts.Token).ConfigureAwait(false);
            if (r.Buffer.Length < TokenBytes) return false;
            Span<byte> token = stackalloc byte[TokenBytes];
            r.Buffer.Slice(0, TokenBytes).CopyTo(token);
            input.AdvanceTo(r.Buffer.GetPosition(TokenBytes));
            var ok = TryConsume(token);
            CryptographicOperations.ZeroMemory(token);
            return ok;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
