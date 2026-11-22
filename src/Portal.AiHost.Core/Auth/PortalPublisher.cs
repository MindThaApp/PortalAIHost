using System.Security.Cryptography;
using System.Text;

namespace Portal.AiHost.Core.Auth;

/// <summary>
/// Decides whether a caller is a Portal app: a package whose family name carries the Portal publisher's id and
/// that Windows installed from the Microsoft Store. The publisher id in a family name is a hash of the signing
/// publisher, but Developer Mode can register an unsigned package under any publisher, so the Store origin is
/// what ties the package to the Portal publisher's Store account. Anything else is not a Portal app.
/// </summary>
public static class PortalPublisher
{
    /// <summary>Publisher (certificate subject) of every Portal app in the Microsoft Store.</summary>
    public const string Subject = "CN=FE63C3BB-418B-484C-852F-E7985F260BC3";

    /// <summary>Publisher id of <see cref="Subject"/>, as it appears after the underscore in a family name.</summary>
    public static string Id { get; } = PublisherId(Subject);

    /// <summary>
    /// True only for a Store-installed package of the Portal publisher. The display name an app sends in its
    /// request plays no part.
    /// </summary>
    public static bool IsPortalPackage(string? packageFamilyName, bool installedFromStore)
    {
        if (!installedFromStore || string.IsNullOrEmpty(packageFamilyName))
        {
            return false;
        }

        var underscore = packageFamilyName.IndexOf('_', StringComparison.Ordinal);
        if (underscore <= 0 || underscore != packageFamilyName.LastIndexOf('_'))
        {
            return false;
        }

        return string.Equals(packageFamilyName[(underscore + 1)..], Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows package publisher id: the first 8 bytes of SHA-256 over the UTF-16LE publisher string, written as
    /// 13 base32 characters (alphabet without i, l, o, u).
    /// </summary>
    public static string PublisherId(string publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (var i = 0; i < 8; i++)
        {
            bits = (bits << 8) | hash[i];
        }

        // 64 bits padded with one zero bit make 13 groups of 5.
        var sb = new StringBuilder(13);
        for (var group = 0; group < 13; group++)
        {
            var shift = 59 - (group * 5);
            var value = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << 1) & 0x1F);
            sb.Append(alphabet[value]);
        }

        return sb.ToString();
    }
}
