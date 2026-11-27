using System.Runtime.CompilerServices;
using Portal.AiHost.Core.Store;

namespace Portal.AiHost.Server.Tests;

/// <summary>Keeps test files out of the real Recycle Bin: records each path, then removes the test's own temporary file.</summary>
internal sealed class TestRecycler : IFileRecycler
{
    public static TestRecycler Instance { get; } = new();

    public System.Collections.Concurrent.ConcurrentQueue<string> Recycled { get; } = new();

    public void AssertCanRecycle(string fullPath, long sizeBytes)
    {
    }

    public void Recycle(string fullPath, long sizeBytes)
    {
        Recycled.Enqueue(fullPath);
        File.Delete(fullPath); // a file inside this test's own temporary store
    }

    [ModuleInitializer]
    internal static void Use() => ModelStore.DefaultRecycler = Instance;
}
