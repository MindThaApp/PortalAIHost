namespace Portal.AiHost.VoiceServer;

/// <summary>Accepts only plain absolute local drive paths (C:\folder\file.wav). Everything else is refused.</summary>
public static class LocalPath
{
    private static readonly string[] Devices =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static readonly char[] BadChars = ['<', '>', '"', '|', '?', '*'];

    /// <summary>Returns null when the path is acceptable, else a short reason.</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260)
        {
            return "The path is empty or too long.";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return "Network and device paths are not allowed.";
        }

        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return "The path must be absolute and start with a drive letter.";
        }

        if (path.IndexOf(':', 2) >= 0)
        {
            return "Stream names are not allowed in a path.";
        }

        if (path.IndexOfAny(BadChars) >= 0 || path.Any(char.IsControl) || path.Contains('/'))
        {
            return "The path has characters that are not allowed.";
        }

        foreach (var segment in path[3..].Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return "The path has an empty, relative or odd segment.";
            }

            var stem = segment;
            var dot = stem.IndexOf('.', StringComparison.Ordinal);
            if (dot >= 0)
            {
                stem = stem[..dot];
            }

            if (Devices.Contains(stem.TrimEnd(), StringComparer.OrdinalIgnoreCase))
            {
                return "Device names are not allowed in a path.";
            }
        }

        return null;
    }

    /// <summary>Checks the output target: acceptable path, existing folder, no file or folder of that name yet.</summary>
    public static string? CheckNewFile(string path)
    {
        if (Check(path) is { } reason)
        {
            return reason;
        }

        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            return "The output folder does not exist.";
        }

        return File.Exists(path) || Directory.Exists(path) ? "The output file already exists." : null;
    }
}
