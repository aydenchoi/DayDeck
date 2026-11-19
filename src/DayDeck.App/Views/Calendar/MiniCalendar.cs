using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DayDeck.App.Views.Calendar;

/// <summary>
/// 주/일/목록 보기 옆의 작은 달력 (설계 §5.3 "미니 달력"). 날짜를 누르면 그 날로 간다.
/// 일정이 있는 날은 점, 지금 보고 있는 기간은 옅게 칠한다. ‹ ›로 큰 보기와 따로 달을 넘길 수 있다.
/// </summary>
internal sealed class MiniCalendar : StackPanel
{
    private readonly Func<DateOnly, IReadOnlySet<DateOnly>> _eventDays;
    private readonly Action<DateOnly> _pick;
    private readonly DayOfWeek _weekStart;
    private readonly DateOnly _from;
    private readonly DateOnly _to;
    private DateOnly _month;

    public MiniCalendar(DateOnly anchor, DateOnly from, DateOnly to, DayOfWeek weekStart,
        Func<DateOnly, IReadOnlySet<DateOnly>> eventDays, Action<DateOnly> pick)
    {
        _month = new DateOnly(anchor.Year, anchor.Month, 1);
        _from = from;
        _to = to;
        _weekStart = weekStart;
        _eventDays = eventDays;
        _pick = pick;
        System.Windows.Automation.AutomationProperties.SetName(this, "미니 달력");
        Build();
    }

    private void Build()
    {
        Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var next = NavButton("›", 1);
        DockPanel.SetDock(next, Dock.Right);
        header.Children.Add(next);
        var previous = NavButton("‹", -1);
        DockPanel.SetDock(previous, Dock.Right);
        header.Children.Add(previous);
        header.Children.Add(CalendarStyle.Label($"{_month.Year}년 {_month.Month}월", 13, CalendarStyle.Text, FontWeights.SemiBold));
        Children.Add(header);

        var grid = new UniformGrid { Columns = 7 };
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)_weekStart + i) % 7);
            var label = CalendarStyle.Label(CalendarStyle.DayName(day), 10.5,
                day == DayOfWeek.Sunday ? CalendarStyle.Holiday : day == DayOfWeek.Saturday ? CalendarStyle.Saturday : CalendarStyle.SubText);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(label);
        }

        var events = _eventDays(_month);
        var start = _month.AddDays(-(((int)_month.DayOfWeek - (int)_weekStart + 7) % 7));
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            grid.Children.Add(DayCell(date, today, events));
        }
        Children.Add(grid);
    }

    private Button NavButton(string text, int months)
    {
        var button = new Button
        {
            Content = text,
            Width = 24,
            Height = 22,
            Padding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, months < 0 ? "미니 달력 이전 달" : "미니 달력 다음 달");
        button.Click += (_, _) =>
        {
            _month = _month.AddMonths(months);
            Build();
        };
        return button;
    }

    private FrameworkElement DayCell(DateOnly date, DateOnly today, IReadOnlySet<DateOnly> events)
    {
        var inMonth = date.Month == _month.Month;
        var number = CalendarStyle.Label(date.Day.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5,
            !inMonth ? CalendarStyle.Muted : date.DayOfWeek == DayOfWeek.Sunday ? CalendarStyle.Holiday
            : date.DayOfWeek == DayOfWeek.Saturday ? CalendarStyle.Saturday : CalendarStyle.Text,
            date == today ? FontWeights.Bold : FontWeights.Normal);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel();
        stack.Children.Add(number);
        stack.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = CalendarStyle.Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = inMonth && events.Contains(date) ? Visibility.Visible : Visibility.Hidden,
        });
        var cell = new Border
        {
            Child = stack,
            Padding = new Thickness(0, 2, 0, 1),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Background = date == today ? CalendarStyle.TodayBackground
                : date >= _from && date <= _to ? Services.ThemeService.Brush("NavSelectedBrush")
                : System.Windows.Media.Brushes.Transparent,
            ToolTip = date.ToString("M월 d일 (ddd)", CalendarStyle.Korean),
        };
        if (date == today)
        {
            cell.BorderBrush = CalendarStyle.Accent;
            cell.BorderThickness = new Thickness(1);
        }
        System.Windows.Automation.AutomationProperties.SetName(cell, "미니 " + date.ToString("yyyy-MM-dd"));
        cell.MouseLeftButtonUp += (_, e) =>
        {
            _pick(date);
            e.Handled = true;
        };
        return cell;
    }
}
