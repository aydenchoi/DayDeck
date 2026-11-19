using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DayDeck.App.ViewModels;
using DayDeck.Core.Calendar;
using DayDeck.Core.Reminders;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views.Calendar;

/// <summary>
/// 일정 만들기/편집 대화상자. 저장/삭제까지 직접 처리한다.
/// 반복 일정의 회차를 열었으면 저장/삭제할 때 범위(이 일정만/이후 모두/전체)를 묻는다.
/// </summary>
public partial class EventEditorWindow : Window
{
    private static readonly (string Label, string AllDayLabel, int Offset)[] ReminderChoices =
    [
        ("정시", "당일 아침", 0), ("10분 전", "", -10), ("30분 전", "", -30), ("1시간 전", "", -60), ("1일 전", "전날 아침", -1440),
    ];

    private readonly App _app = App.Current;
    private readonly CalendarEvent? _existing;
    private readonly EventOccurrence? _occurrence;
    private readonly List<ToggleButton> _dayToggles = [];
    private readonly List<(ToggleButton Toggle, int Offset)> _reminderToggles = [];
    private readonly IReadOnlyList<long?> _listIds;
    private bool _loading = true;

    private EventEditorWindow(CalendarEvent? existing, EventOccurrence? occurrence, DateTime start, DateTime end, bool allDay)
    {
        InitializeComponent();
        DayDeck.App.Services.FontService.ScaleWindow(this);
        // 메모 서식 (할 일 메모, 메모 화면과 같은 마크다운). 미리보기에서 링크 열기, 체크 상자 체크.
        NotesToolbar.Target = NotesBox;
        NotesToolbar.PreviewChanged += (_, previewing) =>
        {
            NotesBox.Visibility = previewing ? Visibility.Collapsed : Visibility.Visible;
            NotesPreviewHost.Visibility = previewing ? Visibility.Visible : Visibility.Collapsed;
            if (previewing)
            {
                RenderNotesPreview();
            }
            else
            {
                NotesPreview.Document = null;
                NotesBox.Focus();
            }
        };
        _existing = existing;
        _occurrence = occurrence;
        Title = existing is null ? "새 일정" : "일정 편집";
        TitleBox.TextChanged += (_, _) => TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var slot = Math.Max(5, _app.Settings.Current.Calendar.TimeSlotMinutes);
        var times = Enumerable.Range(0, 24 * 60 / slot).Select(i => TimeOnly.MinValue.AddMinutes(i * slot)).Select(Format).ToList();
        foreach (var combo in new[] { StartTime, EndTime })
        {
            combo.ItemsSource = times;
            combo.IsEditable = true;
        }

        var weekOrder = Enumerable.Range(0, 7).Select(i => (DayOfWeek)((i + 1) % 7)); // 월~일
        foreach (var day in weekOrder)
        {
            var toggle = new ToggleButton { Content = CalendarStyle.DayName(day), Tag = day, Style = (Style)FindResource("DayToggle") };
            toggle.Click += OnRuleInputChanged;
            _dayToggles.Add(toggle);
            WeekDaysPanel.Children.Add(toggle);
        }

        var lists = _app.Tasks.GetLists();
        ListChoice.Items.Add("목록 없음");
        foreach (var list in lists)
        {
            ListChoice.Items.Add(list.Name);
        }
        _listIds = [null, .. lists.Select(l => (long?)l.Id)];

        // 값 채우기
        var source = existing;
        TitleBox.Text = occurrence?.Title ?? source?.Title ?? "";
        AllDayBox.IsChecked = source?.IsAllDay ?? allDay;
        var shownStart = occurrence?.Start ?? source?.Start ?? start;
        var shownEnd = occurrence?.End ?? source?.End ?? end;
        StartDate.SelectedDate = shownStart.Date;
        StartTime.Text = Format(TimeOnly.FromDateTime(shownStart));
        EndDate.SelectedDate = shownEnd.Date;
        EndTime.Text = Format(TimeOnly.FromDateTime(shownEnd));
        LocationBox.Text = source?.Location ?? "";
        NotesBox.Text = source?.Notes ?? "";
        ListChoice.SelectedIndex = Math.Max(0, _listIds.ToList().IndexOf(source?.ListId));

        var rule = source?.Rule;
        RepeatChoice.SelectedIndex = rule is null ? 0 : (int)rule.Frequency + 1;
        IntervalBox.Text = (rule?.Interval ?? 1).ToString(CultureInfo.InvariantCulture);
        foreach (var toggle in _dayToggles)
        {
            toggle.IsChecked = rule?.Days.Contains((DayOfWeek)toggle.Tag) ?? false;
        }
        EndChoice.SelectedIndex = rule?.Count is not null ? 1 : rule?.Until is not null ? 2 : 0;
        CountBox.Text = (rule?.Count ?? 10).ToString(CultureInfo.InvariantCulture);
        UntilDate.SelectedDate = rule?.Until?.ToDateTime(TimeOnly.MinValue) ?? shownStart.Date.AddMonths(3);
        UpdateMonthModeLabels();
        if (rule is { Frequency: RecurrenceFrequency.Monthly, WeekOfMonth: -1 })
        {
            MonthByLastWeekday.IsChecked = true;
        }
        else if (rule is { Frequency: RecurrenceFrequency.Monthly, WeekOfMonth: not null })
        {
            MonthByWeekday.IsChecked = true;
        }
        else
        {
            MonthByDay.IsChecked = true;
        }

        var existingOffsets = existing is null
            ? (_app.Settings.Current.Reminders.AutoAddDefault ? _app.Settings.Current.Reminders.DefaultOffsets : [])
            : _app.Reminders.GetForItem(existing.Id).Where(r => r.OffsetMinutes is not null).Select(r => r.OffsetMinutes!.Value).ToArray();
        BuildReminderToggles(existingOffsets);

        DeleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        CompleteButton.Visibility = existing is null ? Visibility.Collapsed : Visibility.Visible;
        var completed = occurrence?.IsCompleted ?? existing?.Status == ItemStatus.Completed;
        CompleteButton.Content = completed ? "완료 취소" : "✓ 완료";

        _loading = false;
        UpdateAllDayState();
        UpdateRepeatState();
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>새 일정 만들기. 저장했으면 true.</summary>
    public static bool Create(Window? owner, DateTime start, DateTime end, bool allDay) =>
        new EventEditorWindow(null, null, start, end, allDay) { Owner = owner }.ShowDialog() == true;

    /// <summary>일정(또는 반복 일정의 회차) 편집. 바꿨으면 true.</summary>
    public static bool Edit(Window? owner, EventOccurrence occurrence)
    {
        var current = App.Current.Events.Get(occurrence.Event.Id) ?? occurrence.Event;
        return new EventEditorWindow(current, current.IsRecurring ? occurrence : null, occurrence.Start, occurrence.End, current.IsAllDay)
        {
            Owner = owner,
        }.ShowDialog() == true;
    }

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private bool IsAllDay => AllDayBox.IsChecked == true;

    private void BuildReminderToggles(IEnumerable<int> selected)
    {
        var set = selected.ToHashSet();
        ReminderPanel.Children.Clear();
        _reminderToggles.Clear();
        var morning = Format(_app.Settings.Current.Reminders.GetDateOnlyReminderTime());
        foreach (var (label, allDayLabel, offset) in ReminderChoices)
        {
            if (IsAllDay && allDayLabel.Length == 0)
            {
                continue;
            }
            var toggle = new ToggleButton
            {
                Content = IsAllDay ? $"{allDayLabel} {morning}" : "🔔 " + label,
                IsChecked = set.Contains(offset),
                Style = (Style)FindResource("ChipToggle"),
            };
            _reminderToggles.Add((toggle, offset));
            ReminderPanel.Children.Add(toggle);
        }
    }

    // ---------- 입력 반응 ----------

    private void OnAllDayChanged(object sender, RoutedEventArgs e)
    {
        var selected = _reminderToggles.Where(t => t.Toggle.IsChecked == true).Select(t => t.Offset).ToList();
        UpdateAllDayState();
        BuildReminderToggles(selected);
    }

    private void UpdateAllDayState()
    {
        StartTime.Visibility = EndTime.Visibility = IsAllDay ? Visibility.Collapsed : Visibility.Visible;
        UpdateDuration();
    }

    private void OnStartDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || StartDate.SelectedDate is not { } start)
        {
            return;
        }
        // 시작을 옮기면 끝도 같은 길이만큼 옮긴다.
        if (EndDate.SelectedDate is { } end && end < start)
        {
            EndDate.SelectedDate = start;
        }
        UpdateMonthModeLabels();
        UpdateRepeatState();
    }

    private void OnStartTimeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StartTime.SelectedItem is not string text || !TryTime(text, out var start) || !TryTime(EndTime.Text, out var end))
        {
            return;
        }
        if (StartDate.SelectedDate == EndDate.SelectedDate && end <= start)
        {
            EndTime.Text = Format(start.AddHours(1));
        }
        UpdateDuration();
    }

    private void OnRepeatChanged(object sender, SelectionChangedEventArgs e) => UpdateRepeatState();

    private void OnRuleInputChanged(object sender, RoutedEventArgs e) => UpdateRepeatState();

    private void UpdateRepeatState()
    {
        if (_loading)
        {
            return;
        }
        var choice = RepeatChoice.SelectedIndex;
        var repeats = choice > 0;
        IntervalPanel.Visibility = EndPanel.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
        WeekDaysPanel.Visibility = choice == 2 ? Visibility.Visible : Visibility.Collapsed;
        MonthModePanel.Visibility = choice == 3 ? Visibility.Visible : Visibility.Collapsed;
        IntervalUnit.Text = choice switch { 1 => "일마다", 2 => "주마다", 3 => "개월마다", 4 => "년마다", _ => "" };
        CountBox.Visibility = EndChoice.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        UntilDate.Visibility = EndChoice.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (choice == 2 && _dayToggles.All(t => t.IsChecked != true) && StartDate.SelectedDate is { } start)
        {
            _dayToggles.First(t => (DayOfWeek)t.Tag == start.DayOfWeek).IsChecked = true;
        }

        RuleSummary.Text = BuildRule() is { } rule && StartDate.SelectedDate is { } s ? "↻ " + rule.Describe(DateOnly.FromDateTime(s)) : "";
    }

    private void UpdateMonthModeLabels()
    {
        var start = DateOnly.FromDateTime(StartDate.SelectedDate ?? DateTime.Today);
        var nth = (start.Day - 1) / 7 + 1;
        var dayName = CalendarStyle.Korean.DateTimeFormat.GetDayName(start.DayOfWeek);
        MonthByDay.Content = $"매월 {start.Day}일";
        MonthByWeekday.Content = $"매월 {nth}번째 {dayName}";
        var isLast = start.AddDays(7).Month != start.Month;
        MonthByLastWeekday.Content = $"매월 마지막 {dayName}";
        MonthByLastWeekday.Visibility = isLast ? Visibility.Visible : Visibility.Collapsed;
        if (!isLast && MonthByLastWeekday.IsChecked == true)
        {
            MonthByDay.IsChecked = true;
        }
    }

    private void UpdateDuration()
    {
        DurationText.Text = !IsAllDay && TryRange(out var start, out var end) && end > start
            ? (end - start).TotalMinutes < 60 ? $"{(end - start).TotalMinutes:0}분" : $"{(end - start).TotalHours:0.#}시간"
            : "";
    }

    private RecurrenceRule? BuildRule()
    {
        var start = DateOnly.FromDateTime(StartDate.SelectedDate ?? DateTime.Today);
        var interval = int.TryParse(IntervalBox.Text, out var i) && i > 0 ? i : 1;
        int? count = EndChoice.SelectedIndex == 1 && int.TryParse(CountBox.Text, out var c) && c > 0 ? c : null;
        DateOnly? until = EndChoice.SelectedIndex == 2 && UntilDate.SelectedDate is { } u ? DateOnly.FromDateTime(u) : null;

        return RepeatChoice.SelectedIndex switch
        {
            1 => new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Interval = interval, Count = count, Until = until },
            2 => new RecurrenceRule
            {
                Frequency = RecurrenceFrequency.Weekly, Interval = interval, Count = count, Until = until,
                Days = [.. _dayToggles.Where(t => t.IsChecked == true).Select(t => (DayOfWeek)t.Tag)],
            },
            3 => MonthByWeekday.IsChecked == true || MonthByLastWeekday.IsChecked == true
                ? new RecurrenceRule
                {
                    Frequency = RecurrenceFrequency.Monthly, Interval = interval, Count = count, Until = until,
                    Days = [start.DayOfWeek], WeekOfMonth = MonthByLastWeekday.IsChecked == true ? -1 : (start.Day - 1) / 7 + 1,
                }
                : new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = interval, Count = count, Until = until },
            4 => new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, Interval = interval, Count = count, Until = until },
            _ => null,
        };
    }

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private bool TryRange(out DateTime start, out DateTime end)
    {
        start = end = default;
        if (StartDate.SelectedDate is not { } startDate || EndDate.SelectedDate is not { } endDate)
        {
            return false;
        }
        if (IsAllDay)
        {
            start = startDate.Date;
            end = endDate.Date;
            return true;
        }
        if (!TryTime(StartTime.Text, out var startTime) || !TryTime(EndTime.Text, out var endTime))
        {
            return false;
        }
        start = DateOnly.FromDateTime(startDate).ToDateTime(startTime);
        end = DateOnly.FromDateTime(endDate).ToDateTime(endTime);
        return true;
    }

    // ---------- 저장 ----------

    private void RenderNotesPreview() =>
        NotesPreview.Document = MarkdownRenderer.Render(NotesBox.Text, (line, done) =>
        {
            MarkdownCommands.ToggleTaskLine(NotesBox, line, done);
            RenderNotesPreview();
        }, compact: true);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "제목을 입력하세요.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            TitleBox.Focus();
            return;
        }
        if (!TryRange(out var start, out var end))
        {
            MessageBox.Show(this, "시작과 끝 날짜/시간을 확인하세요. 시간은 14:30처럼 입력합니다.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (end < start || (!IsAllDay && end == start))
        {
            end = IsAllDay ? start : start.AddHours(1);
        }

        var edited = (_existing?.Clone() ?? new CalendarEvent());
        edited.Title = TitleBox.Text.Trim();
        edited.IsAllDay = IsAllDay;
        edited.Start = start;
        edited.End = end;
        edited.Location = LocationBox.Text.Trim();
        edited.Notes = NotesBox.Text;
        edited.ListId = _listIds[Math.Max(0, ListChoice.SelectedIndex)];
        edited.RRule = BuildRule()?.ToString();

        var events = _app.Events;
        long reminderTarget;
        if (_existing is null)
        {
            reminderTarget = events.Add(edited);
        }
        else if (_occurrence is null)
        {
            events.Update(edited);
            reminderTarget = edited.Id;
        }
        else
        {
            var scope = ScopeDialog.Ask(this, "반복 일정 저장", "반복 일정입니다. 어디까지 바꿀까요?");
            switch (scope)
            {
                case EditScope.Cancel:
                    return;
                case EditScope.ThisOnly:
                    // 회차 하나는 시간과 제목만 바꾼다 (장소/메모/반복은 전체 설정을 따른다).
                    events.UpdateOccurrence(_existing.Id, _occurrence.OriginalStart, start, end, edited.Title);
                    reminderTarget = _existing.Id;
                    break;
                case EditScope.Following:
                    reminderTarget = events.UpdateFollowing(_existing.Id, _occurrence.OriginalStart, edited);
                    break;
                default:
                    // 회차에서 옮긴 만큼 원본 시작을 옮긴다.
                    edited.Start = _existing.Start + (start - _occurrence.OriginalStart);
                    edited.End = edited.Start + (end - start);
                    events.Update(edited);
                    reminderTarget = _existing.Id;
                    break;
            }
        }

        SyncReminders(reminderTarget);
        DialogResult = true;
    }

    private void SyncReminders(long eventId)
    {
        var wanted = _reminderToggles.Where(t => t.Toggle.IsChecked == true).Select(t => t.Offset).ToHashSet();
        var existing = _app.Reminders.GetForItem(eventId).Where(r => r.OffsetMinutes is not null).ToList();
        foreach (var reminder in existing.Where(r => !wanted.Contains(r.OffsetMinutes!.Value)))
        {
            _app.Reminders.Remove(reminder.Id);
        }
        foreach (var offset in wanted.Where(o => existing.All(r => r.OffsetMinutes != o)))
        {
            _app.Reminders.AddRelative(eventId, offset);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_existing is null)
        {
            return;
        }

        if (_occurrence is not null)
        {
            switch (ScopeDialog.Ask(this, "반복 일정 삭제", $"'{_existing.Title}'은(는) 반복 일정입니다. 어디까지 지울까요?"))
            {
                case EditScope.ThisOnly:
                    _app.Events.DeleteOccurrence(_existing.Id, _occurrence.OriginalStart);
                    break;
                case EditScope.Following:
                    _app.Events.DeleteFollowing(_existing.Id, _occurrence.OriginalStart);
                    break;
                case EditScope.All:
                    _app.Events.Delete(_existing.Id);
                    break;
                default:
                    return;
            }
        }
        else if (MessageBox.Show(this, $"'{_existing.Title}' 일정을 삭제할까요?", "일정 삭제", MessageBoxButton.OKCancel,
                     MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        {
            _app.Events.Delete(_existing.Id);
        }
        else
        {
            return;
        }
        DialogResult = true;
    }

    private void OnToggleComplete(object sender, RoutedEventArgs e)
    {
        if (_existing is null)
        {
            return;
        }
        if (_occurrence is not null)
        {
            _app.Events.SetOccurrenceCompleted(_existing.Id, _occurrence.OriginalStart, !_occurrence.IsCompleted);
        }
        else
        {
            _app.Events.SetCompleted(_existing.Id, _existing.Status == ItemStatus.Open);
        }
        DialogResult = true;
    }
}
