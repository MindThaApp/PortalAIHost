using Portal.AiHost.Core.Remote.Client;

namespace Portal.AiHost.Platform.Windows.Identity;

/// <summary>
/// DPAPI (CurrentUser) protection for the client's device key and host token, with its own entropy. Only the same
/// Windows user on this PC can open the blobs.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly DpapiHostKeyProtector _dpapi = new("PortalAIHost.ClientSecret.v1");

    public byte[] Protect(byte[] plaintext) => _dpapi.Protect(plaintext);

    public byte[] Unprotect(byte[] protectedData) => _dpapi.Unprotect(protectedData);
}
