using System.Text.RegularExpressions;

namespace Portal.AiHost.Engine;

/// <summary>
/// Capped in-memory buffer of engine output lines. Lines that may carry prompt or reply text are
/// dropped before they are stored, and nothing is ever written to disk.
/// </summary>
public sealed partial class OutputRing
{
    private const int MaxLineLength = 400;
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;
    private readonly object _gate = new();

    public OutputRing(int capacity = 200)
    {
        _capacity = capacity;
    }

    [GeneratedRegex("prompt|\"content\"|\"messages\"|user:|assistant:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sensitive();

    public void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || Sensitive().IsMatch(line))
        {
            return;
        }

        if (line.Length > MaxLineLength)
        {
            line = line[..MaxLineLength];
        }

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }
}
