namespace PortalAIHost.App.Services;

public enum MdKind { Text, Bold, Italic, Code }

public readonly record struct MdRun(string Text, MdKind Kind);

/// <summary>
/// Just enough markdown for a chat reply: **bold**, *italic*, `code`, "- " and "1. " lists, and "#" headings (shown bold).
/// Anything unmatched stays as typed, so a reply that is still arriving never loses text.
/// </summary>
public static class MiniMarkdown
{
    /// <summary>One entry per line of the reply; each line is a list of styled runs.</summary>
    public static List<List<MdRun>> Parse(string? text)
    {
        var lines = new List<List<MdRun>>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            var runs = new List<MdRun>();
            var heading = 0;
            while (heading < trimmed.Length && trimmed[heading] == '#') heading++;
            if (heading is > 0 and <= 6 && heading < trimmed.Length && trimmed[heading] == ' ')
            {
                runs.Add(new MdRun(StripMarkers(trimmed[(heading + 1)..]), MdKind.Bold));
                lines.Add(runs);
                continue;
            }
            if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                runs.Add(new MdRun(new string(' ', Math.Min(indent, 8)) + "• ", MdKind.Text));
                trimmed = trimmed[2..];
            }
            else if (indent > 0)
            {
                runs.Add(new MdRun(new string(' ', Math.Min(indent, 8)), MdKind.Text));
            }
            runs.AddRange(Inline(trimmed));
            lines.Add(runs);
        }
        return lines;
    }

    /// <summary>The reply with the markers taken out (used where styled text cannot be shown).</summary>
    public static string StripMarkers(string? text) =>
        string.Concat(Inline(text ?? "").Select(r => r.Text));

    private static List<MdRun> Inline(string s)
    {
        var runs = new List<MdRun>();
        var plain = new System.Text.StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            runs.Add(new MdRun(plain.ToString(), MdKind.Text));
            plain.Clear();
        }
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '`' && s.IndexOf('`', i + 1) is var c and > 0)
            {
                Flush();
                runs.Add(new MdRun(s[(i + 1)..c], MdKind.Code));
                i = c + 1;
            }
            else if (s.AsSpan(i).StartsWith("**") && s.IndexOf("**", i + 2, StringComparison.Ordinal) is var b and > 0 && b > i + 2)
            {
                Flush();
                runs.Add(new MdRun(s[(i + 2)..b], MdKind.Bold));
                i = b + 2;
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] != ' ' && s[i + 1] != '*'
                     && s.IndexOf('*', i + 1) is var e and > 0 && s[e - 1] != ' ')
            {
                Flush();
                runs.Add(new MdRun(s[(i + 1)..e], MdKind.Italic));
                i = e + 1;
            }
            else
            {
                plain.Append(s[i]);
                i++;
            }
        }
        Flush();
        return runs;
    }
}
