using DayDeck.Core.Reminders;
using DayDeck.Core.Todo;

namespace DayDeck.App.ViewModels;

public enum ReminderCardKind
{
    /// <summary>할 일 하나의 알림. 완료/스누즈/열기.</summary>
    Single,

    /// <summary>놓친 알림 여러 개. 모두 확인/나중에.</summary>
    Missed,

    /// <summary>일일 브리핑.</summary>
    Briefing,
}

/// <summary>알림 팝업의 카드 하나.</summary>
public sealed class ReminderCardViewModel : ObservableObject
{
    private ReminderCardViewModel(ReminderCardKind kind, string header, string title, string body, IReadOnlyList<DueReminder> reminders)
    {
        Kind = kind;
        Header = header;
        Title = title;
        Body = body;
        Reminders = reminders;
    }

    public ReminderCardKind Kind { get; }
    public string Header { get; }
    public string Title { get; }
    public string Body { get; }
    public IReadOnlyList<DueReminder> Reminders { get; }
    public IReadOnlyList<string> Lines { get; private init; } = [];
    public bool IsOverdue { get; private init; }

    public bool IsSingle => Kind == ReminderCardKind.Single;
    public bool IsMissed => Kind == ReminderCardKind.Missed;
    public bool IsBriefing => Kind == ReminderCardKind.Briefing;
    public bool HasBody => Body.Length > 0;
    public bool HasLines => Lines.Count > 0;
    public long? ItemId => IsSingle ? Reminders[0].ItemId : null;

    /// <summary>UI 자동화와 화면 읽기 프로그램용 이름.</summary>
    public string AutomationName => Kind switch
    {
        ReminderCardKind.Single => $"알림: {Reminders[0].Title}",
        ReminderCardKind.Missed => $"놓친 알림 {Reminders.Count}개",
        _ => "오늘의 할 일",
    };

    public static ReminderCardViewModel ForReminder(DueReminder reminder, DateTime now)
    {
        var header = reminder.IsMissed ? "⚠ 놓친 알림" : reminder.IsPersistent ? "🔔 알림 (확인할 때까지 반복)" : "🔔 알림";
        var (title, overdue) = DescribeDue(reminder, now);
        var details = new List<string>();
        if (reminder.DueDate is { } date)
        {
            var today = DateOnly.FromDateTime(now);
            var due = TaskFormatting.FormatDate(date, today);
            details.Add(reminder.IsEvent
                ? reminder.DueTime is { } start ? $"{due} {TaskFormatting.FormatTime(start)} 시작" : $"{due} (종일)"
                : reminder.DueTime is { } time ? $"{due} {TaskFormatting.FormatTime(time)} 마감" : $"{due}까지");
        }
        if (reminder.IsMissed)
        {
            details.Add($"{TaskFormatting.FormatTime(TimeOnly.FromDateTime(reminder.FireAt))}에 울려야 했던 알림");
        }
        if (reminder.ListName is { } list)
        {
            details.Add(list);
        }

        return new ReminderCardViewModel(ReminderCardKind.Single, header, title + reminder.Title, string.Join(" · ", details), [reminder])
        {
            IsOverdue = overdue,
        };
    }

    public static ReminderCardViewModel ForMissed(IReadOnlyList<DueReminder> reminders, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var lines = reminders.Select(r =>
        {
            var when = TaskFormatting.FormatDate(DateOnly.FromDateTime(r.FireAt), today) + " " + TaskFormatting.FormatTime(TimeOnly.FromDateTime(r.FireAt));
            return $"· {r.Title} — {when}";
        }).ToList();

        return new ReminderCardViewModel(ReminderCardKind.Missed, "⚠ 놓친 알림",
            $"놓친 알림 {reminders.Count}개", "PC가 꺼져 있거나 절전 중이었거나 알림을 일시 중지한 동안 지난 알림입니다.", reminders)
        {
            Lines = lines,
        };
    }

    public static ReminderCardViewModel ForBriefing(Briefing briefing, DateTime now)
    {
        var title = briefing.TodayOpen == 0
            ? "오늘 남은 할 일이 없습니다"
            : briefing.Overdue > 0
                ? $"오늘 할 일 {briefing.TodayOpen}개 · 기한 지남 {briefing.Overdue}개"
                : $"오늘 할 일 {briefing.TodayOpen}개";
        var lines = briefing.TopItems.Select(i =>
        {
            var prefix = TaskRules.IsOverdue(i, now) ? "⚠ " : "· ";
            var time = i.DueTime is { } t && i.DueDate == briefing.Date ? $"{TaskFormatting.FormatTime(t)} " : "";
            return prefix + time + i.Title;
        }).ToList();
        if (briefing.TodayOpen > briefing.TopItems.Count)
        {
            lines.Add($"외 {briefing.TodayOpen - briefing.TopItems.Count}개");
        }

        return new ReminderCardViewModel(ReminderCardKind.Briefing, $"☀ 오늘의 할 일 · {TaskFormatting.FormatDayHeader(briefing.Date)}",
            title, "", [])
        {
            Lines = lines,
            IsOverdue = briefing.Overdue > 0,
        };
    }

    /// <summary>"10분 후: ", "지금: ", "마감 지남: " 같은 앞말.</summary>
    private static (string Prefix, bool Overdue) DescribeDue(DueReminder reminder, DateTime now)
    {
        if (reminder.DueDate is not { } date)
        {
            return ("", false);
        }

        var deadline = reminder.DueTime is { } time ? date.ToDateTime(time) : date.ToDateTime(TimeOnly.MaxValue);
        var left = deadline - now;
        if (left < TimeSpan.Zero)
        {
            return reminder.IsEvent ? ("시작함: ", false) : ("마감 지남: ", true);
        }
        if (reminder.IsEvent && reminder.DueTime is null)
        {
            return (date == DateOnly.FromDateTime(now) ? "오늘: " : "", false);
        }
        if (reminder.DueTime is null)
        {
            return (date == DateOnly.FromDateTime(now) ? "오늘까지: " : "", false);
        }
        if (left < TimeSpan.FromMinutes(1))
        {
            return ("지금: ", false);
        }
        if (left < TimeSpan.FromHours(1))
        {
            return ($"{(int)Math.Ceiling(left.TotalMinutes)}분 후: ", false);
        }
        if (left < TimeSpan.FromDays(1))
        {
            var hours = (int)left.TotalHours;
            var minutes = left.Minutes;
            return (minutes > 0 ? $"{hours}시간 {minutes}분 후: " : $"{hours}시간 후: ", false);
        }
        return ("", false);
    }
}
