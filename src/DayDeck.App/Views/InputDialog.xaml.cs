using System.Windows;

namespace DayDeck.App.Views;

/// <summary>한 줄 입력 대화상자 (새 목록 이름 등).</summary>
public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>입력한 문자열(앞뒤 공백 제거). 취소하거나 비어 있으면 null.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var text = dialog.InputBox.Text.Trim();
        return text.Length > 0 ? text : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
