using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.Core.QuickAdd;

public enum QuickAddKind
{
    Task,
    Event,
}

public enum QuickAddTokenKind
{
    Date,
    Time,
    Recurrence,
    List,
    Priority,
    Reminder,
    Tag,
}

/// <summary>입력에서 인식한 부분 (입력창에서 색으로 강조).</summary>
public sealed record QuickAddToken(int Start, int Length, QuickAddTokenKind Kind);

/// <summary>빠른 입력 해석 결과.</summary>
public sealed record QuickAddResult(
    QuickAddKind Kind,
    string Title,
    DateOnly? Date,
    TimeOnly? Time,
    DateOnly? EndDate,
    TimeOnly? EndTime,
    RecurrenceRule? Rule,
    string? ListName,
    Priority Priority,
    IReadOnlyList<int> ReminderOffsets,
    IReadOnlyList<QuickAddToken> Tokens,
    IReadOnlyList<string>? Tags = null)
{
    public bool IsAllDayEvent => Kind == QuickAddKind.Event && Time is null;

    /// <summary>테스트와 미리보기용 한 줄 요약. 예: "E|치과|2026-10-29 15:00|2026-10-29 16:00||개인|High|-10".</summary>
    public string Describe()
    {
        static string Point(DateOnly? d, TimeOnly? t) =>
            d is null ? "" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (t is { } time ? " " + time.ToString("HH:mm", CultureInfo.InvariantCulture) : "");
        return string.Join('|',
            Kind == QuickAddKind.Event ? "E" : "T",
            Title,
            Point(Date, Time),
            Point(EndDate, EndTime),
            Rule?.ToString() ?? "",
            ListName ?? "",
            Priority == Priority.None ? "" : Priority.ToString(),
            string.Join(',', ReminderOffsets));
    }
}

/// <summary>
/// 한국어 자연어 빠른 입력 해석기 (설계 §5.7). 예:
/// <list type="bullet">
/// <item>"내일 오후 3시 치과 #개인 !높음" → 일정, 내일 15:00~16:00, 목록 개인, 우선순위 높음</item>
/// <item>"금요일까지 보고서 제출" → 할 일, 마감 이번 주 금요일</item>
/// <item>"매주 월요일 9시 주간회의 10분전 알림" → 반복 일정 + 10분 전 알림</item>
/// <item>"11/3~11/5 제주 여행" → 3일짜리 종일 일정</item>
/// </list>
/// 규칙: "까지"가 붙으면 마감(할 일). 시간·기간·반복이 있으면 일정. 날짜만 있으면 그날 마감인 할 일.
/// 오전/오후 없이 1~6시는 오후로 본다.
/// </summary>
public sealed partial class QuickAddParser
{
    private const string Dow = "[월화수목금토일]";
    private const string Suffix = @"(?<suffix>까지|부터|에는|에|중에?)?";
    private const string AmPm = @"(?:(?<ampm>오전|오후|아침|낮|저녁|밤|새벽|am|pm|AM|PM)\s*)?";
    private const string KoreanHour = "열한|열두|한|두|세|네|다섯|여섯|일곱|여덟|아홉|열";

    private readonly DateTime _now;

    public QuickAddParser(DateTime now) => _now = now;

    private DateOnly Today => DateOnly.FromDateTime(_now);

