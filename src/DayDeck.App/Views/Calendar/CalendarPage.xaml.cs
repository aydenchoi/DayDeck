using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views.Calendar;

public enum CalendarViewMode
{
    Month,
    Week,
    Day,
    Agenda,
}

/// <summary>달력 화면 (설계 §5.3): 월/주/일/목록 보기, 종일·시간 일정, 반복, 공휴일, 마감 있는 할 일.</summary>
public partial class CalendarPage : UserControl, ICalendarHost
{
    // 화면을 다시 열어도 마지막 보기/날짜를 유지한다 (앱 실행 중에만).
    private static CalendarViewMode s_mode = CalendarViewMode.Month;
    private static DateOnly? s_anchor;
    private static bool s_showTodos = true;

    private readonly App _app = App.Current;
    private CalendarViewMode _mode = s_mode;
    private DateOnly _anchor = s_anchor ?? DateOnly.FromDateTime(DateTime.Now);
    private bool _loading = true;

    public CalendarPage()
    {
        InitializeComponent();
        ShowTodos.IsChecked = s_showTodos;
        CheckModeButton();
        _loading = false;

        Loaded += (_, _) =>
        {
            _app.Events.Changed += OnDataChanged;
            _app.Tasks.Changed += OnDataChanged;
            _app.MinuteTick += OnDataChanged;
            Services.ThemeService.Changed += OnDataChanged;
            Render();
            Focus();
        };
        SizeChanged += (_, e) =>
        {
            // 창 너비가 바뀌어 미니 달력을 보이거나 숨겨야 하면 다시 그린다.
            if (e.WidthChanged && _mode != CalendarViewMode.Month && IsLoaded
                && (ActualWidth >= 900) != (MiniHost.Visibility == Visibility.Visible))
            {
                Render();
            }
        };
        Unloaded += (_, _) =>
        {
            _app.Events.Changed -= OnDataChanged;
            _app.Tasks.Changed -= OnDataChanged;
            _app.MinuteTick -= OnDataChanged;
            Services.ThemeService.Changed -= OnDataChanged;
        };
    }

    /// <summary>특정 날짜를 지정한 보기로 연다 (알림의 "열기" 등).</summary>
    public void Show(DateOnly date, CalendarViewMode mode)
    {
        _anchor = date;
        _mode = mode;
        CheckModeButton();
        Render();
    }

    private void OnDataChanged(object? sender, EventArgs e) => Render();

