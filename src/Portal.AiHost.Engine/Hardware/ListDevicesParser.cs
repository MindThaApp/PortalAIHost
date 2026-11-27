using System.Globalization;
using System.Text.RegularExpressions;

namespace Portal.AiHost.Engine.Hardware;

/// <summary>One line of <c>llama-server --list-devices</c>, for example <c>CUDA0: NVIDIA GeForce RTX 4070 (12281 MiB, 11000 MiB free)</c>.</summary>
public sealed record ListedDevice(string Backend, int Index, string Name, long TotalMiB, long FreeMiB)
{
    /// <summary>The id to pass to <c>--device</c>, such as "CUDA0".</summary>
    public string DeviceId => $"{Backend}{Index}";
}

/// <summary>Parses <c>--list-devices</c> output for any backend prefix (CUDA, Vulkan, and so on).</summary>
public static partial class ListDevicesParser
{
    [GeneratedRegex(@"^\s*(?<backend>[A-Za-z]+)(?<index>\d+):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    /// <summary>All devices of <paramref name="backend"/>, in the order listed. Empty when the process failed.</summary>
    public static IReadOnlyList<ListedDevice> Parse(string output, int exitCode, string backend)
    {
        if (exitCode != 0 || string.IsNullOrEmpty(output))
        {
            return [];
        }

        var list = new List<ListedDevice>();
        foreach (Match m in Line().Matches(output))
        {
            if (!m.Groups["backend"].Value.Equals(backend, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var total = long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture);
            var free = m.Groups["free"].Success ? long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture) : total;
            list.Add(new ListedDevice(m.Groups["backend"].Value, int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture),
                m.Groups["name"].Value, total, free));
        }

        return list;
    }

    /// <summary>
    /// Finds the listed device that is the given NVIDIA card: same name (ignoring case and a leading "NVIDIA ") and the
    /// closest memory. CUDA order can differ from NVML order, so the Host matches instead of assuming an index.
    /// </summary>
    public static ListedDevice? Match(IReadOnlyList<ListedDevice> devices, NvmlDevice card)
    {
        ListedDevice? best = null;
        var bestGap = long.MaxValue;
        var want = Normalize(card.Name);
        foreach (var d in devices)
        {
            if (Normalize(d.Name) != want)
            {
                continue;
            }

            var gap = Math.Abs(d.TotalMiB - (card.TotalBytes / (1024 * 1024)));
            if (gap < bestGap)
            {
                best = d;
                bestGap = gap;
            }
        }

        return best;
    }

    private static string Normalize(string name)
    {
        var n = name.Trim();
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            n = n[7..];
        }

        return n.ToLowerInvariant();
    }
}
