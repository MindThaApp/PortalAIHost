using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Portal.AiHost.Contracts;

namespace Portal.AiHost.Core.Remote.Client;

public enum PairingError
{
    None,
    InvalidInput,
    AddressNotAllowed,
    Unreachable,
    CertificateMismatch,
    HostProofInvalid,
    Refused,
    ProtocolError,
    SaveFailed,
}

/// <summary>The result of a pairing attempt. Expected failures are values, not exceptions.</summary>
public sealed record PairingOutcome(PairingError Error, string Message, string? Reason = null, ClientRemote? Remote = null)
{
    public bool Succeeded => Error == PairingError.None;
}

/// <summary>What to pair with. Build it with <see cref="FromLink"/> or <see cref="FromTypedCode"/>.</summary>
public sealed record PairingTarget(
    string Address, int Port, string Code, string? Fingerprint, string? RelayUrl, string? HostNameHint, string DeviceName)
{
    public static PairingTarget FromLink(PairingLink link, string deviceName) =>
        new(link.Address, link.Port, link.Code, link.Fingerprint, link.RelayUrl, link.HostName, deviceName);

    /// <summary>A typed address and code: no fingerprint, and never a relay (LAN only).</summary>
    public static PairingTarget FromTypedCode(string address, int? port, string code, string deviceName) =>
        new(address.Trim(), port ?? RemoteApi.DefaultPort, code, null, null, null, deviceName);
}

