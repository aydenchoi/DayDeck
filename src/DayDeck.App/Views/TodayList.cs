using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.App.Services;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

/// <summary>오늘 할 일(체크 가능)과 오늘 일정 목록. 바탕화면 위젯과 트레이 "오늘" 팝업이 함께 쓴다.</summary>
internal static class TodayList
{
    /// <summary><paramref name="body"/>를 채우고 요약 한 줄("할 일 3개 남음 · 완료 2")을 돌려준다.</summary>
    public static string Fill(Panel body, App app, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var tasks = app.Tasks.GetToday();
        var open = tasks.Where(t => t.Status == ItemStatus.Open).ToList();
        var done = tasks.Count - open.Count;
        var events = app.Events.GetOccurrences(today, today).Where(o => o.OccursOn(today)).OrderBy(o => !o.IsAllDay).ThenBy(o => o.Start).ToList();
        var overdue = open.Count(t => TaskRules.IsOverdue(t, now));
        var summary = $"할 일 {open.Count}개 남음 · 완료 {done}" + (overdue > 0 ? $" · ⚠ 기한 지남 {overdue}" : "");

        body.Children.Clear();
        body.Children.Add(Header("할 일"));
        if (tasks.Count == 0)
        {
            body.Children.Add(Hint("오늘 할 일이 없습니다."));
        }
        foreach (var task in TaskRules.OrderOpen(open, now).Concat(tasks.Where(t => t.Status != ItemStatus.Open)))
        {
            body.Children.Add(TaskRow(app, task, now));
        }

        body.Children.Add(Header("일정"));
        if (events.Count == 0)
        {
            body.Children.Add(Hint("오늘 일정이 없습니다."));
        }
        foreach (var occurrence in events)
        {
            var past = occurrence.IsPastAndOpen(now);
            var text = new TextBlock
            {
                Text = (occurrence.IsCompleted ? "✓ " : "") + (occurrence.IsAllDay ? "종일" : occurrence.Start.ToString("HH:mm", CultureInfo.InvariantCulture)) + "  " + occurrence.Title,
                FontSize = 12.5,
                Margin = new Thickness(0, 2, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeService.Brush(occurrence.IsCompleted || past ? "MutedBrush" : "TextBrush"),
                TextDecorations = occurrence.IsCompleted ? TextDecorations.Strikethrough : null,
                Cursor = Cursors.Hand,
                ToolTip = occurrence.Event.Location,
            };
            text.MouseLeftButtonUp += (_, _) => app.ShowTask(occurrence.Event.Id, occurrence.Start);
            body.Children.Add(text);
        }
        return summary;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeService.Brush("SubTextBrush"),
        Margin = new Thickness(0, 8, 0, 4),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = ThemeService.Brush("MutedBrush"),
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static CheckBox TaskRow(App app, TaskItem task, DateTime now)
    {
        var overdue = TaskRules.IsOverdue(task, now);
        var label = new TextBlock
        {
            Text = (overdue ? "⚠ " : "") + task.Title + (task.DueTime is { } time ? "  " + TaskFormatting.FormatTime(time) : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeService.Brush(task.Status != ItemStatus.Open ? "MutedBrush" : overdue ? "DangerBrush" : "TextBrush"),
            TextDecorations = task.Status != ItemStatus.Open ? TextDecorations.Strikethrough : null,
            FontWeight = overdue ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var box = new CheckBox
        {
            Content = label,
            IsChecked = task.Status != ItemStatus.Open,
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, task.Title);
        box.Click += (_, _) => app.Tasks.SetCompleted(task.Id, box.IsChecked == true);
        box.MouseDoubleClick += (_, _) => app.ShowTask(task.Id);
        return box;
    }
}
