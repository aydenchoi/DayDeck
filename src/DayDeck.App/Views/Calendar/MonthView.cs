using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DayDeck.App.Views.Calendar;

/// <summary>
/// 월 보기: 7열 × 6주. 칸마다 날짜(공휴일·주말 색), 종일 일정, 시간 일정, 마감 있는 할 일을 넣고
/// 넘치면 "+n개 더"로 줄인다. 날짜 숫자를 누르면 그날의 일 보기로, 빈 곳을 두 번 누르면 새 일정.
/// </summary>
internal sealed class MonthView : Grid
{
    private const int MaxLines = 4;
    private readonly ICalendarHost _host;

    public MonthView(ICalendarHost host, CalendarData data, DayOfWeek weekStart)
    {
        _host = host;
        Background = CalendarStyle.Card;

        for (var c = 0; c < 7; c++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        for (var r = 0; r < 6; r++)
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var c = 0; c < 7; c++)
        {
            var day = (DayOfWeek)(((int)weekStart + c) % 7);
            var header = CalendarStyle.Label(CalendarStyle.DayName(day), 11.5,
                day == DayOfWeek.Sunday ? CalendarStyle.Holiday : day == DayOfWeek.Saturday ? CalendarStyle.Saturday : CalendarStyle.SubText);
            header.HorizontalAlignment = HorizontalAlignment.Center;
            header.VerticalAlignment = VerticalAlignment.Center;
            var headerBorder = new Border { Child = header, BorderBrush = CalendarStyle.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            SetColumn(headerBorder, c);
            Children.Add(headerBorder);
        }

        for (var i = 0; i < 42; i++)
        {
            var date = data.From.AddDays(i);
            var cell = BuildCell(data, date);
            SetRow(cell, 1 + i / 7);
            SetColumn(cell, i % 7);
            Children.Add(cell);
        }
    }

    private Border BuildCell(CalendarData data, DateOnly date)
    {
        var inMonth = date.Month == data.Anchor.Month;
        var isToday = date == data.Today;
        var stack = new StackPanel { Margin = new Thickness(5, 3, 5, 3) };

        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var number = CalendarStyle.Label(date.Day.ToString(), 12.5, inMonth ? CalendarStyle.DayBrush(data, date) : CalendarStyle.Muted, FontWeights.SemiBold);
        var numberHost = new Border
        {
            Child = number,
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(9),
            Cursor = Cursors.Hand,
            ToolTip = inMonth ? "이날 보기" : $"{date.Month}월로 이동",
        };
        if (isToday)
        {
            numberHost.Background = CalendarStyle.Accent;
            number.Foreground = Brushes.White;
        }
        numberHost.MouseLeftButtonUp += (_, e) =>
        {
            // 지난달/다음 달 날짜는 그 달로 넘어간다. 이번 달 날짜는 그날 보기.
            if (inMonth)
            {
                _host.ShowDay(date);
            }
            else
            {
                _host.ShowMonth(date);
            }
            e.Handled = true;
        };
        dateRow.Children.Add(numberHost);
        if (inMonth && data.HolidayName(date) is { } holiday)
        {
            var holidayText = CalendarStyle.Label(holiday, 10.5, CalendarStyle.Holiday);
            holidayText.Margin = new Thickness(4, 1, 0, 0);
            dateRow.Children.Add(holidayText);
        }
        if (App.Current.Settings.Current.Calendar.ShowLunar)
        {
            // 음력: 매달 1일(윤달 포함)은 "윤6.1"처럼 달까지, 나머지는 날짜만.
            var lunar = Core.Calendar.LunarCalendar.FromSolar(date);
            var lunarText = CalendarStyle.Label(lunar.Day == 1 ? "음 " + lunar : "음 " + lunar.Day, 10, CalendarStyle.Muted);
            lunarText.Margin = new Thickness(4, 2, 0, 0);
            dateRow.Children.Add(lunarText);
        }
        stack.Children.Add(dateRow);

        var items = new List<FrameworkElement>();
        foreach (var occurrence in data.EventsOn(date))
        {
            var chip = CalendarStyle.EventChip(occurrence, data.Now, showTime: true);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _host.OpenOccurrence(occurrence);
                e.Handled = true;
            };
            chip.MouseRightButtonUp += (_, e) =>
            {
                _host.ShowOccurrenceMenu(occurrence, chip);
                e.Handled = true;
            };
            items.Add(chip);
        }
        foreach (var todo in data.TodosOn(date))
        {
            var chip = CalendarStyle.TodoChip(todo, data.Now);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _host.OpenTodo(todo);
                e.Handled = true;
            };
            items.Add(chip);
        }

        var visible = items.Count > MaxLines ? MaxLines - 1 : items.Count;
        foreach (var item in items.Take(visible))
        {
            stack.Children.Add(item);
        }
        if (items.Count > visible)
        {
            var more = CalendarStyle.Label($"+{items.Count - visible}개 더", 10.5, CalendarStyle.SubText);
            more.Cursor = Cursors.Hand;
            more.MouseLeftButtonUp += (_, e) =>
            {
                _host.ShowDay(date);
                e.Handled = true;
            };
            stack.Children.Add(more);
        }

        var cell = new Border
        {
            Child = stack,
            BorderBrush = CalendarStyle.Line,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Background = isToday ? CalendarStyle.TodayBackground : CalendarStyle.Card,
            ClipToBounds = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(cell, date.ToString("yyyy-MM-dd"));
        if (!inMonth)
        {
            // 칸의 빈 곳을 눌러도 그 달로 이동 (일정·할 일을 누르면 그 항목이 열린다).
            cell.Cursor = Cursors.Hand;
            cell.ToolTip = $"{date.Year}년 {date.Month}월로 이동";
            cell.MouseLeftButtonUp += (_, e) =>
            {
                if (!e.Handled)
                {
                    _host.ShowMonth(date);
                    e.Handled = true;
                }
            };
        }
        cell.MouseLeftButtonDown += (_, e) =>
        {
            if (inMonth && e.ClickCount == 2)
            {
                // 두 번 누르면 그날 09:00 일정 만들기
                var start = date.ToDateTime(new TimeOnly(9, 0));
                _host.CreateEvent(start, start.AddHours(1), allDay: false);
                e.Handled = true;
            }
        };
        return cell;
    }
}