/// <summary>
/// The device side of docs/remote-v1.md section 3 (docs/client-mode-v1.md section 2.3). Nothing is written until the
/// whole handshake has been verified; a failure at any step leaves no files.
/// </summary>
public sealed class DevicePairingClient(IRemoteConnector connector, ClientStateStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Tests only: lets the loopback address count as a private address.</summary>
    internal bool AllowLoopback { get; init; }

    public async Task<PairingOutcome> PairAsync(PairingTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Validate(target) is { } invalid)
        {
            return invalid;
        }

        var canonical = PairingCrypto.Normalize(target.Code)!;
        var pinned = target.Fingerprint is null ? null : DecodeFingerprint(target.Fingerprint);
        var resolved = await ResolveAsync(target.Address, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return new PairingOutcome(PairingError.AddressNotAllowed,
                "This address is not on your home network. Remote use goes through the relay.");
        }

        byte[]? seen = null; // DER of the certificate the server presented.
        byte[]? expected = pinned; // Full SHA-256 once known (from the link, later also from the start reply).
        var rejected = false;

        bool Accept(byte[] der)
        {
            var hash = SHA256.HashData(der);
            var ok = PairingCrypto.CodeMatchesCertificate(canonical, hash) &&
                (expected is null || CryptographicOperations.FixedTimeEquals(hash, expected));
            if (ok)
            {
                seen = der;
            }
            else
            {
                rejected = true;
            }

            return ok;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(16);
        var name = Encoding.UTF8.GetBytes(target.DeviceName);
        var macKey = PairingCrypto.DeriveKey(canonical);

        try
        {
            using var channel = connector.Open(resolved, target.Port, Accept);

            var start = new PairStartRequest
            {
                ClientNonce = RemoteCodec.Encode(nonce),
                DeviceName = target.DeviceName,
                DevicePublicKey = RemoteCodec.Encode(publicKey),
                Mac = RemoteCodec.Encode(PairingCrypto.Mac(macKey, PairingCrypto.StartLabel, nonce, name, publicKey)),
            };
            var startReply = await channel.PostJsonAsync(RemoteApi.PairStartPath,
                JsonSerializer.Serialize(start, AiHostJsonContext.Default.PairStartRequest), ct).ConfigureAwait(false);
            if (startReply.Status != 200)
            {
                return Refused(startReply);
            }

            var startBody = JsonSerializer.Deserialize(startReply.Body, AiHostJsonContext.Default.PairStartResponse);
            if (startBody is null || seen is null ||
                !RemoteCodec.TryDecode(startBody.Fingerprint, 32, out var fingerprint) || fingerprint.Length != 32 ||
                !RemoteCodec.TryDecode(startBody.HostNonce, 64, out var hostNonce) || hostNonce.Length == 0 ||
                !RemoteCodec.TryDecode(startBody.Mac, 32, out var hostMac))
            {
                return new PairingOutcome(PairingError.ProtocolError, "That PC sent an unexpected answer.");
            }

            // The fingerprint the host names must be the certificate we are talking to (and the one from the link).
            if (!CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(seen)))
            {
                return Mismatch();
            }

            expected = fingerprint;
            var hostProof = PairingCrypto.Mac(macKey, PairingCrypto.HostLabel, nonce, name, publicKey, hostNonce, fingerprint);
            if (!CryptographicOperations.FixedTimeEquals(hostProof, hostMac))
            {
                return new PairingOutcome(PairingError.HostProofInvalid, "This PC could not be verified.");
            }

            var finish = new PairFinishRequest
            {
                HostNonce = startBody.HostNonce,
                Mac = RemoteCodec.Encode(PairingCrypto.Mac(macKey, PairingCrypto.FinishLabel, nonce, name, publicKey,
                    hostNonce, fingerprint)),
            };
            var finishReply = await channel.PostJsonAsync(RemoteApi.PairFinishPath,
                JsonSerializer.Serialize(finish, AiHostJsonContext.Default.PairFinishRequest), ct).ConfigureAwait(false);
            if (finishReply.Status != 200)
            {
                return Refused(finishReply);
            }

            var done = JsonSerializer.Deserialize(finishReply.Body, AiHostJsonContext.Default.PairFinishResponse);
            if (done is null || string.IsNullOrEmpty(done.DeviceId) || done.DeviceId.Length > 64 ||
                !RemoteCodec.TryDecode(done.Mac, 32, out var doneMac))
            {
                return new PairingOutcome(PairingError.ProtocolError, "That PC sent an unexpected answer.");
            }

            var doneProof = PairingCrypto.Mac(macKey, PairingCrypto.DoneLabel, nonce, name, publicKey, hostNonce, fingerprint,
                Encoding.ASCII.GetBytes(done.DeviceId));
            if (!CryptographicOperations.FixedTimeEquals(doneProof, doneMac))
            {
                return new PairingOutcome(PairingError.HostProofInvalid, "This PC could not be verified.");
            }

            var remote = new ClientRemote(
                string.IsNullOrWhiteSpace(startBody.HostName) ? target.HostNameHint ?? target.Address : startBody.HostName,
                resolved, target.Port, target.RelayUrl, RemoteCodec.Encode(fingerprint), done.DeviceId, _time.GetUtcNow());
            var pkcs8 = key.ExportPkcs8PrivateKey();
            try
            {
                store.SavePairing(remote, pkcs8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return new PairingOutcome(PairingError.SaveFailed, "The pairing worked, but it could not be saved on this PC.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            return new PairingOutcome(PairingError.None, "Paired.", Remote: remote);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException
            or JsonException or System.Security.Authentication.AuthenticationException)
        {
            return rejected
                ? Mismatch()
                : new PairingOutcome(PairingError.Unreachable, "Could not reach that address.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static PairingOutcome Mismatch() =>
        new(PairingError.CertificateMismatch, "This PC could not be verified.");

    private PairingOutcome? Validate(PairingTarget t)
    {
        if (PairingCrypto.Normalize(t.Code) is null)
        {
            return new PairingOutcome(PairingError.InvalidInput, "The code is not right. It has 12 letters and numbers.");
        }

        if (t.Fingerprint is not null && DecodeFingerprint(t.Fingerprint) is null)
        {
            return new PairingOutcome(PairingError.InvalidInput, "The link is not valid.");
        }

        if (t.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(t.Address))
        {
            return new PairingOutcome(PairingError.InvalidInput, "Enter the address of the PC.");
        }

        if (t.DeviceName.Length is < 1 or > 64 || t.DeviceName.Any(char.IsControl))
        {
            return new PairingOutcome(PairingError.InvalidInput, "The device name must be 1 to 64 characters.");
        }

        return null;
    }

    private static byte[]? DecodeFingerprint(string text) =>
        RemoteCodec.TryDecode(text, 32, out var bytes) && bytes.Length == 32 ? bytes : null;

    /// <summary>The first private (or, in tests, loopback) address for the name, as text; null when there is none.</summary>
    private async Task<string?> ResolveAsync(string address, CancellationToken ct)
    {
        IPAddress[] candidates;
        if (IPAddress.TryParse(address, out var ip))
        {
            candidates = [ip];
        }
        else
        {
            try
            {
                candidates = await Dns.GetHostAddressesAsync(address, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return null;
            }
        }

        var allowed = candidates.FirstOrDefault(a => RemoteNetwork.IsPrivateAddress(a) || AllowLoopback && IPAddress.IsLoopback(a));
        return allowed?.ToString();
    }

    private static PairingOutcome Refused(RemoteReply reply)
    {
        string? reason = null;
        try
        {
            using var doc = JsonDocument.Parse(reply.Body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("details", out var details) &&
                details.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reason = r.GetString();
            }
        }
        catch (JsonException)
        {
        }

        var message = reason switch
        {
            PairingReasons.Expired => "The code ran out. Ask for a new one.",
            PairingReasons.CodeVoided => "That code was used up by wrong tries. Ask for a new one.",
            PairingReasons.NoOffer => "That PC is not waiting for a device. Open a new code on it.",
            PairingReasons.TooManyDevices => "That PC already has 16 devices. Remove one first.",
            PairingReasons.WrongCode => "The code is not right.",
            _ => "That PC refused the pairing.",
        };
        return new PairingOutcome(PairingError.Refused, message, reason);
    }
}