    public QuickAddResult Parse(string input)
    {
        var text = new StringBuilder(input);
        var tokens = new List<QuickAddToken>();

        // 이미 쓴 부분은 공백으로 지워 다음 규칙이 다시 잡지 않게 한다.
        // at을 주면 그 위치(공백 건너뜀)에서 바로 시작하는 것만 받는다 (기간의 두 번째 날짜/시간).
        Match? Take(Regex regex, QuickAddTokenKind kind, int? at = null)
        {
            var current = text.ToString();
            var match = regex.Match(current, Math.Min(at ?? 0, current.Length));
            if (!match.Success || (at is { } start && current[start..match.Index].Trim().Length > 0))
            {
                return null;
            }
            tokens.Add(new QuickAddToken(match.Index, match.Length, kind));
            for (var i = match.Index; i < match.Index + match.Length; i++)
            {
                text[i] = ' ';
            }
            return match;
        }

        // 목록 #태그, 우선순위
        string? list = null;
        if (Take(ListTag(), QuickAddTokenKind.List) is { } listMatch)
        {
            list = listMatch.Groups["name"].Value;
        }
        // 태그 @이름 (여러 개)
        var tags = new List<string>();
        while (Take(TagMark(), QuickAddTokenKind.Tag) is { } tagMatch)
        {
            tags.Add(tagMatch.Groups["name"].Value);
        }
        var priority = Priority.None;
        if (Take(PriorityMark(), QuickAddTokenKind.Priority) is { } priorityMatch)
        {
            priority = ParsePriority(priorityMatch.Value);
        }

        // 알림 ("10분 전 알림", "하루 전 알림", "정시 알림")
        var reminders = new List<int>();
        while (Take(ReminderPhrase(), QuickAddTokenKind.Reminder) is { } reminder)
        {
            reminders.Add(ParseReminder(reminder));
        }

        // 반복
        RecurrenceRule? rule = null;
        DateOnly? ruleStart = null;
        if (Take(Recurrence(), QuickAddTokenKind.Recurrence) is { } recurrence)
        {
            (rule, ruleStart) = ParseRecurrence(recurrence);
        }

        // 날짜 (범위 먼저)
        DateOnly? date = null;
        DateOnly? endDate = null;
        var deadline = false;
        if (Take(DateRangeShort(), QuickAddTokenKind.Date) is { } shortRange)
        {
            date = MonthDay(int.Parse(shortRange.Groups["m"].Value), int.Parse(shortRange.Groups["d"].Value));
            endDate = SafeDate(date.Value.Year, date.Value.Month, int.Parse(shortRange.Groups["d2"].Value));
            if (endDate < date)
            {
                endDate = endDate.Value.AddMonths(1);
            }
        }
        else if (TakeDate(Take, null) is { } first)
        {
            date = first.Date;
            deadline = first.Deadline;
            // 바로 이어지는 두 번째 날짜 → 기간
            var next = first.End;
            if (Take(RangeSeparator(), QuickAddTokenKind.Date, next) is { } separator)
            {
                next = separator.Index + separator.Length;
            }
            if (next != first.End || first.Suffix == "부터")
            {
                if (TakeDate(Take, next) is { } second)
                {
                    endDate = second.Date;
                    while (endDate < first.Date)
                    {
                        // "월요일~수요일"처럼 끝이 앞서면 다음 주로
                        endDate = endDate.Value.AddDays(7);
                    }
                    deadline = false;
                }
            }
        }

        // 시간 (범위 포함)
        TimeOnly? time = null;
        TimeOnly? endTime = null;
        if (TakeTime(Take, null, null) is { } startTime)
        {
            time = startTime.Time;
            deadline |= startTime.Deadline;
            var next = startTime.End;
            if (Take(RangeSeparator(), QuickAddTokenKind.Time, next) is { } separator)
            {
                next = separator.Index + separator.Length;
            }
            if (next != startTime.End || startTime.Suffix == "부터")
            {
                if (TakeTime(Take, startTime.Time, next) is { } end)
                {
                    endTime = end.Time;
                    deadline = false;
                }
            }
        }

        // 기간 ("2시간 동안", "30분 동안", "1시간 반")
        TimeSpan? duration = null;
        if (time is not null && endTime is null && Take(Duration(), QuickAddTokenKind.Time) is { } durationMatch)
        {
            duration = ParseDuration(durationMatch);
        }

        // 남은 "까지" (예: "보고서 제출 금요일 까지", 반복이면 "매주 금요일까지 주간보고" → 반복 할 일)
        if ((date is not null || rule is not null) && Take(DeadlineWord(), QuickAddTokenKind.Date) is not null)
        {
            deadline = true;
        }

        var title = CleanTitle(text.ToString());
        if (title.Length == 0)
        {
            title = input.Trim();
        }

        // 종류 정하기
        var isEvent = !deadline && (rule is not null || endDate is not null || time is not null);
        if (rule is not null)
        {
            date ??= ruleStart ?? Today;
            if (rule.Frequency == RecurrenceFrequency.Yearly && ruleStart is null && date is { } yearly)
            {
                date = yearly;
            }
        }
        if (isEvent && date is null)
        {
            date = Today;
        }

        if (isEvent && time is { } t)
        {
            var start = date!.Value.ToDateTime(t);
            DateTime end;
            if (endTime is { } et)
            {
                end = (endDate ?? date.Value).ToDateTime(et);
                if (end <= start)
                {
                    end = end.AddDays(1);
                }
            }
            else
            {
                end = start + (duration ?? TimeSpan.FromHours(1));
            }
            endDate = DateOnly.FromDateTime(end);
            endTime = TimeOnly.FromDateTime(end);
        }
        else if (isEvent)
        {
            endDate ??= date;
        }

        return new QuickAddResult(
            isEvent ? QuickAddKind.Event : QuickAddKind.Task,
            title, date, time,
            isEvent ? endDate : null,
            isEvent ? endTime : null,
            rule, list, priority, reminders,
            [.. tokens.OrderBy(t => t.Start)],
            tags);
    }

