using System.Runtime.InteropServices;

namespace Portal.AiHost.Platform.Windows.Identity;

/// <summary>Win32 calls used for caller identity and DPAPI. Read-only queries only; nothing here writes.</summary>
internal static unsafe partial class IdentityNative
{
    public const int AfInet = 2;
    public const int AfInet6 = 23;
    public const int TcpTableOwnerPidConnections = 4;
    public const uint ErrorSuccess = 0;
    public const uint ErrorInsufficientBuffer = 122;
    public const int AppModelErrorNoPackage = 15700;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const int TokenUserClass = 1;
    public const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tcp6RowOwnerPid
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DataBlob
    {
        public int CbData;
        public nint PbData;
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(void* tcpTable, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int af, int tableClass, uint reserved);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* exeName, ref int size);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackageFamilyName(nint process, ref uint length, char* familyName);

    [LibraryImport("kernel32.dll")]
    public static partial int GetPackageFullName(nint process, ref uint length, char* fullName);

    /// <summary>PackageOrigin_Store: the package was installed from the Microsoft Store.</summary>
    public const int PackageOriginStore = 3;

    // Not exported by kernel32.dll; it lives in the app-model API set.
    [LibraryImport("api-ms-win-appmodel-runtime-l1-1-1.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetStagedPackageOrigin(string packageFullName, out int origin);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int infoClass, void* info, int length,
        out int returnLength);

    [LibraryImport("advapi32.dll")]
    public static partial int GetLengthSid(nint sid);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptProtectData(DataBlob* dataIn, char* description, DataBlob* entropy,
        nint reserved, nint prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* entropy,
        nint reserved, nint prompt, uint flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);
}
