namespace DayDeck.Core.Todo;

public enum ItemKind
{
    Todo = 0,
    Event = 1,
}

public enum ItemStatus
{
    Open = 0,
    Completed = 1,
    Cancelled = 2,
}

public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>할 일 한 건 (items 테이블). 일정(Event)은 M4에서 같은 테이블을 쓴다.</summary>
public sealed class TaskItem
{
    public long Id { get; set; }
    public ItemKind Kind { get; set; } = ItemKind.Todo;
    public long? ListId { get; set; }
    public long? ParentId { get; set; }
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public Priority Priority { get; set; }

    /// <summary>마감일. 시간이 없으면 그날 하루가 끝날 때까지가 기한이다.</summary>
    public DateOnly? DueDate { get; set; }
    public TimeOnly? DueTime { get; set; }

    /// <summary>"오늘 할 일"에 직접 추가한 날짜. 그날의 오늘 화면에만 나온다.</summary>
    public DateOnly? MyDay { get; set; }

    public ItemStatus Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>반복 규칙 (RFC 5545 RRULE). 완료하면 다음 회차가 새 할 일로 생긴다.</summary>
    public string? RRule { get; set; }

    /// <summary>태그 이름들.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    public Calendar.RecurrenceRule? Rule => Calendar.RecurrenceRule.Parse(RRule);
    public bool IsRecurring => RRule is not null;

    // 조회할 때 함께 채우는 값 (저장하지 않음)
    public string? ListName { get; set; }
    public string? ListColor { get; set; }
    public int SubtaskCount { get; set; }
    public int SubtaskDoneCount { get; set; }

    /// <summary>다음에 울릴 알림 시각 (조회 시 채움).</summary>
    public DateTime? NextReminderAt { get; set; }

    public bool IsDone => Status != ItemStatus.Open;
}

/// <summary>할 일 목록 (lists 테이블).</summary>
public sealed class TodoList
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public int SortOrder { get; set; }

    /// <summary>조회 시 채우는 미완료 개수.</summary>
    public int OpenCount { get; set; }
}
