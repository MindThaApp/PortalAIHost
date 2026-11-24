namespace Portal.AiHost.Contracts;

/// <summary>A parsed pairing link (docs/remote-v1.md section 2).</summary>
public sealed record PairingLink(
    string Address, int Port, string Fingerprint, string Code, string HostName, string? RelayUrl)
{
    /// <summary>
    /// Reads <c>portalai://pair?h=..&amp;p=..&amp;f=..&amp;c=..&amp;n=..[&amp;r=..]</c>. Unknown fields are ignored. A missing,
    /// malformed or unsafe <c>r</c> (not https, or http on a non-loopback host) gives a null relay: LAN only.
    /// </summary>
    public static bool TryParse(string? text, out PairingLink? link)
    {
        link = null;
        var prefix = RemoteApi.PairingScheme + "://pair?";
        if (text is null || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text[prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            try
            {
                fields.TryAdd(part[..eq], Uri.UnescapeDataString(part[(eq + 1)..]));
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        if (!fields.TryGetValue("h", out var host) || host.Length == 0 ||
            !fields.TryGetValue("p", out var portText) || !int.TryParse(portText, out var port) || port is < 1 or > 65535 ||
            !fields.TryGetValue("f", out var fingerprint) || fingerprint.Length == 0 ||
            !fields.TryGetValue("c", out var code) || code.Length == 0)
        {
            return false;
        }

        fields.TryGetValue("n", out var name);
        fields.TryGetValue("r", out var relay);
        link = new PairingLink(host, port, fingerprint, code, name ?? "", SafeRelay(relay));
        return true;
    }

    private static string? SafeRelay(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
        {
            return null;
        }

        var ok = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return ok ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}
