using Portal.AiHost.Platform.Windows.Identity;

namespace Portal.AiHost.Platform.Windows.Tests.Identity;

public sealed class DpapiSecretProtectorTests
{
    [Fact]
    public void Round_trips_and_does_not_store_plaintext()
    {
        var protector = new DpapiSecretProtector();
        var secret = "device-secret-0123456789"u8.ToArray();

        var blob = protector.Protect(secret);

        Assert.False(blob.AsSpan().IndexOf(secret) >= 0);
        Assert.Equal(secret, protector.Unprotect(blob));
    }

    [Fact]
    public void Blob_of_another_purpose_is_refused()
    {
        var other = new DpapiHostKeyProtector().Protect([1, 2, 3, 4]);
        Assert.ThrowsAny<Exception>(() => new DpapiSecretProtector().Unprotect(other));
    }
}
