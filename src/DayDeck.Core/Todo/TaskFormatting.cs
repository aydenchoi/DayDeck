using System.Globalization;

namespace DayDeck.Core.Todo;

/// <summary>마감일 등을 한국어로 짧게 표시한다. 예: "오늘 17:00", "내일", "금요일", "11월 3일 (화)".</summary>
public static class TaskFormatting
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public static string FormatDate(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            0 => "오늘",
            1 => "내일",
            2 => "모레",
            -1 => "어제",
            > 2 and < 7 => date.ToString("dddd", Korean),
            _ when date.Year == today.Year => date.ToString("M월 d일 (ddd)", Korean),
            _ => date.ToString("yyyy년 M월 d일 (ddd)", Korean),
        };
    }

    public static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>목록 행에 표시할 마감 문구. 마감이 없으면 null.</summary>
    public static string? FormatDue(TaskItem item, DateTime now)
    {
        if (item.DueDate is not { } due)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(now);
        var text = FormatDate(due, today);
        if (item.DueTime is { } time)
        {
            text += " " + FormatTime(time);
        }

        if (TaskRules.IsOverdue(item, now))
        {
            var lateDays = today.DayNumber - due.DayNumber;
            return lateDays > 0 ? $"{text} 마감 · {lateDays}일 지남" : $"{text} 마감 · 지남";
        }

        return item.DueTime is null ? $"{text}까지" : text;
    }

    public static string FormatPriority(Priority priority) => priority switch
    {
        Priority.High => "!!! 높음",
        Priority.Medium => "!! 보통",
        Priority.Low => "! 낮음",
        _ => "",
    };

    public static string FormatDayHeader(DateOnly date) => date.ToString("yyyy년 M월 d일 dddd", Korean);
}
