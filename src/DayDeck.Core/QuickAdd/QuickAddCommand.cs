using DayDeck.Core.Calendar;
using DayDeck.Core.Reminders;
using DayDeck.Core.Todo;

namespace DayDeck.Core.QuickAdd;

/// <summary>빠른 입력을 어디서 했는지에 따른 기본값 (목록 화면에서는 그 목록, 오늘 화면에서는 오늘 할 일).</summary>
public sealed record QuickAddContext(long? ListId = null, bool MyDayWhenNoDate = true, DateOnly? DefaultDueDate = null);

public sealed record QuickAddCreated(QuickAddKind Kind, long Id, string Title, string Summary);

/// <summary>해석 결과로 할 일 또는 일정을 실제로 만든다.</summary>
public sealed class QuickAddCommand
{
    private static readonly string[] ListColors = ["#2B6CD4", "#0F7B0F", "#7A3FBF", "#C75B00", "#00807A", "#C42B1C"];

    private readonly TaskRepository _tasks;
    private readonly EventRepository _events;
    private readonly ReminderRepository _reminders;
    private readonly TimeProvider _time;

    public QuickAddCommand(TaskRepository tasks, EventRepository events, ReminderRepository reminders, TimeProvider time)
    {
        _tasks = tasks;
        _events = events;
        _reminders = reminders;
        _time = time;
    }

    public QuickAddCreated Create(QuickAddResult result, QuickAddContext context)
    {
        var listId = ResolveList(result.ListName) ?? context.ListId;

        if (result.Kind == QuickAddKind.Event)
        {
            var start = result.Date!.Value.ToDateTime(result.Time ?? TimeOnly.MinValue);
            var end = (result.EndDate ?? result.Date.Value).ToDateTime(result.EndTime ?? TimeOnly.MinValue);
            var id = _events.Add(new CalendarEvent
            {
                Title = result.Title,
                IsAllDay = result.Time is null,
                Start = start,
                End = end,
                RRule = result.Rule?.ToString(),
                ListId = listId,
            });
            AddReminders(id, result.ReminderOffsets);
            return new QuickAddCreated(QuickAddKind.Event, id, result.Title, Describe(result));
        }

        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var due = result.Date ?? context.DefaultDueDate;
        var taskId = _tasks.Add(new TaskItem
        {
            Title = result.Title,
            DueDate = due,
            DueTime = result.Time,
            ListId = listId,
            Priority = result.Priority,
            // "매주 금요일까지 주간보고"처럼 반복 + 마감이면 반복 할 일.
            RRule = result.Rule?.ToString(),
            Tags = result.Tags ?? [],
            MyDay = due is null && context.MyDayWhenNoDate && context.ListId is null && listId is null ? today : null,
        });
        if (result.ReminderOffsets.Count > 0)
        {
            AddReminders(taskId, result.ReminderOffsets);
        }
        else if (due is not null)
        {
            _reminders.AddDefaultsIfNone(taskId);
        }
        return new QuickAddCreated(QuickAddKind.Task, taskId, result.Title, Describe(result with { Date = due }));
    }

    private void AddReminders(long itemId, IReadOnlyList<int> offsets)
    {
        if (offsets.Count == 0)
        {
            _reminders.AddDefaultsIfNone(itemId);
            return;
        }
        foreach (var offset in offsets.Distinct())
        {
            _reminders.AddRelative(itemId, offset);
        }
    }

    /// <summary>"#업무" → 목록 id. 없는 이름이면 새 목록을 만든다.</summary>
    private long? ResolveList(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var lists = _tasks.GetLists();
        var existing = lists.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase));
        return existing?.Id ?? _tasks.AddList(name, ListColors[lists.Count % ListColors.Length]);
    }

    /// <summary>"할 일 · 금요일까지", "일정 · 내일 15:00–16:00 · 매주 월" 같은 짧은 설명.</summary>
    public string Describe(QuickAddResult result)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var parts = new List<string> { result.Kind == QuickAddKind.Event ? "📅 일정" : "☐ 할 일" };
        if (result.Date is { } date)
        {
            var when = TaskFormatting.FormatDate(date, today);
            if (result.Kind == QuickAddKind.Event)
            {
                if (result.Time is { } t)
                {
                    when += $" {TaskFormatting.FormatTime(t)}";
                    if (result.EndTime is { } et)
                    {
                        when += result.EndDate == date ? $"–{TaskFormatting.FormatTime(et)}" : $" – {TaskFormatting.FormatDate(result.EndDate!.Value, today)} {TaskFormatting.FormatTime(et)}";
                    }
                }
                else if (result.EndDate is { } end && end != date)
                {
                    when += $" – {TaskFormatting.FormatDate(end, today)} (종일)";
                }
                else
                {
                    when += " (종일)";
                }
            }
            else
            {
                when += result.Time is { } t ? $" {TaskFormatting.FormatTime(t)}까지" : "까지";
            }
            parts.Add(when);
        }
        else if (result.Kind == QuickAddKind.Task)
        {
            parts.Add("오늘 할 일");
        }
        if (result.Rule is { } rule)
        {
            parts.Add("↻ " + rule.Describe(result.Date ?? today));
        }
        if (result.ReminderOffsets.Count > 0)
        {
            parts.Add("🔔 " + string.Join(", ", result.ReminderOffsets.Select(o => ReminderRules.Describe(new Reminder { OffsetMinutes = o }, today))));
        }
        if (result.ListName is { } list)
        {
            parts.Add("#" + list);
        }
        if (result.Tags is { Count: > 0 } tags)
        {
            parts.Add(string.Join(" ", tags.Select(t => "@" + t)));
        }
        if (result.Priority != Priority.None)
        {
            parts.Add(TaskFormatting.FormatPriority(result.Priority));
        }
        return string.Join(" · ", parts);
    }
}