    private (DateOnly From, DateOnly To) Range()
    {
        var weekStart = _app.Settings.Current.Calendar.WeekStartsOn;
        DateOnly StartOfWeek(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek - (int)weekStart + 7) % 7));
        return _mode switch
        {
            CalendarViewMode.Month => (StartOfWeek(new DateOnly(_anchor.Year, _anchor.Month, 1)), StartOfWeek(new DateOnly(_anchor.Year, _anchor.Month, 1)).AddDays(41)),
            CalendarViewMode.Week => (StartOfWeek(_anchor), StartOfWeek(_anchor).AddDays(6)),
            CalendarViewMode.Day => (_anchor, _anchor),
            _ => (_anchor, _anchor.AddDays(30)),
        };
    }

    private void Render()
    {
        s_mode = _mode;
        s_anchor = _anchor;
        var (from, to) = Range();
        var settings = _app.Settings.Current.Calendar;
        var holidays = settings.ShowKoreanHolidays
            ? KoreanHolidays.Between(from, to).GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.ToList())
            : [];
        var data = new CalendarData(
            from, to, _anchor,
            _app.Events.GetOccurrences(from, to),
            s_showTodos ? _app.Tasks.GetDueBetween(from, to) : [],
            holidays, DateTime.Now, settings.TimeSlotMinutes);

        TitleText.Text = _mode switch
        {
            CalendarViewMode.Month => $"{_anchor.Year}년 {_anchor.Month}월",
            CalendarViewMode.Week => from.Month == to.Month
                ? $"{from.Year}년 {from.Month}월 {from.Day}일 – {to.Day}일"
                : $"{from.Year}년 {from.Month}월 {from.Day}일 – {to.Month}월 {to.Day}일",
            CalendarViewMode.Day => $"{_anchor.Year}년 {_anchor.Month}월 {_anchor.Day}일 ({CalendarStyle.DayName(_anchor.DayOfWeek)})",
            _ => $"{from.Month}월 {from.Day}일부터 30일",
        };

        // 주/일 보기는 다시 그려도 스크롤 위치를 유지한다.
        var offset = (ViewHost.Content as TimeGridView)?.VerticalOffset;
        ViewHost.Content = _mode switch
        {
            CalendarViewMode.Month => new MonthView(this, data, settings.WeekStartsOn),
            CalendarViewMode.Week => new TimeGridView(this, data, 7, offset),
            CalendarViewMode.Day => new TimeGridView(this, data, 1, offset),
            _ => new AgendaView(this, data),
        };
        UpdateMiniCalendar(from, to, settings.WeekStartsOn);
    }

    private void UpdateMiniCalendar(DateOnly from, DateOnly to, DayOfWeek weekStart)
    {
        // 월 보기에는 필요 없고, 좁은 창에서는 큰 보기를 가리지 않게 숨긴다.
        var show = _mode != CalendarViewMode.Month && ActualWidth is 0 or >= 900;
        MiniHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MiniHost.Child = show
            ? new MiniCalendar(_anchor, from, to, weekStart,
                month => _app.Events.GetOccurrences(month, month.AddMonths(1).AddDays(-1))
                    .SelectMany(o => Enumerable.Range(0, o.LastDay.DayNumber - o.FirstDay.DayNumber + 1).Select(i => o.FirstDay.AddDays(i)))
                    .ToHashSet(),
                date => Show(date, _mode))
            : null;
    }

    private void CheckModeButton()
    {
        (_mode switch
        {
            CalendarViewMode.Month => MonthButton,
            CalendarViewMode.Week => WeekButton,
            CalendarViewMode.Day => DayButton,
            _ => AgendaButton,
        }).IsChecked = true;
    }

    // ---------- 머리글 ----------

    private void OnViewChecked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _mode = sender == WeekButton ? CalendarViewMode.Week
            : sender == DayButton ? CalendarViewMode.Day
            : sender == AgendaButton ? CalendarViewMode.Agenda
            : CalendarViewMode.Month;
        Render();
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => Step(-1);

    private void OnNext(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int direction)
    {
        _anchor = _mode switch
        {
            CalendarViewMode.Month => _anchor.AddMonths(direction),
            CalendarViewMode.Week => _anchor.AddDays(7 * direction),
            CalendarViewMode.Day => _anchor.AddDays(direction),
            _ => _anchor.AddDays(30 * direction),
        };
        Render();
    }

    private void OnToday(object sender, RoutedEventArgs e)
    {
        _anchor = DateOnly.FromDateTime(DateTime.Now);
        Render();
    }

    private void OnShowTodosChanged(object sender, RoutedEventArgs e)
    {
        s_showTodos = ShowTodos.IsChecked == true;
        Render();
    }

    private void OnNewEvent(object sender, RoutedEventArgs e)
    {
        var day = _mode == CalendarViewMode.Day || _anchor != DateOnly.FromDateTime(DateTime.Now) ? _anchor : DateOnly.FromDateTime(DateTime.Now);
        var hour = day == DateOnly.FromDateTime(DateTime.Now) ? Math.Min(23, DateTime.Now.Hour + 1) : 9;
        var start = day.ToDateTime(new TimeOnly(hour, 0));
        CreateEvent(start, start.AddHours(1), allDay: false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || e.OriginalSource is TextBox)
        {
            return;
        }

        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnNewEvent(this, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.M: Show(_anchor, CalendarViewMode.Month); break;
            case Key.W: Show(_anchor, CalendarViewMode.Week); break;
            case Key.D: Show(_anchor, CalendarViewMode.Day); break;
            case Key.A: Show(_anchor, CalendarViewMode.Agenda); break;
            case Key.T: OnToday(this, e); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------- ICalendarHost ----------

    public void OpenOccurrence(EventOccurrence occurrence) => EventEditorWindow.Edit(Window.GetWindow(this), occurrence);

    public void OpenTodo(TaskItem item) => (Window.GetWindow(this) as MainWindow)?.OpenTask(item.Id);

    public void CreateEvent(DateTime start, DateTime end, bool allDay) => EventEditorWindow.Create(Window.GetWindow(this), start, end, allDay);

    public void ToggleTodo(TaskItem item) => _app.Tasks.SetCompleted(item.Id, item.Status == ItemStatus.Open);

    public void ShowDay(DateOnly day) => Show(day, CalendarViewMode.Day);

    public void ShowMonth(DateOnly day) => Show(day, CalendarViewMode.Month);

    public void MoveOccurrence(EventOccurrence occurrence, DateTime start, DateTime end)
    {
        var events = _app.Events;
        if (events.Get(occurrence.Event.Id) is not { } calendarEvent)
        {
            return;
        }

        if (!calendarEvent.IsRecurring)
        {
            calendarEvent.Start = start;
            calendarEvent.End = end;
            events.Update(calendarEvent);
            return;
        }

        switch (ScopeDialog.Ask(Window.GetWindow(this), "반복 일정 옮기기", $"'{calendarEvent.Title}'은(는) 반복 일정입니다. 어디까지 옮길까요?"))
        {
            case EditScope.ThisOnly:
                events.UpdateOccurrence(calendarEvent.Id, occurrence.OriginalStart, start, end);
                break;
            case EditScope.Following:
            {
                var following = calendarEvent.Clone();
                following.Start = start;
                following.End = end;
                events.UpdateFollowing(calendarEvent.Id, occurrence.OriginalStart, following);
                break;
            }
            case EditScope.All:
                calendarEvent.Start += start - occurrence.OriginalStart;
                calendarEvent.End = calendarEvent.Start + (end - start);
                events.Update(calendarEvent);
                break;
        }
        Render();
    }

    public void ShowOccurrenceMenu(EventOccurrence occurrence, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var events = _app.Events;
        var id = occurrence.Event.Id;

        var complete = new MenuItem { Header = occurrence.IsCompleted ? "완료 취소" : "✓ 완료" };
        complete.Click += (_, _) =>
        {
            if (occurrence.IsRecurring)
            {
                events.SetOccurrenceCompleted(id, occurrence.OriginalStart, !occurrence.IsCompleted);
            }
            else
            {
                events.SetCompleted(id, !occurrence.IsCompleted);
            }
        };
        var edit = new MenuItem { Header = "편집…" };
        edit.Click += (_, _) => OpenOccurrence(occurrence);
        var delete = new MenuItem { Header = "삭제" };
        delete.Click += (_, _) =>
        {
            if (!occurrence.IsRecurring)
            {
                events.Delete(id);
                (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{occurrence.Title}' 일정 삭제됨", () => events.Restore(id));
                return;
            }
            switch (ScopeDialog.Ask(Window.GetWindow(this), "반복 일정 삭제", $"'{occurrence.Title}'은(는) 반복 일정입니다. 어디까지 지울까요?"))
            {
                case EditScope.ThisOnly:
                    events.DeleteOccurrence(id, occurrence.OriginalStart);
                    break;
                case EditScope.Following:
                    events.DeleteFollowing(id, occurrence.OriginalStart);
                    break;
                case EditScope.All:
                    events.Delete(id);
                    (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{occurrence.Title}' 반복 일정 삭제됨", () => events.Restore(id));
                    break;
            }
        };

        menu.Items.Add(complete);
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }
}
