using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Reminders;

/// <summary>
/// 할 일에 붙은 알림 하나 (reminders 테이블). 마감 기준 상대 알림(<see cref="OffsetMinutes"/>)이거나
/// 절대 시각 알림(<see cref="AbsoluteAt"/>)이다.
/// </summary>
public sealed class Reminder
{
    public long Id { get; set; }
    public long ItemId { get; set; }

    /// <summary>마감 기준 분 (음수 = 마감 전). 마감에 시간이 없으면 설정의 기준 시각(기본 09:00)을 마감으로 본다.</summary>
    public int? OffsetMinutes { get; set; }

    public DateTime? AbsoluteAt { get; set; }

    /// <summary>끈질긴 알림: 확인할 때까지 반복해서 울린다.</summary>
    public bool IsPersistent { get; set; }

    public DateTime? NextFireAt { get; set; }
    public DateTime? TargetAt { get; set; }
    public DateTime? SnoozedUntil { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public DateTime? DismissedFor { get; set; }
}

/// <summary>지금 울려야 하는 알림과 표시에 필요한 할 일 정보.</summary>
public sealed record DueReminder(
    long ReminderId,
    long ItemId,
    string Title,
    string? ListName,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    DateTime FireAt,
    bool IsPersistent,
    bool IsMissed,
    bool IsEvent = false,
    Priority Priority = Priority.None);

/// <summary>알림 기준이 되는 할 일/일정 정보. 할 일은 마감, 일정은 시작 시각이 기준이다.</summary>
public sealed record ReminderBase(bool IsActive, DateOnly? Date, TimeOnly? Time, RecurrenceRule? Rule = null);

/// <summary>일일 브리핑 내용.</summary>
public sealed record Briefing(DateOnly Date, int TodayOpen, int Overdue, IReadOnlyList<TaskItem> TopItems);

/// <summary>알림 시각 계산 규칙.</summary>
public static class ReminderRules
{
    /// <summary>놓친 알림으로 볼 지연 시간. 이보다 늦게 울리면 "놓친 알림"으로 묶어 보여 준다.</summary>
    public static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(2);

    /// <summary>알림이 울릴 시각. 상대 알림인데 마감이 없으면 null.</summary>
    public static DateTime? ComputeTarget(Reminder reminder, DateOnly? dueDate, TimeOnly? dueTime, TimeOnly dateOnlyTime)
    {
        if (reminder.AbsoluteAt is { } absolute)
        {
            return absolute;
        }
        if (reminder.OffsetMinutes is not { } offset || dueDate is not { } date)
        {
            return null;
        }
        return date.ToDateTime(dueTime ?? dateOnlyTime).AddMinutes(offset);
    }

    /// <summary>
    /// 기준(마감/시작)과 반복을 고려한 알림 시각. 반복 일정이면 회차마다 울린다:
    /// 마지막으로 처리한 회차 이후 이미 지난 회차가 있으면 그중 가장 최근 것(놓친 알림, 과거 허용),
    /// 없으면 다음 회차.
    /// </summary>
    public static (DateTime? Target, bool AllowPast) ComputeTarget(Reminder reminder, ReminderBase b, TimeOnly dateOnlyTime, DateTime now)
    {
        if (reminder.AbsoluteAt is not null || b.Rule is null || b.Date is not { } firstDate)
        {
            return (ComputeTarget(reminder, b.Date, b.Time, dateOnlyTime), false);
        }

        var offset = TimeSpan.FromMinutes(reminder.OffsetMinutes ?? 0);
        var time = b.Time ?? dateOnlyTime;
        DateTime? missed = null;
        // 확인한 회차가 없더라도, 예약해 둔 회차가 지났다면 그 이후는 놓친 것이다.
        var last = reminder.DismissedFor ?? reminder.TargetAt?.AddTicks(-1);
        foreach (var start in b.Rule.Expand(firstDate.ToDateTime(b.Time ?? TimeOnly.MinValue), now.AddDays(400)))
        {
            var target = DateOnly.FromDateTime(start).ToDateTime(time) + offset;
            if (target <= now)
            {
                if (last is { } l && target > l)
                {
                    missed = target;
                }
                continue;
            }
            return missed is not null ? (missed, true) : (target, false);
        }
        return missed is not null ? (missed, true) : (null, false);
    }

