using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;

namespace Portal.AiHost.Core.Remote;

/// <summary>Which network addresses may use remote access, and which address this PC shows to a new device.</summary>
public static class RemoteNetwork
{
    /// <summary>
    /// True for a home or office network address: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, link-local
    /// (169.254.0.0/16, fe80::/10) and unique local IPv6 (fc00::/7). Loopback, public and unspecified addresses are not.
    /// </summary>
    public static bool IsPrivateAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }

        return false;
    }

    /// <summary>This PC's private IPv4 addresses, those on a network with a gateway first (the real home network).</summary>
    public static List<string> LanAddresses()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        IsPrivateAddress(unicast.Address))
                    {
                        (hasGateway ? withGateway : others).Add(unicast.Address.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        withGateway.AddRange(others);
        return withGateway;
    }

    /// <summary>
    /// The text of the pairing QR code (docs/remote-v1.md section 2). <paramref name="relayUrl"/> is the relay base
    /// address, or null when the relay is off (no <c>r</c> field: LAN only).
    /// </summary>
    public static string QrText(string address, int port, string fingerprint, string code, string hostName,
        string? relayUrl = null) =>
        $"{RemoteApi.PairingScheme}://pair?h={Uri.EscapeDataString(address)}&p={port}&f={fingerprint}&c={code}" +
        $"&n={Uri.EscapeDataString(hostName)}" +
        (string.IsNullOrEmpty(relayUrl) ? "" : $"&r={EscapeRelay(relayUrl)}");

    // ':' and '/' are legal in a query value; keeping them readable saves 4 characters, which the QR code's
    // 213-byte limit (version 10) can use for a long custom relay address.
    private static string EscapeRelay(string url) =>
        Uri.EscapeDataString(url).Replace("%3A", ":", StringComparison.Ordinal).Replace("%2F", "/", StringComparison.Ordinal);
}

/// <summary>Result of <see cref="RemoteAccessGate.Check"/>.</summary>
public sealed record RemoteGateResult(bool Allowed, string? Reason = null)
{
    public static RemoteGateResult Open { get; } = new(true);
}

/// <summary>
/// The one place that decides whether remote access may be used at all. Open for everyone today; a paid tier
/// would answer here, without touching the listener or the pairing code.
/// </summary>
public static class RemoteAccessGate
{
    public static RemoteGateResult Check(RemoteSettings settings) => RemoteGateResult.Open;
}
