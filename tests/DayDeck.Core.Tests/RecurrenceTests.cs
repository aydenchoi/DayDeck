using DayDeck.Core.Calendar;

namespace DayDeck.Core.Tests;

public sealed class RecurrenceTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 9, 0, 0); // 수요일

    private static List<DateTime> Expand(string rrule, DateTime? start = null, int days = 120) =>
        [.. RecurrenceRule.Parse(rrule)!.Expand(start ?? Start, (start ?? Start).AddDays(days))];

    [Fact]
    public void Daily_WithInterval()
    {
        var dates = Expand("FREQ=DAILY;INTERVAL=2;COUNT=3");
        Assert.Equal([Start, Start.AddDays(2), Start.AddDays(4)], dates);
    }

    [Fact]
    public void Weekly_OnSeveralDays()
    {
        var dates = Expand("FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=5");
        Assert.Equal(
            [new(2026, 10, 28, 9, 0, 0), new(2026, 10, 30, 9, 0, 0), new(2026, 11, 2, 9, 0, 0), new(2026, 11, 4, 9, 0, 0), new(2026, 11, 6, 9, 0, 0)],
            dates);
    }

    [Fact]
    public void Weekly_EveryOtherWeek()
    {
        var dates = Expand("FREQ=WEEKLY;INTERVAL=2;COUNT=3");
        Assert.Equal([Start, Start.AddDays(14), Start.AddDays(28)], dates);
    }

    [Fact]
    public void Monthly_OnNthWeekday()
    {
        var dates = Expand("FREQ=MONTHLY;BYDAY=2TU;COUNT=3", new DateTime(2026, 10, 13, 10, 0, 0));
        Assert.Equal([new(2026, 10, 13, 10, 0, 0), new(2026, 11, 10, 10, 0, 0), new(2026, 12, 8, 10, 0, 0)], dates);
    }

    [Fact]
    public void Monthly_LastFriday()
    {
        var dates = Expand("FREQ=MONTHLY;BYDAY=-1FR;COUNT=2", new DateTime(2026, 10, 30, 18, 0, 0));
        Assert.Equal([new(2026, 10, 30, 18, 0, 0), new(2026, 11, 27, 18, 0, 0)], dates);
    }

    [Fact]
    public void Monthly_On31st_SkipsShortMonths()
    {
        var dates = Expand("FREQ=MONTHLY;COUNT=3", new DateTime(2026, 1, 31), days: 400);
        Assert.Equal([new(2026, 1, 31), new(2026, 3, 31), new(2026, 5, 31)], dates);
    }

    [Fact]
    public void Yearly_OnLeapDay()
    {
        var dates = Expand("FREQ=YEARLY;COUNT=2", new DateTime(2024, 2, 29), days: 3000);
        Assert.Equal([new(2024, 2, 29), new(2028, 2, 29)], dates);
    }

    [Fact]
    public void Until_IsInclusive()
    {
        var dates = Expand("FREQ=DAILY;UNTIL=20261030T235959");
        Assert.Equal(3, dates.Count);
    }

    [Fact]
    public void Expand_StopsAtRangeEnd()
    {
        Assert.Equal(10, RecurrenceRule.Parse("FREQ=DAILY")!.Expand(Start, Start.AddDays(10)).Count());
    }

    [Theory]
    [InlineData("FREQ=DAILY", "매일")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE", "매주 월, 수")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", "2주마다 수")]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU", "매월 둘째 화요일")]
    [InlineData("FREQ=MONTHLY", "매월 28일")]
    [InlineData("FREQ=YEARLY;COUNT=10", "매년 10월 28일 · 10회")]
    [InlineData("FREQ=DAILY;UNTIL=20261231", "매일 · 2026-12-31까지")]
    public void Describe(string rrule, string expected) =>
        Assert.Equal(expected, RecurrenceRule.Parse(rrule)!.Describe(DateOnly.FromDateTime(Start)));

    [Fact]
    public void ToString_RoundTrips()
    {
        var rule = RecurrenceRule.Parse("RRULE:FREQ=MONTHLY;INTERVAL=2;BYDAY=-1FR;UNTIL=20271231T235959")!;
        Assert.Equal(rule, RecurrenceRule.Parse(rule.ToString()));
        Assert.Equal("FREQ=MONTHLY;INTERVAL=2;BYDAY=-1FR;UNTIL=20271231T235959", rule.ToString());
    }

    [Fact]
    public void Parse_RejectsUnknown()
    {
        Assert.Null(RecurrenceRule.Parse("FREQ=HOURLY"));
        Assert.Null(RecurrenceRule.Parse(""));
    }
}
