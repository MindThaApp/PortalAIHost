using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PortalAIHost.App.Services;

namespace PortalAIHost.App.Controls;

/// <summary>Shows text with bold, italic, code and lists styled instead of raw markers. Selectable.</summary>
public sealed class MarkdownView : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", (d, _) => ((MarkdownView)d).Render()));

    private readonly RichTextBlock _block = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public MarkdownView()
    {
        Content = _block;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render()
    {
        _block.Blocks.Clear();
        foreach (var line in MiniMarkdown.Parse(Markdown ?? ""))
        {
            var p = new Paragraph();
            foreach (var run in line)
            {
                var r = new Run { Text = run.Text };
                switch (run.Kind)
                {
                    case MdKind.Bold: r.FontWeight = FontWeights.SemiBold; break;
                    case MdKind.Italic: r.FontStyle = Windows.UI.Text.FontStyle.Italic; break;
                    case MdKind.Code: r.FontFamily = new FontFamily("Cascadia Mono, Consolas"); break;
                }

                p.Inlines.Add(r);
            }

            _block.Blocks.Add(p);
        }
    }
}
