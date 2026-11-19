namespace DayDeck.Core.Calendar;

public sealed record Holiday(DateOnly Date, string Name, bool IsSubstitute = false);

/// <summary>
/// 대한민국 법정 공휴일 (관공서의 공휴일에 관한 규정). 선거일, 임시공휴일은 포함하지 않는다.
/// 대체공휴일 규칙 (2023년 5월 이후 기준):
/// <list type="bullet">
/// <item>설날·추석 연휴: 연휴 중 일요일이 있거나 다른 공휴일과 겹치면, 연휴 다음 첫 비공휴일</item>
/// <item>삼일절·어린이날·부처님오신날·광복절·개천절·한글날·성탄절: 토·일요일이거나 다른 공휴일과 겹치면, 다음 첫 비공휴일 평일</item>
/// <item>신정·현충일: 대체공휴일 없음</item>
/// </list>
/// </summary>
public static class KoreanHolidays
{
    private static readonly Dictionary<int, IReadOnlyList<Holiday>> Cache = [];
    private static readonly Lock CacheLock = new();

    public static IReadOnlyList<Holiday> ForYear(int year)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(year, out var holidays))
            {
                holidays = Build(year);
                Cache[year] = holidays;
            }
            return holidays;
        }
    }

    public static IReadOnlyList<Holiday> Between(DateOnly from, DateOnly to)
    {
        var result = new List<Holiday>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            result.AddRange(ForYear(year).Where(h => h.Date >= from && h.Date <= to));
        }
        return result;
    }

    private static List<Holiday> Build(int year)
    {
        var list = new List<Holiday>
        {
            new(new DateOnly(year, 1, 1), "신정"),
            new(new DateOnly(year, 3, 1), "삼일절"),
            new(new DateOnly(year, 5, 5), "어린이날"),
            new(new DateOnly(year, 6, 6), "현충일"),
            new(new DateOnly(year, 8, 15), "광복절"),
            new(new DateOnly(year, 10, 3), "개천절"),
            new(new DateOnly(year, 10, 9), "한글날"),
            new(new DateOnly(year, 12, 25), "성탄절"),
        };

        var seollal = LunarCalendar.Seollal(year);
        var chuseok = LunarCalendar.Chuseok(year);
        list.Add(new Holiday(seollal.AddDays(-1), "설날 연휴"));
        list.Add(new Holiday(seollal, "설날"));
        list.Add(new Holiday(seollal.AddDays(1), "설날 연휴"));
        list.Add(new Holiday(LunarCalendar.BuddhasBirthday(year), "부처님오신날"));
        list.Add(new Holiday(chuseok.AddDays(-1), "추석 연휴"));
        list.Add(new Holiday(chuseok, "추석"));
        list.Add(new Holiday(chuseok.AddDays(1), "추석 연휴"));

        if (year >= 2023)
        {
            AddSubstitutes(list, year);
        }

        return [.. list.OrderBy(h => h.Date)];
    }

    private static void AddSubstitutes(List<Holiday> list, int year)
    {
        bool IsHoliday(DateOnly date) => list.Any(h => h.Date == date);
        int CountOn(DateOnly date) => list.Count(h => h.Date == date);

        DateOnly NextFree(DateOnly after, bool weekdayOnly)
        {
            var date = after.AddDays(1);
            while (IsHoliday(date) || (weekdayOnly && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                   || date.DayOfWeek == DayOfWeek.Sunday)
            {
                date = date.AddDays(1);
            }
            return date;
        }

        // 설날·추석 연휴
        foreach (var center in new[] { LunarCalendar.Seollal(year), LunarCalendar.Chuseok(year) })
        {
            var days = new[] { center.AddDays(-1), center, center.AddDays(1) };
            var overlaps = days.Count(d => d.DayOfWeek == DayOfWeek.Sunday) + days.Sum(d => CountOn(d) - 1);
            for (var i = 0; i < overlaps; i++)
            {
                var name = list.First(h => h.Date == center).Name;
                list.Add(new Holiday(NextFree(days[^1], weekdayOnly: false), "대체공휴일 (" + name + ")", IsSubstitute: true));
            }
        }

        // 주말 대체 대상 공휴일
        var weekendRule = new[] { "삼일절", "어린이날", "부처님오신날", "광복절", "개천절", "한글날", "성탄절" };
        foreach (var holiday in list.Where(h => weekendRule.Contains(h.Name)).ToList())
        {
            var isWeekend = holiday.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var overlapsOther = list.Any(h => h.Date == holiday.Date && h != holiday && !h.IsSubstitute
                                              && string.CompareOrdinal(h.Name, holiday.Name) < 0);
            if (isWeekend || overlapsOther)
            {
                list.Add(new Holiday(NextFree(holiday.Date, weekdayOnly: true), $"대체공휴일 ({holiday.Name})", IsSubstitute: true));
            }
        }
    }
}
