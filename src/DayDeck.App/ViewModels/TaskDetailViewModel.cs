using System.Collections.ObjectModel;
using DayDeck.Core.AutoSave;
using DayDeck.Core.Calendar;
using DayDeck.Core.Reminders;
using DayDeck.Core.Todo;

namespace DayDeck.App.ViewModels;

/// <summary>
/// 오른쪽 상세 패널. 제목과 메모는 타이핑 중이므로 자동 저장(디바운스/주기/포커스 이동)에 맡기고,
/// 날짜·우선순위·목록 같은 선택형 값은 바꾸는 즉시 저장한다.
/// </summary>
public sealed class TaskDetailViewModel : ObservableObject, IAutoSaveTarget
{
    public const string NoTime = "시간 없음";

    private readonly TaskRepository _repo;
    private readonly ReminderRepository _reminders;
    private readonly AutoSaveService _autoSave;
    private readonly TaskItem _item;
    private string _title;
    private string _notes;
    private bool _isDirty;

    public TaskDetailViewModel(TaskItem item, TaskRepository repo, ReminderRepository reminders, AutoSaveService autoSave, int timeSlotMinutes)
    {
        _item = item;
        _repo = repo;
        _reminders = reminders;
        _autoSave = autoSave;
        _title = item.Title;
        _notes = item.Notes ?? "";

        Lists = [new ListOption(null, "목록 없음"), .. repo.GetLists().Select(l => new ListOption(l.Id, l.Name))];
        TimeOptions = BuildTimeOptions(timeSlotMinutes, item.DueTime);
        ReloadSubtasks();
        ReloadReminders();
    }

