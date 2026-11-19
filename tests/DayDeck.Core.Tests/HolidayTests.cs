using DayDeck.Core.Calendar;

namespace DayDeck.Core.Tests;

public sealed class HolidayTests
{
    [Theory]
    [InlineData(2020, 1, 25)]
    [InlineData(2021, 2, 12)]
    [InlineData(2022, 2, 1)]
    [InlineData(2023, 1, 22)]
    [InlineData(2024, 2, 10)]
    [InlineData(2025, 1, 29)]
    [InlineData(2026, 2, 17)]
    [InlineData(2027, 2, 7)] // 중국(UTC+8)은 2/6, 한국(UTC+9)은 2/7
    [InlineData(2034, 2, 19)] // 2033년 음력 문제 예외
    public void Seollal(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), LunarCalendar.Seollal(year));

    [Theory]
    [InlineData(2020, 10, 1)]
    [InlineData(2021, 9, 21)]
    [InlineData(2022, 9, 10)]
    [InlineData(2023, 9, 29)]
    [InlineData(2024, 9, 17)]
    [InlineData(2025, 10, 6)]
    [InlineData(2026, 9, 25)]
    public void Chuseok(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), LunarCalendar.Chuseok(year));

    [Theory]
    [InlineData(2020, 4, 30)]
    [InlineData(2021, 5, 19)]
    [InlineData(2022, 5, 8)]
    [InlineData(2023, 5, 27)]
    [InlineData(2024, 5, 15)]
    [InlineData(2025, 5, 5)]
    [InlineData(2026, 5, 24)]
    public void BuddhasBirthday(int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), LunarCalendar.BuddhasBirthday(year));

    [Fact]
    public void NewMoon_MatchesMeeusExample()
    {
        // Meeus 예제 49.a: 1977년 2월 합삭 JDE 2443192.65118 (k = -283)
        Assert.Equal(2443192.65118 - 70.0 / 86400, LunarCalendar.NewMoonJd(-283), 3);
    }

    private static string[] Names(int year, int month, int day) =>
        [.. KoreanHolidays.ForYear(year).Where(h => h.Date == new DateOnly(year, month, day)).Select(h => h.Name)];

    [Fact]
    public void Holidays2025_IncludeSubstitutes()
    {
        Assert.Equal(["삼일절"], Names(2025, 3, 1));
        Assert.Equal(["대체공휴일 (삼일절)"], Names(2025, 3, 3));          // 토요일
        Assert.Contains("부처님오신날", Names(2025, 5, 5));
        Assert.Contains("어린이날", Names(2025, 5, 5));
        Assert.Single(Names(2025, 5, 6));                                  // 겹침 → 대체 1일
        Assert.StartsWith("대체공휴일", Names(2025, 5, 6)[0]);
        Assert.Equal(["추석 연휴"], Names(2025, 10, 5));
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2025, 10, 8))); // 추석 연휴 일요일
    }

    [Fact]
    public void Holidays2026_IncludeSubstitutes()
    {
        Assert.Equal(["설날 연휴"], Names(2026, 2, 16));
        Assert.Equal(["설날"], Names(2026, 2, 17));
        Assert.Equal(["설날 연휴"], Names(2026, 2, 18));
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2026, 3, 2)));  // 삼일절 일요일
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2026, 5, 25))); // 부처님오신날 일요일
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2026, 8, 17))); // 광복절 토요일
        Assert.Empty(Names(2026, 9, 28));                                   // 추석 연휴 토요일은 대체 없음
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2026, 10, 5))); // 개천절 토요일
        Assert.Empty(Names(2026, 6, 8));                                    // 현충일(토)은 대체 없음
    }

    [Fact]
    public void Holidays2024_SeollalOnSunday()
    {
        Assert.Equal(["설날 연휴"], Names(2024, 2, 9));
        Assert.Equal(["설날 연휴"], Names(2024, 2, 11));
        Assert.StartsWith("대체공휴일", Assert.Single(Names(2024, 2, 12)));
    }

    [Fact]
    public void Between_SpansYears()
    {
        var holidays = KoreanHolidays.Between(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 5));
        Assert.Equal(["성탄절", "신정"], holidays.Select(h => h.Name));
    }
}
