using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

public sealed class WeeklyReportTests
{
    [Theory]
    [InlineData("2026-10-26", "2026-10-26", 2026, 10, 5)] // 월요일
    [InlineData("2026-10-29", "2026-10-26", 2026, 10, 5)] // 목요일
    [InlineData("2026-10-31", "2026-10-26", 2026, 10, 5)] // 토요일은 그 주
    [InlineData("2026-11-01", "2026-10-26", 2026, 10, 5)] // 일요일도 그 주
    [InlineData("2026-11-02", "2026-11-02", 2026, 11, 1)]
    [InlineData("2026-09-28", "2026-09-28", 2026, 10, 1)] // 월요일은 9월이지만 목요일 10/1이 10월
    [InlineData("2026-06-30", "2026-06-29", 2026, 7, 1)]  // 목요일 7/2
    [InlineData("2026-12-30", "2026-12-28", 2026, 12, 5)] // 목요일 12/31
    [InlineData("2027-01-01", "2026-12-28", 2026, 12, 5)] // 새해 첫날(금)도 2026년 12월 5주차
    [InlineData("2027-01-04", "2027-01-04", 2027, 1, 1)]
    public void Of_WeekNumberFollowsThursday(string date, string monday, int year, int month, int week)
    {
        var report = ReportWeek.Of(DateOnly.Parse(date));

        Assert.Equal(DateOnly.Parse(monday), report.Monday);
        Assert.Equal((year, month, week), (report.Year, report.Month, report.WeekOfMonth));
    }

    [Fact]
    public void Write_UsesTemplate_WithComputedDates()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 10, 28));

        var text = WeeklyReport.Write(week, " - 견적 회신\n - 회의록 정리", "");

        Assert.Equal(
            "주간업무 보고 10월 5주차\n금주실적(10/26 ~ 10/30)\n - 견적 회신\n - 회의록 정리\n\n차주계획(11/2 ~ 11/6)\n - ",
            text);
        Assert.Equal("2026년 10월 5주차", week.Label);
    }

    [Fact]
    public void Write_AcrossYearEnd()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 12, 29));

        Assert.Equal("주간업무 보고 12월 5주차", WeeklyReport.Title(week));
        Assert.Equal("금주실적(12/28 ~ 1/1)", WeeklyReport.DoneHeader(week));
        Assert.Equal("차주계획(1/4 ~ 1/8)", WeeklyReport.PlanHeader(week));
        Assert.Equal("주간업무보고/2026/2026-12 5주차.txt", WeeklyReport.PathOf(week));
    }

    [Fact]
    public void Parse_TakesOnlyBodies_IgnoringWrongDatesInHeaders()
    {
        // 손으로 고쳐 날짜를 잘못 적은 파일이어도 내용만 읽고, 다시 쓰면 날짜는 주차대로 바로잡힌다.
        const string typed = "주간업무 보고 10월 4주차\r\n금주실적(10/19 ~ 10/23)\r\n - 실적\r\n\r\n차주계획(10/26~ 10/30)\r\n - 계획 1\r\n - \r\n";
        var week = ReportWeek.Of(new DateOnly(2026, 10, 26));

        var (done, plan) = WeeklyReport.Parse(typed);

        Assert.Equal(" - 실적", done);
        Assert.Equal(" - 계획 1\n - ", plan);
        Assert.Equal("주간업무 보고 10월 5주차\n금주실적(10/26 ~ 10/30)\n - 실적\n\n차주계획(11/2 ~ 11/6)\n - 계획 1\n - ",
            WeeklyReport.Write(week, done, plan));
    }

    [Fact]
    public void Parse_RoundTripsWrite_KeepingEmptyItem()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 10, 26));

        var (done, plan) = WeeklyReport.Parse(WeeklyReport.Write(week, "", ""));

        Assert.Equal(WeeklyReport.EmptyItem, done);
        Assert.Equal(WeeklyReport.EmptyItem, plan);
        Assert.True(WeeklyReport.IsBlank(done));
        Assert.False(WeeklyReport.IsBlank(" - 보고"));
    }

    [Fact]
    public void Parse_WithoutHeadings_KeepsTextAsDone() =>
        Assert.Equal(("그냥 메모", ""), WeeklyReport.Parse("그냥 메모\n"));

    [Theory]
    [InlineData("2026-10 5주차.txt", "2026-10-26")]
    [InlineData("2026-10 1주차", "2026-09-28")]
    [InlineData("2027-01 1주차.txt", "2027-01-04")]
    public void FromFileName_FindsWeek(string name, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), WeeklyReport.FromFileName(name)!.Monday);

    [Theory]
    [InlineData("2026-11 5주차.txt")] // 2026년 11월에는 목요일이 4번
    [InlineData("2026-13 1주차.txt")]
    [InlineData("회의록.txt")]
    public void FromFileName_RejectsUnknown(string name) => Assert.Null(WeeklyReport.FromFileName(name));

    [Fact]
    public void NameOf_RoundTripsFromFileName()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 3, 5));

        Assert.Equal(week, WeeklyReport.FromFileName(WeeklyReport.NameOf(week) + ".txt"));
    }
}