    /// <summary>
    /// 할 일/마감이 바뀐 뒤 다음 울릴 시각을 다시 정한다. 반환값이 true면 저장해야 한다.
    /// <list type="bullet">
    /// <item>할 일이 완료/삭제되었거나 마감이 없어졌으면 끈다.</item>
    /// <item>알림 시각이 바뀌었으면 스누즈/확인 기록을 지우고, 미래면 그 시각에 켠다 (과거로 바꾼 경우는 울리지 않는다).</item>
    /// <item>알림 시각이 그대로면 손대지 않는다. 그래서 앱이 꺼져 있는 동안 지난 알림은 다음 실행 때 "놓친 알림"으로 울린다.</item>
    /// </list>
    /// </summary>
    /// <param name="allowPast">지난 시각이어도 울린다 (반복 일정의 놓친 회차).</param>
    public static bool Recompute(Reminder reminder, bool itemIsActive, DateTime? target, DateTime now, bool allowPast = false)
    {
        if (!itemIsActive || target is null)
        {
            // target_at도 지워서, 할 일을 다시 열면 "시각이 바뀐 것"으로 보고 다시 켠다.
            return Set(reminder, next: null, target: null, resetHistory: false);
        }

        if (reminder.TargetAt == target)
        {
            return false;
        }

        // 반복 일정의 다음 회차로 넘어갈 때는 마지막으로 처리한 회차 기록(DismissedFor)을 남겨 둔다.
        var dismissed = reminder.DismissedFor;
        var changed = Set(reminder, next: target > now || allowPast ? target : null, target: target, resetHistory: true);
        if (allowPast || dismissed is not null && dismissed < target)
        {
            reminder.DismissedFor = dismissed;
        }
        return changed;
    }

    private static bool Set(Reminder reminder, DateTime? next, DateTime? target, bool resetHistory)
    {
        var changed = reminder.NextFireAt != next || reminder.TargetAt != target
                      || (resetHistory && (reminder.SnoozedUntil, reminder.DismissedFor, reminder.LastFiredAt) != (null, null, null));
        reminder.NextFireAt = next;
        reminder.TargetAt = target;
        if (resetHistory)
        {
            reminder.SnoozedUntil = null;
            reminder.DismissedFor = null;
            reminder.LastFiredAt = null;
        }
        return changed;
    }

    /// <summary>울렸다. 끈질긴 알림은 반복 간격 뒤에 다시, 아니면 이 시각에 대해서는 끝.</summary>
    public static void MarkFired(Reminder reminder, DateTime now, TimeSpan persistentRepeat)
    {
        reminder.LastFiredAt = now;
        reminder.SnoozedUntil = null;
        if (reminder.IsPersistent)
        {
            reminder.NextFireAt = now + persistentRepeat;
        }
        else
        {
            reminder.NextFireAt = null;
            reminder.DismissedFor = reminder.TargetAt;
        }
    }

    public static void Snooze(Reminder reminder, DateTime until)
    {
        reminder.SnoozedUntil = until;
        reminder.NextFireAt = until;
    }

    public static void Dismiss(Reminder reminder)
    {
        reminder.NextFireAt = null;
        reminder.SnoozedUntil = null;
        reminder.DismissedFor = reminder.TargetAt;
    }

    /// <summary>"10분 전", "정시", "1시간 30분 전", "1일 전", "10월 30일 09:00".</summary>
    public static string Describe(Reminder reminder, DateOnly today)
    {
        if (reminder.AbsoluteAt is { } at)
        {
            return $"{TaskFormatting.FormatDate(DateOnly.FromDateTime(at), today)} {TaskFormatting.FormatTime(TimeOnly.FromDateTime(at))}";
        }

        var offset = reminder.OffsetMinutes ?? 0;
        if (offset == 0)
        {
            return "정시";
        }

        var span = TimeSpan.FromMinutes(Math.Abs(offset));
        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days}일");
        }
        if (span.Hours > 0)
        {
            parts.Add($"{span.Hours}시간");
        }
        if (span.Minutes > 0)
        {
            parts.Add($"{span.Minutes}분");
        }
        return string.Join(" ", parts) + (offset < 0 ? " 전" : " 후");
    }
}
