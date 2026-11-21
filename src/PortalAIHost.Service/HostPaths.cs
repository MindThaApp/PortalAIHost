using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Portal.AiHost.Platform.Windows.Pipes;
using Portal.AiHost.Server;

namespace PortalAIHost.Service;

/// <summary>Where the host keeps its data, and whether it runs in test mode (architecture section 7).</summary>
public sealed partial record HostPaths(string DataRoot, bool TestMode, string DiscoveryPath, string PipeName)
{
    public const string TestRootVariable = "PORTALAIHOST_TEST_DATA_ROOT";
    public const string TestRootMarker = ".portal-ai-host-test-root";

    /// <summary>Exit code when the test data root is missing its marker (fail closed).</summary>
    public const int BadTestRootExitCode = 3;

    /// <summary>
    /// Test mode when <see cref="TestRootVariable"/> is set: the folder must exist and hold the marker file,
    /// otherwise null (the caller exits with code 3). Test mode never touches the real LocalState.
    /// </summary>
    public static HostPaths? Resolve(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var testRoot = env(TestRootVariable);
        if (testRoot is not null)
        {
            if (testRoot.Length == 0 || !Path.IsPathFullyQualified(testRoot))
            {
                return null;
            }

            var full = Path.GetFullPath(testRoot);
            if (!Directory.Exists(full) || !File.Exists(Path.Combine(full, TestRootMarker)))
            {
                return null;
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
            return new HostPaths(full, TestMode: true, DiscoveryFileWriter.PathFor(full),
                ControlPipe.NameForCurrentUser("test-" + hash));
        }

        var localState = RealLocalState();
        return new HostPaths(localState, TestMode: false, DiscoveryFileWriter.PathFor(localState),
            ControlPipe.NameForCurrentUser());
    }

    /// <summary>The packaged host's LocalState, or a per-user folder for unpackaged developer runs.</summary>
    private static string RealLocalState()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (PackageFamilyName() is { } pfn)
        {
            return Path.Combine(localAppData, "Packages", pfn, "LocalState");
        }

        return Path.Combine(localAppData, "PortalAIHost", "LocalState");
    }

    private static unsafe string? PackageFamilyName()
    {
        uint length = 0;
        var rc = GetCurrentPackageFamilyName(ref length, null);
        if (rc != 122 || length == 0) // ERROR_INSUFFICIENT_BUFFER; APPMODEL_ERROR_NO_PACKAGE otherwise
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            return GetCurrentPackageFamilyName(ref length, p) == 0 ? new string(p, 0, (int)length - 1) : null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFamilyName(ref uint length, char* name);
}