    public long Id => _item.Id;

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                MarkEdited();
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value))
            {
                MarkEdited();
            }
        }
    }

    public bool IsCompleted
    {
        get => _item.Status != ItemStatus.Open;
        set
        {
            if (value == IsCompleted)
            {
                return;
            }
            Save();
            _repo.SetCompleted(_item.Id, value);
            _item.Status = value ? ItemStatus.Completed : ItemStatus.Open;
            _item.CompletedAt = value ? DateTime.Now : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsMyDay
    {
        get => _item.MyDay == DateOnly.FromDateTime(DateTime.Now);
        set
        {
            if (value == IsMyDay)
            {
                return;
            }
            _item.MyDay = value ? DateOnly.FromDateTime(DateTime.Now) : null;
            SaveNow();
            OnPropertyChanged();
        }
    }

    /// <summary>DatePicker용.</summary>
    public DateTime? DueDate
    {
        get => _item.DueDate?.ToDateTime(TimeOnly.MinValue);
        set
        {
            var date = value is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
            if (date == _item.DueDate)
            {
                return;
            }
            var hadDue = _item.DueDate is not null;
            _item.DueDate = date;
            if (_item.DueDate is null)
            {
                _item.DueTime = null;
                OnPropertyChanged(nameof(DueTime));
            }
            SaveNow();
            if (!hadDue && date is not null)
            {
                _reminders.AddDefaultsIfNone(_item.Id);
            }
            ReloadReminders();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDueDate));
        }
    }

    public bool HasDueDate => _item.DueDate is not null;

    public IReadOnlyList<string> TimeOptions { get; }

    public string DueTime
    {
        get => _item.DueTime is { } t ? TaskFormatting.FormatTime(t) : NoTime;
        set
        {
            // 콤보박스는 항목 목록이 바뀌는 순간 null을 되돌려 쓸 수 있다. 사용자의 선택이 아니므로 무시한다.
            if (value is null || value == DueTime)
            {
                return;
            }
            _item.DueTime = value == NoTime ? null : TimeOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            SaveNow();
            ReloadReminders();
            OnPropertyChanged();
        }
    }

    public int PriorityIndex
    {
        get => (int)_item.Priority;
        set
        {
            if (value < 0 || value == PriorityIndex)
            {
                return;
            }
            _item.Priority = (Priority)value;
            SaveNow();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ListOption> Lists { get; }

    // ---------- 반복 ----------

    private static readonly (string Label, string? RRule)[] RepeatPresets =
    [
        ("반복 안 함", null),
        ("매일", "FREQ=DAILY"),
        ("평일 (월~금)", new RecurrenceRule { Frequency = RecurrenceFrequency.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] }.ToString()),
        ("매주", "FREQ=WEEKLY"),
        ("2주마다", "FREQ=WEEKLY;INTERVAL=2"),
        ("매월", "FREQ=MONTHLY"),
        ("매년", "FREQ=YEARLY"),
    ];

    private List<RepeatOption>? _repeatOptions;

    /// <summary>반복 선택지. 빠른 추가 등으로 만든 다른 규칙이면 그 설명을 한 줄 더 넣는다.</summary>
    public IReadOnlyList<RepeatOption> RepeatOptions => _repeatOptions ??= BuildRepeatOptions();

    private List<RepeatOption> BuildRepeatOptions()
    {
        var options = RepeatPresets.Select(p => new RepeatOption(p.Label, p.RRule)).ToList();
        if (_item.RRule is { } current && options.All(o => o.RRule != current) && RecurrenceRule.Parse(current) is { } rule)
        {
            options.Add(new RepeatOption(rule.Describe(_item.DueDate ?? DateOnly.FromDateTime(DateTime.Now)), current));
        }
        return options;
    }

    public RepeatOption SelectedRepeat
    {
        get => RepeatOptions.FirstOrDefault(o => o.RRule == _item.RRule) ?? RepeatOptions[0];
        set
        {
            if (value is null || value.RRule == _item.RRule)
            {
                return;
            }
            _item.RRule = value.RRule;
            if (value.RRule is not null && _item.DueDate is null)
            {
                // 반복은 마감일을 기준으로 다음 회차를 정한다.
                _item.DueDate = DateOnly.FromDateTime(DateTime.Now);
                OnPropertyChanged(nameof(DueDate));
                OnPropertyChanged(nameof(HasDueDate));
            }
            SaveNow();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepeatHint));
        }
    }

    public string RepeatHint => _item.Rule is { } rule
        ? $"↻ {rule.Describe(_item.DueDate ?? DateOnly.FromDateTime(DateTime.Now))} · 완료하면 다음 회차가 새로 생깁니다"
        : "";

    // ---------- 태그 ----------

    /// <summary>쉼표나 공백으로 구분한 태그. 입력란을 떠날 때 저장한다.</summary>
    public string TagsText
    {
        get => string.Join(", ", _item.Tags);
        set
        {
            var tags = TaskRepository.NormalizeTags((value ?? "").Split([',', ' ', '\u3001'], StringSplitOptions.RemoveEmptyEntries));
            if (tags.SequenceEqual(_item.Tags))
            {
                return;
            }
            _repo.SetTags(_item.Id, tags);
            _item.Tags = tags;
            OnPropertyChanged();
        }
    }

    // ---------- 취소 ----------

    public bool IsCancelled => _item.Status == ItemStatus.Cancelled;

    public string CancelLabel => IsCancelled ? "취소 해제" : "⊘ 하지 않기로 함";

    public void ToggleCancelled()
    {
        var cancel = !IsCancelled;
        _repo.SetCancelled(_item.Id, cancel);
        _item.Status = cancel ? ItemStatus.Cancelled : ItemStatus.Open;
        _item.CompletedAt = cancel ? DateTime.Now : null;
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(StatusText));
    }

    public ListOption SelectedList
    {
        get => Lists.FirstOrDefault(l => l.Id == _item.ListId) ?? Lists[0];
        set
        {
            // DueTime과 같은 이유로 null은 무시한다. "목록 없음"은 Id가 null인 항목으로 고른다.
            if (value is null || value.Id == _item.ListId)
            {
                return;
            }
            _item.ListId = value.Id;
            SaveNow();
            OnPropertyChanged();
        }
    }

    public ObservableCollection<SubtaskViewModel> Subtasks { get; } = [];

    public ObservableCollection<ReminderItemViewModel> Reminders { get; } = [];

    public bool HasReminders => Reminders.Count > 0;

    /// <summary>끈질긴 알림: 이 할 일의 알림을 확인할 때까지 반복한다.</summary>
    public bool IsPersistent
    {
        get => _reminders.GetForItem(_item.Id).Any(r => r.IsPersistent);
        set
        {
            if (value == IsPersistent)
            {
                return;
            }
            _reminders.SetPersistent(_item.Id, value);
            OnPropertyChanged();
        }
    }

    public void AddRelativeReminder(int offsetMinutes)
    {
        if (_reminders.GetForItem(_item.Id).Any(r => r.OffsetMinutes == offsetMinutes))
        {
            return;
        }
        _reminders.AddRelative(_item.Id, offsetMinutes, IsPersistent);
        ReloadReminders();
    }

    public void AddAbsoluteReminder(DateTime at)
    {
        _reminders.AddAbsolute(_item.Id, at, IsPersistent);
        ReloadReminders();
    }

    public void RemoveReminder(ReminderItemViewModel reminder)
    {
        _reminders.Remove(reminder.Id);
        ReloadReminders();
    }

    public string StatusText => _item.Status != ItemStatus.Open && _item.CompletedAt is { } done
        ? $"{done:yyyy-MM-dd HH:mm} {(_item.Status == ItemStatus.Cancelled ? "취소" : "완료")} · {_item.CreatedAt:yyyy-MM-dd HH:mm} 만듦"
        : $"{_item.CreatedAt:yyyy-MM-dd HH:mm} 만듦";

    public bool IsDirty => _isDirty;

    public void SetDue(DateOnly? date)
    {
        DueDate = date?.ToDateTime(TimeOnly.MinValue);
    }

    public void AddSubtask(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }
        _repo.Add(new TaskItem { Title = title, ParentId = _item.Id, ListId = _item.ListId });
        ReloadSubtasks();
    }

    public void ToggleSubtask(SubtaskViewModel subtask)
    {
        _repo.SetCompleted(subtask.Id, !subtask.IsDone);
        ReloadSubtasks();
    }

    public void DeleteSubtask(SubtaskViewModel subtask)
    {
        _repo.Delete(subtask.Id);
        ReloadSubtasks();
    }

    public void Save()
    {
        if (!_isDirty)
        {
            return;
        }

        // 제목을 모두 지운 채로 저장되면 원래 제목을 유지한다.
        if (!string.IsNullOrWhiteSpace(_title))
        {
            _item.Title = _title.Trim();
        }
        _item.Notes = _notes;
        _repo.Update(_item);
        _isDirty = false;
    }

    public override string ToString() => $"할 일 #{_item.Id}";

    private void SaveNow()
    {
        _isDirty = true;
        Save();
    }

    private void MarkEdited()
    {
        _isDirty = true;
        _autoSave.NotifyEdited(this);
    }

    private void ReloadReminders()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        Reminders.Clear();
        foreach (var reminder in _reminders.GetForItem(_item.Id))
        {
            string status;
            if (reminder.NextFireAt is { } next)
            {
                var date = DateOnly.FromDateTime(next);
                status = (date == today ? "" : TaskFormatting.FormatDate(date, today) + " ") + TaskFormatting.FormatTime(TimeOnly.FromDateTime(next))
                         + (reminder.SnoozedUntil is not null ? " (스누즈)" : "");
            }
            else if (reminder.AbsoluteAt is null && _item.DueDate is null)
            {
                status = "마감을 정하면 울립니다";
            }
            else if (_item.Status != ItemStatus.Open)
            {
                status = "완료된 할 일";
            }
            else
            {
                status = "지남";
            }
            Reminders.Add(new ReminderItemViewModel(reminder.Id, "🔔 " + ReminderRules.Describe(reminder, today), status,
                reminder.NextFireAt is not null));
        }
        OnPropertyChanged(nameof(HasReminders));
        OnPropertyChanged(nameof(IsPersistent));
    }

    private void ReloadSubtasks()
    {
        Subtasks.Clear();
        foreach (var subtask in _repo.GetSubtasks(_item.Id))
        {
            Subtasks.Add(new SubtaskViewModel(subtask.Id, subtask.Title, subtask.Status != ItemStatus.Open));
        }
    }

    private static List<string> BuildTimeOptions(int slotMinutes, TimeOnly? current)
    {
        var options = new List<string> { NoTime };
        var slot = Math.Max(5, slotMinutes);
        for (var minutes = 0; minutes < 24 * 60; minutes += slot)
        {
            options.Add(TaskFormatting.FormatTime(new TimeOnly(minutes / 60, minutes % 60)));
        }

        if (current is { } time && !options.Contains(TaskFormatting.FormatTime(time)))
        {
            options.Add(TaskFormatting.FormatTime(time));
            options.Sort((a, b) => a == NoTime ? -1 : b == NoTime ? 1 : string.CompareOrdinal(a, b));
        }
        return options;
    }
}

public sealed record ListOption(long? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record SubtaskViewModel(long Id, string Title, bool IsDone);

public sealed record RepeatOption(string Label, string? RRule)
{
    public override string ToString() => Label;
}

public sealed record ReminderItemViewModel(long Id, string Text, string Status, bool IsActive);
