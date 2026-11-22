using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Runtime.Versioning;
using Portal.AiHost.Core.Abstractions;
using Portal.AiHost.Core.Auth;

namespace Portal.AiHost.Platform.Windows.Identity;

/// <summary>
/// Maps a loopback TCP connection to the calling process: GetExtendedTcpTable gives the owner PID of the
/// client's end; the process is then queried for its package family name (packaged = verified), its exe path,
/// and its user SID, which must equal the host's. A Portal app is a Store-installed package of the Portal
/// publisher; being packaged or signed is not enough. Fails closed: anything unknown is unverified / other user /
/// not a Portal app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCallerIdentityResolver : ICallerIdentityResolver
{
    private readonly SecurityIdentifier? _hostUser = WindowsIdentity.GetCurrent().User;

    public CallerIdentity? Resolve(IPEndPoint remote, IPEndPoint local)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(local);
        if (!IPAddress.IsLoopback(Normalize(remote.Address)) || !IPAddress.IsLoopback(Normalize(local.Address)))
        {
            return null;
        }

        var pid = TcpConnectionTable.FindOwnerPid(remote, local);
        return pid is { } p ? Describe(p) : null;
    }

    /// <summary>Package, exe path and same-user check for one process id.</summary>
    public CallerIdentity Describe(int processId)
    {
        var handle = IdentityNative.OpenProcess(IdentityNative.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0)
        {
            return new CallerIdentity(processId, null, null, Verified: false, SameUser: false);
        }

        try
        {
            var pfn = PackageFamilyName(handle);
            var exe = ImagePath(handle);
            var user = ProcessUser(handle);
            var sameUser = user is not null && _hostUser is not null && user.Equals(_hostUser);
            var fullName = PackageFullName(handle);
            int? origin = fullName is null ? null : OriginOf(fullName);
            var portal = pfn is not null && PortalPublisher.IsPortalPackage(pfn, origin == IdentityNative.PackageOriginStore);
            string? publisher = null;
            if (fullName is not null && origin is { } o && PortalPublisher.IsTrustedOrigin(o))
            {
                publisher = PortalPublisher.DisplayPublisher(o, ManifestPublisher(fullName));
            }

            return new CallerIdentity(processId, pfn, exe, Verified: pfn is not null, SameUser: sameUser,
                PortalApp: portal, PackagePublisher: publisher);
        }
        finally
        {
            IdentityNative.CloseHandle(handle);
        }
    }

    internal static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static unsafe string? PackageFamilyName(nint process)
    {
        uint length = 0;
        var rc = IdentityNative.GetPackageFamilyName(process, ref length, null);
        if (rc != (int)IdentityNative.ErrorInsufficientBuffer || length == 0 || length > 1024)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            rc = IdentityNative.GetPackageFamilyName(process, ref length, p);
        }

        return rc == 0 && length > 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    private static unsafe string? PackageFullName(nint process)
    {
        uint length = 0;
        var rc = IdentityNative.GetPackageFullName(process, ref length, null);
        if (rc != (int)IdentityNative.ErrorInsufficientBuffer || length == 0 || length > 1024)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            rc = IdentityNative.GetPackageFullName(process, ref length, p);
        }

        return rc == 0 && length > 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    /// <summary>The package origin Windows reports, or null when it cannot be determined.</summary>
    internal static int? OriginOf(string packageFullName)
    {
        try
        {
            return IdentityNative.GetStagedPackageOrigin(packageFullName, out var origin) == 0 ? origin : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True only when Windows reports the package as installed from the Microsoft Store.</summary>
    internal static bool IsStoreOrigin(string packageFullName) =>
        OriginOf(packageFullName) == IdentityNative.PackageOriginStore;

    /// <summary>Publisher display name from the installed package's own manifest (read only), or null.</summary>
    private static unsafe string? ManifestPublisher(string packageFullName)
    {
        try
        {
            uint length = 0;
            var rc = IdentityNative.GetPackagePathByFullName(packageFullName, ref length, null);
            if (rc != (int)IdentityNative.ErrorInsufficientBuffer || length == 0 || length > 2048)
            {
                return null;
            }

            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                rc = IdentityNative.GetPackagePathByFullName(packageFullName, ref length, p);
            }

            if (rc != 0 || length == 0)
            {
                return null;
            }

            var manifest = Path.Combine(new string(buffer, 0, (int)length - 1), "AppxManifest.xml");
            return PublisherFromManifest(File.ReadAllText(manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Package/Properties/PublisherDisplayName from a manifest, or null when absent.</summary>
    internal static string? PublisherFromManifest(string manifestXml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(manifestXml);
        var props = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
        return props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
    }

    private static unsafe string? ImagePath(nint process)
    {
        var buffer = new char[32768];
        var size = buffer.Length;
        fixed (char* p = buffer)
        {
            if (!IdentityNative.QueryFullProcessImageNameW(process, 0, p, ref size))
            {
                return null;
            }
        }

        return new string(buffer, 0, size);
    }

    private static unsafe SecurityIdentifier? ProcessUser(nint process)
    {
        if (!IdentityNative.OpenProcessToken(process, IdentityNative.TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            IdentityNative.GetTokenInformation(token, IdentityNative.TokenUserClass, null, 0, out var needed);
            if (needed <= 0 || needed > 4096)
            {
                return null;
            }

            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (!IdentityNative.GetTokenInformation(token, IdentityNative.TokenUserClass, p, needed, out _))
                {
                    return null;
                }

                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the PSID.
                var sid = *(nint*)p;
                return sid == 0 ? null : new SecurityIdentifier(sid);
            }
        }
        finally
        {
            IdentityNative.CloseHandle(token);
        }
    }
}

/// <summary>Reads the TCP connection tables (IPv4 and IPv6) with owner PIDs.</summary>
[SupportedOSPlatform("windows")]
public static class TcpConnectionTable
{
    /// <summary>
    /// PID of the process whose socket has local endpoint <paramref name="clientEnd"/> and remote endpoint
    /// <paramref name="serverEnd"/>, or null.
    /// </summary>
    public static unsafe int? FindOwnerPid(IPEndPoint clientEnd, IPEndPoint serverEnd)
    {
        var client = new IPEndPoint(WindowsCallerIdentityResolver.Normalize(clientEnd.Address), clientEnd.Port);
        var server = new IPEndPoint(WindowsCallerIdentityResolver.Normalize(serverEnd.Address), serverEnd.Port);
        if (client.AddressFamily != server.AddressFamily)
        {
            return null;
        }

        var af = client.AddressFamily == AddressFamily.InterNetwork ? IdentityNative.AfInet : IdentityNative.AfInet6;
        var table = ReadTable(af);
        if (table is null)
        {
            return null;
        }

        fixed (byte* basePtr = table)
        {
            var count = *(uint*)basePtr;
            if (af == IdentityNative.AfInet)
            {
                var rows = (IdentityNative.TcpRowOwnerPid*)(basePtr + 4);
                var max = (table.Length - 4) / sizeof(IdentityNative.TcpRowOwnerPid);
                for (var i = 0; i < count && i < max; i++)
                {
                    var r = rows[i];
                    if (Port(r.LocalPort) == client.Port && Port(r.RemotePort) == server.Port &&
                        new IPAddress(r.LocalAddr).Equals(client.Address) &&
                        new IPAddress(r.RemoteAddr).Equals(server.Address))
                    {
                        return (int)r.OwningPid;
                    }
                }
            }
            else
            {
                // MIB_TCP6TABLE_OWNER_PID: the rows start after dwNumEntries, aligned to 4 bytes.
                var rows = (IdentityNative.Tcp6RowOwnerPid*)(basePtr + 4);
                var max = (table.Length - 4) / sizeof(IdentityNative.Tcp6RowOwnerPid);
                for (var i = 0; i < count && i < max; i++)
                {
                    var r = rows[i];
                    if (Port(r.LocalPort) != client.Port || Port(r.RemotePort) != server.Port)
                    {
                        continue;
                    }

                    var localAddr = new IPAddress(new ReadOnlySpan<byte>(r.LocalAddr, 16));
                    var remoteAddr = new IPAddress(new ReadOnlySpan<byte>(r.RemoteAddr, 16));
                    if (localAddr.Equals(new IPAddress(client.Address.GetAddressBytes())) &&
                        remoteAddr.Equals(new IPAddress(server.Address.GetAddressBytes())))
                    {
                        return (int)r.OwningPid;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static unsafe byte[]? ReadTable(int af)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            IdentityNative.GetExtendedTcpTable(null, ref size, false, af, IdentityNative.TcpTableOwnerPidConnections, 0);
            if (size <= 0)
            {
                return null;
            }

            // Headroom: connections can appear between the two calls.
            var buffer = new byte[size + 4096];
            size = buffer.Length;
            uint rc;
            fixed (byte* p = buffer)
            {
                rc = IdentityNative.GetExtendedTcpTable(p, ref size, false, af,
                    IdentityNative.TcpTableOwnerPidConnections, 0);
            }

            if (rc == IdentityNative.ErrorSuccess)
            {
                return buffer;
            }

            if (rc != IdentityNative.ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }
}
