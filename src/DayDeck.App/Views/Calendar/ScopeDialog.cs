using System.Windows;
using System.Windows.Controls;

namespace DayDeck.App.Views.Calendar;

public enum EditScope
{
    Cancel,
    ThisOnly,
    Following,
    All,
}

/// <summary>반복 일정을 바꾸거나 지울 때 범위를 묻는다.</summary>
internal static class ScopeDialog
{
    public static EditScope Ask(Window? owner, string title, string message, bool allowFollowing = true)
    {
        var result = EditScope.Cancel;
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 12) });

        void AddButton(string text, string hint, EditScope scope)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = CalendarStyle.SubText, TextWrapping = TextWrapping.Wrap });
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            button.Click += (_, _) =>
            {
                result = scope;
                window.DialogResult = true;
            };
            stack.Children.Add(button);
        }

        AddButton("이 일정만", "선택한 회차만 바꿉니다", EditScope.ThisOnly);
        if (allowFollowing)
        {
            AddButton("이 일정 및 이후 일정", "이 회차부터 새 반복으로 나눕니다", EditScope.Following);
        }
        AddButton("모든 일정", "반복 전체를 바꿉니다", EditScope.All);

        var cancel = new Button { Content = "취소", IsCancel = true, Width = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(cancel);
        window.Content = stack;
        DayDeck.App.Services.FontService.ScaleWindow(window);
        window.ShowDialog();
        return result;
    }
}
