using System.Windows;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>메모에 링크 넣기: 주소와 표시할 글자. 웹·메일 주소만 받는다.</summary>
public partial class LinkDialog : Window
{
    private LinkDialog(string label)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        LabelBox.Text = label;
        Loaded += (_, _) =>
        {
            // 클립보드에 주소가 있으면 미리 채운다.
            if (Clipboard.ContainsText() && MarkdownEdit.NormalizeUrl(Clipboard.GetText()) is { } copied && !copied.Contains('\n'))
            {
                UrlBox.Text = copied;
                UrlBox.SelectAll();
            }
            UrlBox.Focus();
        };
    }

    public string Url { get; private set; } = "";

    public string Label => LabelBox.Text.Trim();

    /// <summary>(주소, 표시할 글자). 취소하면 null.</summary>
    public static (string Url, string Label)? Ask(Window? owner, string selectedText)
    {
        var dialog = new LinkDialog(selectedText.Contains('\n') ? "" : selectedText) { Owner = owner };
        return dialog.ShowDialog() == true ? (dialog.Url, dialog.Label) : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (MarkdownEdit.NormalizeUrl(UrlBox.Text) is not { } url)
        {
            ErrorText.Visibility = Visibility.Visible;
            UrlBox.Focus();
            return;
        }
        Url = url;
        DialogResult = true;
    }
}
