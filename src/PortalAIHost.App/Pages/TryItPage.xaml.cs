using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Pages;

public sealed partial class TryItPage : Page
{
    public TryItPage()
    {
        ViewModel = new TryItViewModel(AppServices.Shell, AppServices.Ui, AppServices.Strings);
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TryItViewModel.Reply)) ShowReply();
        };
        ShowReply();
    }

    public TryItViewModel ViewModel { get; }

    /// <summary>Shows the reply with bold, italic, code and lists styled instead of raw markers.</summary>
    private void ShowReply()
    {
        ReplyBlock.Blocks.Clear();
        foreach (var line in MiniMarkdown.Parse(ViewModel.Reply))
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
            ReplyBlock.Blocks.Add(p);
        }
    }
}
