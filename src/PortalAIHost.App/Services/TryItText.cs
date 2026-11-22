namespace PortalAIHost.App.Services;

/// <summary>Separates a model's thinking text from its answer.</summary>
public static class TryItText
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    /// <summary>
    /// Thinking comes from the engine's reasoning field and from any think tags left in the answer.
    /// An unclosed tag (the reply was cut short while thinking) counts as thinking only.
    /// </summary>
    public static (string Thinking, string Answer) Split(string? reasoning, string? content)
    {
        var thinking = reasoning?.Trim() ?? "";
        var answer = content ?? "";
        while (true)
        {
            var start = answer.IndexOf(Open, StringComparison.Ordinal);
            if (start < 0)
                break;
            var end = answer.IndexOf(Close, start, StringComparison.Ordinal);
            var inner = end < 0 ? answer[(start + Open.Length)..] : answer[(start + Open.Length)..end];
            thinking = (thinking.Length == 0 ? "" : thinking + "\n") + inner.Trim();
            answer = end < 0 ? answer[..start] : answer[..start] + answer[(end + Close.Length)..];
        }

        // Some templates open the thinking in the prompt, so only a closing tag shows up in the reply.
        var lone = answer.IndexOf(Close, StringComparison.Ordinal);
        if (lone >= 0)
        {
            thinking = (thinking.Length == 0 ? "" : thinking + "\n") + answer[..lone].Trim();
            answer = answer[(lone + Close.Length)..];
        }

        return (thinking.Trim(), answer.Trim());
    }
}
