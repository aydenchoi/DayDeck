using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views.Calendar;

/// <summary>목록 보기: 기간 안의 일정과 할 일을 날짜별로 나열한다. 빈 날은 건너뛴다.</summary>
internal sealed class AgendaView : ScrollViewer
{
    public AgendaView(ICalendarHost host, CalendarData data)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
        Content = stack;

        var any = false;
        for (var day = data.From; day <= data.To; day = day.AddDays(1))
        {
            var events = data.EventsOn(day).ToList();
            var todos = data.TodosOn(day).ToList();
            var holiday = data.HolidayName(day);
            if (events.Count == 0 && todos.Count == 0 && holiday is null)
            {
                continue;
            }
            any = true;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
            var title = TaskFormatting.FormatDate(day, data.Today);
            var dateText = CalendarStyle.Label(
                title is "오늘" or "내일" or "어제" or "모레" ? $"{title} · {day:M월 d일} ({CalendarStyle.DayName(day.DayOfWeek)})" : $"{day:M월 d일} ({CalendarStyle.DayName(day.DayOfWeek)})",
                13, CalendarStyle.DayBrush(data, day), FontWeights.SemiBold);
            header.Children.Add(dateText);
            if (holiday is not null)
            {
                var holidayText = CalendarStyle.Label(holiday, 11.5, CalendarStyle.Holiday);
                holidayText.Margin = new Thickness(8, 1, 0, 0);
                header.Children.Add(holidayText);
            }
            stack.Children.Add(header);

            foreach (var occurrence in events)
            {
                var color = CalendarStyle.EventColor(occurrence);
                var when = occurrence.IsAllDay ? "종일" : $"{CalendarStyle.Time(occurrence.Start)} – {CalendarStyle.Time(occurrence.End)}";
                var row = Row(when, occurrence.Title, occurrence.Event.Location, CalendarStyle.Solid(color));
                if (occurrence.IsCompleted)
                {
                    Strike(row, "✓ ");
                }
                else if (occurrence.IsPastAndOpen(data.Now))
                {
                    row.Background = CalendarStyle.Hatch;
                }
                row.MouseLeftButtonUp += (_, _) => host.OpenOccurrence(occurrence);
                row.MouseRightButtonUp += (_, e) =>
                {
                    host.ShowOccurrenceMenu(occurrence, row);
                    e.Handled = true;
                };
                stack.Children.Add(row);
            }
            foreach (var todo in todos)
            {
                var when = todo.DueTime is { } t ? $"{TaskFormatting.FormatTime(t)} 마감" : "마감";
                var row = Row(when, "☐ " + todo.Title, todo.ListName, CalendarStyle.Muted);
                if (todo.Status != ItemStatus.Open)
                {
                    Strike(row, "");
                }
                row.MouseLeftButtonUp += (_, _) => host.OpenTodo(todo);
                stack.Children.Add(row);
            }
        }

        if (!any)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "이 기간에 일정이 없습니다.\n상단의 ＋ 새 일정으로 추가해 보세요.",
                Foreground = CalendarStyle.Muted,
                FontSize = 13,
                Margin = new Thickness(0, 40, 0, 0),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private static Border Row(string when, string title, string? detail, Brush bar)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(CalendarStyle.Label(when, 12.5, CalendarStyle.SubText));
        var text = new StackPanel();
        text.Children.Add(CalendarStyle.Label(title, 13.5));
        if (!string.IsNullOrEmpty(detail))
        {
            text.Children.Add(CalendarStyle.Label(detail, 11.5, CalendarStyle.SubText));
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Child = grid,
            Background = CalendarStyle.Card,
            BorderBrush = bar,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
        };
    }

    private static void Strike(Border row, string prefix)
    {
        foreach (var text in ((Grid)row.Child).Children.OfType<FrameworkElement>()
                     .SelectMany(e => e is Panel p ? p.Children.OfType<TextBlock>() : e is TextBlock t ? [t] : []))
        {
            text.Foreground = CalendarStyle.Muted;
            text.TextDecorations = TextDecorations.Strikethrough;
        }
        if (prefix.Length > 0 && ((Grid)row.Child).Children[1] is StackPanel { Children: [TextBlock first, ..] })
        {
            first.Text = prefix + first.Text;
        }
        row.BorderBrush = CalendarStyle.DoneBorder;
    }
}
