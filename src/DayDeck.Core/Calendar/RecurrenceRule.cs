using System.Globalization;
using System.Text;

namespace DayDeck.Core.Calendar;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// 반복 규칙. RFC 5545 RRULE의 자주 쓰는 부분만 지원한다:
/// FREQ(DAILY/WEEKLY/MONTHLY/YEARLY), INTERVAL, BYDAY(주간: 요일 목록, 월간: "2TU"/"-1FR" 같은 n번째 요일),
/// BYMONTHDAY(월간 하나), COUNT, UNTIL. ICS 가져오기/내보내기와 호환되도록 RRULE 문자열로 저장한다.
/// </summary>
public sealed record RecurrenceRule
{
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public RecurrenceFrequency Frequency { get; init; }
    public int Interval { get; init; } = 1;

    /// <summary>주간 반복의 요일들. 비어 있으면 시작일의 요일.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>월간 반복의 "n번째 요일" (1~4, -1 = 마지막). <see cref="Days"/>의 첫 요일과 함께 쓴다.</summary>
    public int? WeekOfMonth { get; init; }

    /// <summary>월간 반복의 날짜. 없으면 시작일의 날짜.</summary>
    public int? MonthDay { get; init; }

    public int? Count { get; init; }

    /// <summary>이 날짜(포함)까지 반복.</summary>
    public DateOnly? Until { get; init; }

    // 요일 목록까지 값으로 비교한다 (기본 레코드 비교는 목록을 참조로 비교함).
    public bool Equals(RecurrenceRule? other) => other is not null && ToString() == other.ToString();

    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static RecurrenceRule? Parse(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }

