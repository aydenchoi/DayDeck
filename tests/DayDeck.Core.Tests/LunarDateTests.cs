using DayDeck.Core.Calendar;

namespace DayDeck.Core.Tests;

/// <summary>양력 → 음력 (달력의 음력 표시). 공휴일 날짜와 알려진 윤달로 확인한다.</summary>
public sealed class LunarDateTests
{
    public static TheoryData<int> Years => [.. Enumerable.Range(2020, 13)]; // 2020~2032 (2033년 윤달 문제 제외)

    [Theory]
    [MemberData(nameof(Years))]
    public void HolidaysHaveTheirLunarDates(int year)
    {
        Assert.Equal(new LunarDate(1, 1, false), LunarCalendar.FromSolar(LunarCalendar.Seollal(year)));
        Assert.Equal(new LunarDate(4, 8, false), LunarCalendar.FromSolar(LunarCalendar.BuddhasBirthday(year)));
        Assert.Equal(new LunarDate(8, 15, false), LunarCalendar.FromSolar(LunarCalendar.Chuseok(year)));
        // 설날 전날은 지난해 12월
        Assert.Equal(12, LunarCalendar.FromSolar(LunarCalendar.Seollal(year).AddDays(-1)).Month);
    }

    [Theory]
    [InlineData("2020-05-23", 4, 1, true)]   // 2020년 윤4월
    [InlineData("2020-06-21", 5, 1, false)]
    [InlineData("2023-03-22", 2, 1, true)]   // 2023년 윤2월
    [InlineData("2023-04-20", 3, 1, false)]
    [InlineData("2025-07-25", 6, 1, true)]   // 2025년 윤6월
    [InlineData("2025-08-23", 7, 1, false)]
    [InlineData("2026-09-25", 8, 15, false)] // 2026년 추석
    public void KnownDates(string solar, int month, int day, bool leap) =>
        Assert.Equal(new LunarDate(month, day, leap), LunarCalendar.FromSolar(DateOnly.Parse(solar)));

    [Fact]
    public void Format() => Assert.Equal("윤6.1", new LunarDate(6, 1, true).ToString());
}
