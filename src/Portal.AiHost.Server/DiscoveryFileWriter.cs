using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Server;

/// <summary>
/// The discovery file <c>LocalState\discovery\host.json</c>. Written atomically; removed only when it still
/// names this host's own <c>instance_id</c>, so a newer host's file is never deleted.
/// </summary>
public static class DiscoveryFileWriter
{
    public const string FolderName = "discovery";
    public const string FileName = "host.json";

    public static string PathFor(string localStateDirectory) =>
        Path.Combine(localStateDirectory, FolderName, FileName);

    public static void Write(string path, DiscoveryFile file)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(file);
        RequireDiscoveryName(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(file, AiHostJsonContext.Default.DiscoveryFile));
    }

    public static DiscoveryFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            if (info.Length > 64 * 1024)
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllBytes(path), AiHostJsonContext.Default.DiscoveryFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the file only if it is named host.json, parses as a discovery file for this service and carries
    /// <paramref name="instanceId"/>. Returns true when it was deleted.
    /// </summary>
    public static bool DeleteIfOwn(string path, string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        RequireDiscoveryName(path);
        var current = TryRead(path);
        if (current is null || current.Service != ApiInfo.ServiceName ||
            !string.Equals(current.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RequireDiscoveryName(string path)
    {
        if (!string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The discovery file must be named host.json.", nameof(path));
        }
    }
}
