using System.Text;

namespace Portal.AiHost.VoiceServer;

/// <summary>Splits text into pieces the speech models handle well: at sentence ends first, then at clause marks, then at spaces.</summary>
public static class TextChunker
{
    public const int DefaultMaxChars = 280;

    public static IReadOnlyList<string> Split(string text, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 20);
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in Sentences(text))
        {
            if (sentence.Length > maxChars)
            {
                Flush(current, result);
                result.AddRange(SplitLong(sentence, maxChars));
                continue;
            }

            if (current.Length > 0 && current.Length + 1 + sentence.Length > maxChars)
            {
                Flush(current, result);
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        Flush(current, result);
        return result;
    }

    private static void Flush(StringBuilder sb, List<string> result)
    {
        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
            sb.Clear();
        }
    }

    private static IEnumerable<string> Sentences(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or '\t')
            {
                c = ' ';
            }

            sb.Append(c);
            var wide = c is '。' or '！' or '？';
            var end = wide || c is '.' or '!' or '?' or '…';
            if (end && (wide || i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                var s = sb.ToString().Trim();
                sb.Clear();
                if (s.Length > 0)
                {
                    yield return s;
                }
            }
        }

        var rest = sb.ToString().Trim();
        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    private static IEnumerable<string> SplitLong(string s, int max)
    {
        while (s.Length > max)
        {
            var cut = s.LastIndexOfAny([',', ';', ':', '，', '、'], max - 1, max);
            if (cut < max / 3)
            {
                cut = s.LastIndexOf(' ', max - 1, max);
            }

            cut = cut < 1 ? max : cut + 1;
            var part = s[..cut].Trim();
            if (part.Length > 0)
            {
                yield return part;
            }

            s = s[cut..].TrimStart();
        }

        if (s.Length > 0)
        {
            yield return s;
        }
    }
}
