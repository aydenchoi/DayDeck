using DayDeck.Core.Todo;

namespace DayDeck.Core.Calendar;

/// <summary>
/// 달력 일정 (items 테이블, kind = Event). 종일 일정은 <see cref="Start"/>/<see cref="End"/>가 날짜(00:00)이고
/// End는 마지막 날을 포함한다. 시간 일정은 정확한 시작/끝 시각이다.
/// </summary>
public sealed class CalendarEvent
{
    public long Id { get; set; }
    public long? ListId { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public string? Location { get; set; }
    public string? Color { get; set; }
    public bool IsAllDay { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>RFC 5545 RRULE. null이면 반복하지 않는다.</summary>
    public string? RRule { get; set; }

    public ItemStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // 조회 시 채움
    public string? ListName { get; set; }
    public string? ListColor { get; set; }

    public RecurrenceRule? Rule => RecurrenceRule.Parse(RRule);
    public bool IsRecurring => RRule is not null;

    /// <summary>종일 일정은 날 수 - 1일, 시간 일정은 끝 - 시작.</summary>
    public TimeSpan Duration => End - Start;

    public CalendarEvent Clone() => (CalendarEvent)MemberwiseClone();
}

/// <summary>달력에 그려지는 일정 한 번 (반복 일정이면 그 회차).</summary>
public sealed record EventOccurrence(
    CalendarEvent Event,
    DateTime Start,
    DateTime End,
    DateTime OriginalStart,
    string Title,
    bool IsCompleted,
    bool IsException)
{
    public bool IsAllDay => Event.IsAllDay;
    public bool IsRecurring => Event.IsRecurring;
    public DateOnly FirstDay => DateOnly.FromDateTime(Start);

    /// <summary>마지막으로 걸치는 날. 시간 일정이 자정에 끝나면 그 전날까지.</summary>
    public DateOnly LastDay => IsAllDay
        ? DateOnly.FromDateTime(End)
        : DateOnly.FromDateTime(End > Start ? End.AddTicks(-1) : End);

    public bool OccursOn(DateOnly day) => day >= FirstDay && day <= LastDay;

    public bool IsMultiDay => LastDay > FirstDay;

    /// <summary>지난 시간 일정인데 완료하지 않았는지 (달력에서 빗금 표시).</summary>
    public bool IsPastAndOpen(DateTime now) => !IsCompleted && (IsAllDay ? LastDay < DateOnly.FromDateTime(now) : End <= now);
}