    // ---------- 날짜 ----------

    private sealed record DateHit(DateOnly Date, string Suffix, int End)
    {
        public bool Deadline => Suffix == "까지";
    }

    private DateHit? TakeDate(Func<Regex, QuickAddTokenKind, int?, Match?> take, int? at)
    {
        foreach (var (regex, parse) in DateRules())
        {
            if (take(regex, QuickAddTokenKind.Date, at) is { } match && parse(match) is { } date)
            {
                return new DateHit(date, match.Groups["suffix"].Value, match.Index + match.Length);
            }
        }
        return null;
    }

    private IEnumerable<(Regex, Func<Match, DateOnly?>)> DateRules()
    {
        yield return (IsoDate(), m => SafeDate(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value)));
        yield return (KoreanMonthDay(), m => MonthDay(int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value)));
        yield return (SlashMonthDay(), m => MonthDay(int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["d"].Value)));
        yield return (RelativeMonthDay(), m =>
        {
            var month = new DateOnly(Today.Year, Today.Month, 1).AddMonths(m.Groups["which"].Value.StartsWith('다') ? 1 : 0);
            return SafeDate(month.Year, month.Month, int.Parse(m.Groups["d"].Value));
        });
        yield return (RelativeOffset(), m =>
        {
            var n = int.Parse(m.Groups["n"].Value);
            return m.Groups["unit"].Value switch
            {
                "일" => Today.AddDays(n),
                "주" => Today.AddDays(7 * n),
                _ => Today.AddMonths(n),
            };
        });
        yield return (Weekday(), m => WeekdayDate(m.Groups["week"].Value, m.Groups["dow"].Value));
        yield return (Weekend(), m =>
        {
            var saturday = Today.AddDays(((int)DayOfWeek.Saturday - (int)Today.DayOfWeek + 7) % 7);
            return m.Groups["next"].Success ? saturday.AddDays(7) : saturday;
        });
        yield return (RelativeDay(), m => m.Groups["w"].Value switch
        {
            "오늘" => Today,
            "내일" or "낼" => Today.AddDays(1),
            "모레" => Today.AddDays(2),
            "글피" => Today.AddDays(3),
            _ => (DateOnly?)null,
        });
        yield return (NextPeriod(), m => m.Groups["p"].Value switch
        {
            var p when p.EndsWith('주') => StartOfWeek(Today).AddDays(7),
            "월말" => new DateOnly(Today.Year, Today.Month, 1).AddMonths(1).AddDays(-1),
            "연말" => new DateOnly(Today.Year, 12, 31),
            _ => new DateOnly(Today.Year, Today.Month, 1).AddMonths(1),
        });
        yield return (DayOfMonth(), m =>
        {
            var day = int.Parse(m.Groups["d"].Value);
            var candidate = SafeDate(Today.Year, Today.Month, day);
            if (candidate is { } c && c < Today)
            {
                var next = new DateOnly(Today.Year, Today.Month, 1).AddMonths(1);
                candidate = SafeDate(next.Year, next.Month, day);
            }
            return candidate;
        });
    }

    /// <summary>이번 해의 M월 D일. 이미 지났으면(3개월 이상 전) 내년.</summary>
    private DateOnly MonthDay(int month, int day)
    {
        var date = SafeDate(Today.Year, month, day) ?? Today;
        return date < Today.AddMonths(-3) ? SafeDate(Today.Year + 1, month, day) ?? date : date;
    }

    private static DateOnly? SafeDate(int year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>"금요일" = 오늘 포함 다가오는 금요일, "이번 주 월요일" = 이번 주(월~일)의 월요일, "다음 주 금요일" = 다음 주의 금요일.</summary>
    private DateOnly WeekdayDate(string week, string dow)
    {
        var day = ToDayOfWeek(dow);
        var offsetInWeek = ((int)day + 6) % 7;
        week = week.Replace(" ", "");
        return week switch
        {
            "이번주" => StartOfWeek(Today).AddDays(offsetInWeek),
            "다음주" or "담주" => StartOfWeek(Today).AddDays(7 + offsetInWeek),
            "다다음주" => StartOfWeek(Today).AddDays(14 + offsetInWeek),
            _ => Today.AddDays(((int)day - (int)Today.DayOfWeek + 7) % 7),
        };
    }

    private static DayOfWeek ToDayOfWeek(string text) => text[0] switch
    {
        '월' => DayOfWeek.Monday,
        '화' => DayOfWeek.Tuesday,
        '수' => DayOfWeek.Wednesday,
        '목' => DayOfWeek.Thursday,
        '금' => DayOfWeek.Friday,
        '토' => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    // ---------- 시간 ----------

    private sealed record TimeHit(TimeOnly Time, string Suffix, int End)
    {
        public bool Deadline => Suffix == "까지";
    }

    private static TimeHit? TakeTime(Func<Regex, QuickAddTokenKind, int?, Match?> take, TimeOnly? rangeStart, int? at)
    {
        foreach (var regex in new[] { KoreanTime(), NativeKoreanTime(), ClockTime(), EnglishTime(), NoonMidnight() })
        {
            if (take(regex, QuickAddTokenKind.Time, at) is not { } match)
            {
                continue;
            }

            var ampm = match.Groups["ampm"].Value;
            int hour;
            int minute;
            if (regex == NoonMidnight())
            {
                hour = match.Value.Contains("정오") ? 12 : 0;
                minute = 0;
            }
            else
            {
                hour = match.Groups["h"].Value is var h && int.TryParse(h, out var numeric) ? numeric : NativeHour(match.Groups["h"].Value);
                minute = match.Groups["half"].Success ? 30 : int.TryParse(match.Groups["min"].Value, out var m) ? m : 0;
            }

            hour = ApplyAmPm(hour, ampm, explicitClock: regex == ClockTime(), rangeStart);
            if (hour is < 0 or > 23 || minute is < 0 or > 59)
            {
                continue;
            }
            return new TimeHit(new TimeOnly(hour, minute), match.Groups["suffix"].Value, match.Index + match.Length);
        }
        return null;
    }

    private static int ApplyAmPm(int hour, string ampm, bool explicitClock, TimeOnly? rangeStart)
    {
        switch (ampm.ToLowerInvariant())
        {
            case "오후" or "저녁" or "pm":
                return hour < 12 ? hour + 12 : hour;
            case "밤":
                return hour == 12 ? 0 : hour < 12 && hour >= 6 ? hour + 12 : hour;
            case "낮":
                return hour < 7 ? hour + 12 : hour;
            case "오전" or "아침" or "새벽" or "am":
                return hour == 12 ? 0 : hour;
        }

        if (rangeStart is { } start)
        {
            // "오후 3시~5시" → 5시도 오후
            if (hour < 12 && hour + 12 > start.Hour && hour <= start.Hour)
            {
                return hour + 12;
            }
            return hour;
        }

        // 오전/오후가 없으면 1~6시는 오후로 본다 (일정 입력에서 새벽은 드물다). 14:00처럼 쓴 시각은 그대로.
        return !explicitClock && hour is >= 1 and <= 6 ? hour + 12 : hour;
    }

    private static int NativeHour(string word) => word switch
    {
        "한" => 1, "두" => 2, "세" => 3, "네" => 4, "다섯" => 5, "여섯" => 6,
        "일곱" => 7, "여덟" => 8, "아홉" => 9, "열" => 10, "열한" => 11, "열두" => 12,
        _ => -1,
    };

    private static TimeSpan ParseDuration(Match match)
    {
        var hours = int.TryParse(match.Groups["h"].Value, out var h) ? h : 0;
        var minutes = match.Groups["half"].Success ? 30 : int.TryParse(match.Groups["m"].Value, out var m) ? m : 0;
        return new TimeSpan(hours, minutes, 0);
    }

    // ---------- 반복, 알림, 우선순위 ----------

    private (RecurrenceRule Rule, DateOnly? Start) ParseRecurrence(Match match)
    {
        var value = match.Value.Replace(" ", "");
        int? interval = match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value) : null;

        if (value.StartsWith("매일") || match.Groups["unit"].Value == "일")
        {
            return (new RecurrenceRule { Frequency = RecurrenceFrequency.Daily, Interval = interval ?? 1 }, null);
        }
        if (value.Contains("평일") || value.Contains("주중"))
        {
            List<DayOfWeek> weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
            return (Weekly(weekdays, 1), FirstOf(weekdays));
        }
        if (value.Contains("주말"))
        {
            List<DayOfWeek> weekend = [DayOfWeek.Saturday, DayOfWeek.Sunday];
            return (Weekly(weekend, 1), FirstOf(weekend));
        }
        if (match.Groups["monthly"].Success || match.Groups["unit"].Value is "개월" or "달")
        {
            var monthInterval = interval ?? 1;
            if (match.Groups["nth"].Success)
            {
                var n = match.Groups["nth"].Value switch
                {
                    "첫째" or "첫번째" or "첫" => 1,
                    "둘째" or "두번째" => 2,
                    "셋째" or "세번째" => 3,
                    "넷째" or "네번째" => 4,
                    _ => -1,
                };
                var day = ToDayOfWeek(match.Groups["mdow"].Value);
                var rule = new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = monthInterval, Days = [day], WeekOfMonth = n };
                var thisMonth = RecurrenceRule.NthWeekday(new DateOnly(Today.Year, Today.Month, 1), day, n);
                var start = thisMonth is { } t && t >= Today ? t : RecurrenceRule.NthWeekday(new DateOnly(Today.Year, Today.Month, 1).AddMonths(1), day, n);
                return (rule, start);
            }
            if (match.Groups["mday"].Success)
            {
                var day = int.Parse(match.Groups["mday"].Value);
                var start = SafeDate(Today.Year, Today.Month, day) is { } d && d >= Today
                    ? d
                    : SafeDate(Today.AddMonths(1).Year, Today.AddMonths(1).Month, day);
                return (new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = monthInterval }, start);
            }
            return (new RecurrenceRule { Frequency = RecurrenceFrequency.Monthly, Interval = monthInterval }, null);
        }
        if (value.StartsWith("매년") || match.Groups["unit"].Value == "년")
        {
            return (new RecurrenceRule { Frequency = RecurrenceFrequency.Yearly, Interval = interval ?? 1 }, null);
        }

        // 주간: 매주 / 격주 / n주마다 (+ 요일들)
        var weeks = value.StartsWith("격주") ? 2 : interval ?? 1;
        var days = match.Groups["days"].Success
            ? match.Groups["days"].Value.Where(c => "월화수목금토일".Contains(c)).Select(c => ToDayOfWeek(c.ToString())).Distinct().ToList()
            : [];
        return (Weekly(days, weeks), days.Count > 0 ? FirstOf(days) : null);
    }

    /// <summary>오늘부터 가장 먼저 오는 해당 요일.</summary>
    private DateOnly FirstOf(IEnumerable<DayOfWeek> days) => days.Select(d => Today.AddDays(((int)d - (int)Today.DayOfWeek + 7) % 7)).Min();

    private static RecurrenceRule Weekly(List<DayOfWeek> days, int interval) =>
        new() { Frequency = RecurrenceFrequency.Weekly, Interval = interval, Days = [.. days.OrderBy(d => ((int)d + 6) % 7)] };

    private static int ParseReminder(Match match)
    {
        if (match.Value.Contains("정시"))
        {
            return 0;
        }
        if (match.Value.Contains("하루"))
        {
            return -1440;
        }
        var n = int.Parse(match.Groups["n"].Value);
        return match.Groups["unit"].Value switch
        {
            "시간" => -60 * n,
            "일" => -1440 * n,
            _ => -n,
        };
    }

    private static Priority ParsePriority(string mark) => mark switch
    {
        "!!!" or "!높음" or "!중요" or "!긴급" or "!3" => Priority.High,
        "!!" or "!보통" or "!2" => Priority.Medium,
        _ => Priority.Low,
    };

    private static string CleanTitle(string text)
    {
        var collapsed = Whitespace().Replace(text, " ").Trim();
        // 인식한 부분 사이에 남은 조사/기호 정리
        collapsed = EdgeParticles().Replace(collapsed, "").Trim();
        return Whitespace().Replace(collapsed, " ").Trim(' ', ',', '-', '~', '·');
    }

    // ---------- 정규식 ----------

    [GeneratedRegex(@"#(?<name>[^\s#!]+)")]
    private static partial Regex ListTag();

    // 메일 주소(a@b.com)는 태그가 아니다: 앞이 공백이거나 줄 처음일 때만.
    [GeneratedRegex(@"(?<=^|\s)@(?<name>[^\s#!@]+)")]
    private static partial Regex TagMark();

    [GeneratedRegex(@"(?<=^|\s)!(?<p>!!|!|높음|중요|긴급|보통|낮음|[123])?(?=\s|$)")]
    private static partial Regex PriorityMark();

    [GeneratedRegex(@"(?:(?<n>\d+)\s*(?<unit>분|시간|일)\s*전\s*에?\s*알림|알림\s*(?<n>\d+)\s*(?<unit>분|시간|일)\s*전|하루\s*전\s*에?\s*알림|정시\s*에?\s*알림|알림\s*하루\s*전)")]
    private static partial Regex ReminderPhrase();

    [GeneratedRegex(@"(?:매일|평일마다|매\s*평일|평일|주중|주말마다|매주\s*주말|매\s*주말|(?<monthly>매월|매달)(?:\s*(?:(?<mday>\d{1,2})\s*일|(?<nth>첫째|첫번째|첫|둘째|두번째|셋째|세번째|넷째|네번째|마지막)\s*(?<mdow>[월화수목금토일])요일))?|매년|(?<n>\d+)\s*(?<unit>일|주|개월|달|년)\s*마다(?:\s*(?<days>[월화수목금토일](?:\s*[,·]?\s*[월화수목금토일])*)\s*(?:요일)?(?![가-힣]))?|(?:매주|격주)(?:\s*(?<days>[월화수목금토일](?:\s*[,·]?\s*[월화수목금토일])*)\s*(?:요일)?(?![가-힣]))?)\s*(?:마다)?")]
    private static partial Regex Recurrence();

    [GeneratedRegex(@"(?<m>\d{1,2})/(?<d>\d{1,2})\s*[~\-]\s*(?<d2>\d{1,2})(?![\d/:시일])")]
    private static partial Regex DateRangeShort();

    [GeneratedRegex(@"\s*(?:~|–|-)\s*")]
    private static partial Regex RangeSeparator();

    [GeneratedRegex(@"(?<y>\d{4})[-./](?<m>\d{1,2})[-./](?<d>\d{1,2})" + Suffix)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일" + Suffix)]
    private static partial Regex KoreanMonthDay();

    [GeneratedRegex(@"(?<![\d:])(?<m>\d{1,2})/(?<d>\d{1,2})(?![\d:])" + Suffix)]
    private static partial Regex SlashMonthDay();

    [GeneratedRegex(@"(?<which>이번|다음)\s*달\s*(?<d>\d{1,2})\s*일" + Suffix)]
    private static partial Regex RelativeMonthDay();

    [GeneratedRegex(@"(?<n>\d+)\s*(?<unit>일|주|개월|달)\s*(?:후|뒤)" + Suffix)]
    private static partial Regex RelativeOffset();

    [GeneratedRegex(@"(?<week>(?:이번|다음|다다음)\s*주|담주)?\s*(?<dow>[월화수목금토일])(?:요일|욜)" + Suffix)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"(?<next>다음\s*)?(?:이번\s*)?주말" + Suffix)]
    private static partial Regex Weekend();

    [GeneratedRegex(@"(?<w>오늘|내일|낼|모레|글피)" + Suffix)]
    private static partial Regex RelativeDay();

    [GeneratedRegex(@"(?<p>다음\s*주|담주|다음\s*달|월말|연말)" + Suffix)]
    private static partial Regex NextPeriod();

    [GeneratedRegex(@"(?<![\d월/])(?<d>\d{1,2})\s*일(?!\s*(?:후|뒤|동안|마다|간|째))" + Suffix + "(?![가-힣])")]
    private static partial Regex DayOfMonth();

    [GeneratedRegex(AmPm + @"(?<h>\d{1,2})\s*시(?!간)\s*(?:(?<half>반)|(?<min>\d{1,2})\s*분(?!\s*(?:전|동안|후)))?" + Suffix)]
    private static partial Regex KoreanTime();

    [GeneratedRegex(AmPm + "(?<h>" + KoreanHour + @")\s*시(?!간)\s*(?<half>반)?" + Suffix)]
    private static partial Regex NativeKoreanTime();

    [GeneratedRegex(AmPm + @"(?<![\d/])(?<h>\d{1,2}):(?<min>\d{2})(?!\d)" + Suffix)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"(?<h>\d{1,2})\s*(?<ampm>am|pm|AM|PM)\b" + Suffix)]
    private static partial Regex EnglishTime();

    [GeneratedRegex(@"(?:정오|자정)" + Suffix)]
    private static partial Regex NoonMidnight();

    [GeneratedRegex(@"(?:(?<h>\d+)\s*시간\s*(?:(?<half>반)|(?<m>\d+)\s*분)?|(?<m>\d+)\s*분)\s*(?:동안)?")]
    private static partial Regex Duration();

    [GeneratedRegex(@"(?<=\s|^)까지(?=\s|$)")]
    private static partial Regex DeadlineWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:에서|에는|에|까지|부터)\s+|\s+(?:에서|에는|에|까지|부터)$")]
    private static partial Regex EdgeParticles();
}