        var parts = rrule.Trim().Replace("RRULE:", "", StringComparison.OrdinalIgnoreCase)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());

        if (!parts.TryGetValue("FREQ", out var freq))
        {
            return null;
        }

        var frequency = freq switch
        {
            "DAILY" => RecurrenceFrequency.Daily,
            "WEEKLY" => RecurrenceFrequency.Weekly,
            "MONTHLY" => RecurrenceFrequency.Monthly,
            "YEARLY" => RecurrenceFrequency.Yearly,
            _ => (RecurrenceFrequency?)null,
        };
        if (frequency is null)
        {
            return null;
        }

        var days = new List<DayOfWeek>();
        int? weekOfMonth = null;
        if (parts.TryGetValue("BYDAY", out var byDay))
        {
            foreach (var token in byDay.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var code = token[^2..];
                var index = Array.IndexOf(DayCodes, code);
                if (index < 0)
                {
                    continue;
                }
                days.Add((DayOfWeek)index);
                if (token.Length > 2 && int.TryParse(token[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                {
                    weekOfMonth = n;
                }
            }
        }

        DateOnly? until = null;
        if (parts.TryGetValue("UNTIL", out var untilText) && untilText.Length >= 8
            && DateOnly.TryParseExact(untilText[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u))
        {
            until = u;
        }

        return new RecurrenceRule
        {
            Frequency = frequency.Value,
            Interval = parts.TryGetValue("INTERVAL", out var interval) && int.TryParse(interval, out var i) && i > 0 ? i : 1,
            Days = days,
            WeekOfMonth = weekOfMonth,
            MonthDay = parts.TryGetValue("BYMONTHDAY", out var md) && int.TryParse(md, out var d) ? d : null,
            Count = parts.TryGetValue("COUNT", out var count) && int.TryParse(count, out var c) && c > 0 ? c : null,
            Until = until,
        };
    }

    public override string ToString()
    {
        var sb = new StringBuilder("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Interval > 1)
        {
            sb.Append(";INTERVAL=").Append(Interval);
        }
        if (Days.Count > 0)
        {
            var prefix = WeekOfMonth is { } n ? n.ToString(CultureInfo.InvariantCulture) : "";
            sb.Append(";BYDAY=").Append(string.Join(',', Days.Select(day => prefix + DayCodes[(int)day])));
        }
        if (MonthDay is { } monthDay)
        {
            sb.Append(";BYMONTHDAY=").Append(monthDay);
        }
        if (Count is { } count)
        {
            sb.Append(";COUNT=").Append(count);
        }
        if (Until is { } until)
        {
            sb.Append(";UNTIL=").Append(until.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("T235959");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 시작 시각 <paramref name="start"/>부터 반복되는 발생 시작 시각들. <paramref name="to"/>(제외) 이전까지,
    /// 순서대로 돌려준다. COUNT는 시작부터 센다.
    /// </summary>
    public IEnumerable<DateTime> Expand(DateTime start, DateTime to)
    {
        var produced = 0;
        var time = start.TimeOfDay;
        for (var period = 0; period < 100_000; period++)
        {
            var candidates = CandidatesForPeriod(DateOnly.FromDateTime(start), period);
            if (candidates.Count == 0)
            {
                // 그 달/해에 해당 날짜가 없다 (31일, 2월 29일, 다섯째 주). 범위를 넘었으면 끝낸다.
                var periodStart = PeriodStart(DateOnly.FromDateTime(start), period);
                if (periodStart.ToDateTime(TimeOnly.MinValue) >= to || (Until is { } u && periodStart > u))
                {
                    yield break;
                }
                continue;
            }

            foreach (var date in candidates)
            {
                var occurrence = date.ToDateTime(TimeOnly.FromTimeSpan(time));
                if (occurrence < start)
                {
                    continue;
                }
                if (occurrence >= to || (Until is { } until && date > until) || (Count is { } count && produced >= count))
                {
                    yield break;
                }

                produced++;
                yield return occurrence;
            }
        }
    }

    private DateOnly PeriodStart(DateOnly start, int period) => Frequency == RecurrenceFrequency.Yearly
        ? new DateOnly(start.Year + period * Interval, 1, 1)
        : new DateOnly(start.Year, start.Month, 1).AddMonths(period * Interval);

    private List<DateOnly> CandidatesForPeriod(DateOnly start, int period)
    {
        var step = period * Interval;
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
                return [start.AddDays(step)];

            case RecurrenceFrequency.Weekly:
            {
                // 주의 시작은 월요일 (RFC 5545 기본 WKST=MO)
                var weekStart = start.AddDays(-(((int)start.DayOfWeek + 6) % 7)).AddDays(step * 7);
                var days = Days.Count > 0 ? Days : [start.DayOfWeek];
                return [.. days.Select(d => weekStart.AddDays(((int)d + 6) % 7)).Order()];
            }

            case RecurrenceFrequency.Monthly:
            {
                var month = new DateOnly(start.Year, start.Month, 1).AddMonths(step);
                if (WeekOfMonth is { } n && Days.Count > 0)
                {
                    return NthWeekday(month, Days[0], n) is { } nth ? [nth] : [];
                }
                var day = MonthDay ?? start.Day;
                // 그 달에 없는 날짜(예: 31일)는 건너뛴다 (RFC 5545 동작)
                return day <= DateTime.DaysInMonth(month.Year, month.Month) ? [new DateOnly(month.Year, month.Month, day)] : [];
            }

            default:
            {
                var year = start.Year + step;
                return start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)
                    ? []
                    : [new DateOnly(year, start.Month, start.Day)];
            }
        }
    }

    public static DateOnly? NthWeekday(DateOnly monthStart, DayOfWeek day, int n)
    {
        if (n > 0)
        {
            var first = monthStart.AddDays(((int)day - (int)monthStart.DayOfWeek + 7) % 7);
            var date = first.AddDays((n - 1) * 7);
            return date.Month == monthStart.Month ? date : null;
        }

        var last = monthStart.AddMonths(1).AddDays(-1);
        var lastMatch = last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
        var result = lastMatch.AddDays((n + 1) * 7);
        return result.Month == monthStart.Month ? result : null;
    }

    /// <summary>"매주 월, 수", "2주마다 화요일", "매월 둘째 화요일", "매년 10월 28일 · 10회".</summary>
    public string Describe(DateOnly start)
    {
        var every = Interval > 1 ? Frequency switch
        {
            RecurrenceFrequency.Daily => $"{Interval}일마다",
            RecurrenceFrequency.Weekly => $"{Interval}주마다",
            RecurrenceFrequency.Monthly => $"{Interval}개월마다",
            _ => $"{Interval}년마다",
        } : Frequency switch
        {
            RecurrenceFrequency.Daily => "매일",
            RecurrenceFrequency.Weekly => "매주",
            RecurrenceFrequency.Monthly => "매월",
            _ => "매년",
        };

        var detail = Frequency switch
        {
            RecurrenceFrequency.Weekly => string.Join(", ", (Days.Count > 0 ? Days : [start.DayOfWeek])
                .OrderBy(d => ((int)d + 6) % 7).Select(d => Korean.DateTimeFormat.GetAbbreviatedDayName(d))),
            RecurrenceFrequency.Monthly when WeekOfMonth is { } n && Days.Count > 0 =>
                $"{OrdinalWeek(n)} {Korean.DateTimeFormat.GetDayName(Days[0])}",
            RecurrenceFrequency.Monthly => $"{MonthDay ?? start.Day}일",
            RecurrenceFrequency.Yearly => $"{start.Month}월 {start.Day}일",
            _ => "",
        };

        var text = detail.Length > 0 ? $"{every} {detail}" : every;
        if (Count is { } count)
        {
            text += $" · {count}회";
        }
        else if (Until is { } until)
        {
            text += $" · {until:yyyy-MM-dd}까지";
        }
        return text;
    }

    private static string OrdinalWeek(int n) => n switch
    {
        1 => "첫째",
        2 => "둘째",
        3 => "셋째",
        4 => "넷째",
        5 => "다섯째",
        -1 => "마지막",
        _ => $"{n}번째",
    };
}
