namespace DayDeck.Core.Todo;

/// <summary>화면에서 구분해서 보여 줄 상태 (설계 문서 §5.2).</summary>
public enum TaskDisplayState
{
    Open,
    Overdue,
    Completed,
    Cancelled,
}

/// <summary>
/// 할 일의 상태와 스마트 목록 포함 여부를 정하는 규칙. 화면과 저장소가 같은 규칙을 쓰도록 한곳에 둔다.
/// </summary>
public static class TaskRules
{
    public const int UpcomingDays = 7;

    public static TaskDisplayState GetState(TaskItem item, DateTime now) => item.Status switch
    {
        ItemStatus.Completed => TaskDisplayState.Completed,
        ItemStatus.Cancelled => TaskDisplayState.Cancelled,
        _ => IsOverdue(item, now) ? TaskDisplayState.Overdue : TaskDisplayState.Open,
    };

    /// <summary>
    /// 미완료이면서 기한이 지났는지. 시간이 없는 마감은 그날이 끝나야 지난 것으로 본다.
    /// </summary>
    public static bool IsOverdue(TaskItem item, DateTime now)
    {
        if (item.Status != ItemStatus.Open || item.DueDate is not { } due)
        {
            return false;
        }

        return item.DueTime is { } time
            ? now > due.ToDateTime(time)
            : DateOnly.FromDateTime(now) > due;
    }

    /// <summary>"오늘" 화면에 나오는지: 오늘까지 마감이거나 오늘 할 일에 추가한 항목. 완료 항목은 오늘 완료한 것만.</summary>
    public static bool IsInToday(TaskItem item, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var relevant = item.MyDay == today || (item.DueDate is { } due && due <= today);
        if (!relevant)
        {
            return false;
        }

        return item.Status == ItemStatus.Open
               || (item.CompletedAt is { } done && DateOnly.FromDateTime(done) == today);
    }

    /// <summary>"예정" 화면: 오늘부터 <paramref name="days"/>일(기본 7일) 안에 마감인 미완료 항목 (기한 지난 항목 제외).</summary>
    public static bool IsUpcoming(TaskItem item, DateTime now, int days = UpcomingDays)
    {
        if (item.Status != ItemStatus.Open || item.DueDate is not { } due || IsOverdue(item, now))
        {
            return false;
        }

        var today = DateOnly.FromDateTime(now);
        return due >= today && due <= today.AddDays(days);
    }

    /// <summary>
    /// 반복 할 일의 다음 마감일: <paramref name="due"/> 다음 회차. 그것도 이미 지났으면 오늘 이후 첫 회차로 건너뛴다
    /// (밀린 회차를 하나씩 다 만들지 않는다). 규칙이 끝났으면 null. COUNT는 호출자가 회차마다 줄인다.
    /// </summary>
    public static DateOnly? NextDue(Calendar.RecurrenceRule rule, DateOnly due, DateOnly today)
    {
        var open = rule with { Count = null };
        var start = due.ToDateTime(TimeOnly.MinValue);
        foreach (var occurrence in open.Expand(start, start.AddYears(20)))
        {
            var day = DateOnly.FromDateTime(occurrence);
            if (day > due && day >= today)
            {
                return day;
            }
        }
        return null;
    }

    /// <summary>직접 정렬: 사용자가 끌어다 놓은 순서 (sort_order) 그대로.</summary>
    public static IOrderedEnumerable<TaskItem> OrderManual(IEnumerable<TaskItem> items) =>
        items.OrderBy(i => i.SortOrder).ThenBy(i => i.Id);

    /// <summary>미완료 항목 정렬: 기한 지남 → 마감 빠른 순(없으면 뒤) → 우선순위 높은 순 → 사용자 순서 → 만든 순.</summary>
    public static IOrderedEnumerable<TaskItem> OrderOpen(IEnumerable<TaskItem> items, DateTime now) =>
        items.OrderByDescending(i => IsOverdue(i, now))
             .ThenBy(i => i.DueDate is null)
             .ThenBy(i => i.DueDate)
             .ThenBy(i => i.DueTime ?? TimeOnly.MaxValue)
             .ThenByDescending(i => i.Priority)
             .ThenBy(i => i.SortOrder)
             .ThenBy(i => i.Id);

    public static IOrderedEnumerable<TaskItem> OrderDone(IEnumerable<TaskItem> items) =>
        items.OrderByDescending(i => i.CompletedAt).ThenByDescending(i => i.Id);
}
