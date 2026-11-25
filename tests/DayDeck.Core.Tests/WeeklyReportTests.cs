using DayDeck.Core.Notes;
using DayDeck.Core.Settings;

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

    private static readonly WeeklyReportSettings Template = new();

    private static Dictionary<string, string> Bodies(params (string Name, string Body)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Body);

    [Fact]
    public void Write_DefaultTemplate_HasFourSectionsWithComputedDates()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 10, 28));

        var text = WeeklyReport.Write(week, Template, Bodies(("금주실적", " - 견적 회신\n - 회의록 정리"), ("이슈사항", " - 서버 점검 지연")));

        Assert.Equal(
            "주간업무 보고 10월 5주차\n금주실적(10/26 ~ 10/30)\n - 견적 회신\n - 회의록 정리\n\n차주계획(11/2 ~ 11/6)\n - "
            + "\n\n이슈사항\n - 서버 점검 지연\n\n문의사항\n - ",
            text);
        Assert.Equal("2026년 10월 5주차", week.Label);
    }

    [Fact]
    public void Write_AcrossYearEnd()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 12, 29));

        Assert.Equal("주간업무 보고 12월 5주차", WeeklyReport.Title(week, Template));
        Assert.Equal("금주실적(12/28 ~ 1/1)", WeeklyReport.Header(week, Template.Sections[0]));
        Assert.Equal("차주계획(1/4 ~ 1/8)", WeeklyReport.Header(week, Template.Sections[1]));
        Assert.Equal("주간업무보고/2026/2026-12 5주차.txt", WeeklyReport.PathOf(week));
    }

    [Fact]
    public void Write_CustomTemplate_TitlePlaceholdersOrderAndDisabledSections()
    {
        var template = new WeeklyReportSettings
        {
            Title = "[{년}] {월}월 {주차}주 보고 ({금주})",
            Sections =
            [
                new("문의사항"),
                new("금주 한 일", ReportDateRange.ThisWeek),
                new("차주보고", ReportDateRange.NextWeek),
                new("이슈사항") { Enabled = false },
            ],
        };
        var week = ReportWeek.Of(new DateOnly(2026, 10, 28));

        var text = WeeklyReport.Write(week, template, Bodies(("금주 한 일", " - A")));

        Assert.Equal("[2026] 10월 5주 보고 (10/26 ~ 10/30)\n문의사항\n - \n\n금주 한 일(10/26 ~ 10/30)\n - A\n\n차주보고(11/2 ~ 11/6)\n - ", text);

        // 꺼 둔 항목이라도 이미 쓴 내용은 지우지 않는다.
        var kept = WeeklyReport.Write(week, template, Bodies(("이슈사항", " - 예전 이슈")));
        Assert.EndsWith("차주보고(11/2 ~ 11/6)\n - \n\n이슈사항\n - 예전 이슈", kept);
    }

    [Fact]
    public void Parse_TakesOnlyBodies_IgnoringWrongDatesInHeaders()
    {
        // 손으로 고쳐 날짜를 잘못 적은 파일이어도 내용만 읽고, 다시 쓰면 날짜는 주차대로 바로잡힌다.
        const string typed = "주간업무 보고 10월 4주차\r\n금주실적(10/19 ~ 10/23)\r\n - 실적\r\n\r\n차주계획(10/26~ 10/30)\r\n - 계획 1\r\n - \r\n";
        var week = ReportWeek.Of(new DateOnly(2026, 10, 26));

        var bodies = WeeklyReport.Parse(typed, Template);

        Assert.Equal(" - 실적", bodies["금주실적"]);
        Assert.Equal(" - 계획 1\n - ", bodies["차주계획"]);
        Assert.False(bodies.ContainsKey("이슈사항")); // 예전(두 항목) 보고도 그대로 읽힌다
        Assert.StartsWith("주간업무 보고 10월 5주차\n금주실적(10/26 ~ 10/30)\n - 실적\n\n차주계획(11/2 ~ 11/6)\n - 계획 1\n - \n\n이슈사항\n - ",
            WeeklyReport.Write(week, Template, bodies));
    }

    [Fact]
    public void Parse_RoundTripsWrite_KeepingEmptyItems()
    {
        var week = ReportWeek.Of(new DateOnly(2026, 10, 26));
        var written = Bodies(("금주실적", " - 실적"), ("이슈사항", " - 이슈\n   세부 내용"), ("문의사항", " - 질문"));

        var bodies = WeeklyReport.Parse(WeeklyReport.Write(week, Template, written), Template);

        Assert.Equal(" - 실적", bodies["금주실적"]);
        Assert.Equal(WeeklyReport.EmptyItem, bodies["차주계획"]);
        Assert.Equal(" - 이슈\n   세부 내용", bodies["이슈사항"]);
        Assert.Equal(" - 질문", bodies["문의사항"]);
        Assert.True(WeeklyReport.IsBlank(bodies["차주계획"]));
        Assert.False(WeeklyReport.IsBlank(" - 보고"));
    }

    [Fact]
    public void Parse_ContentLineStartingWithSectionName_IsNotAHeading()
    {
        var text = "제목\n금주실적(10/26 ~ 10/30)\n이슈사항: 서버 장애 대응\n\n이슈사항:\n - 없음";

        var bodies = WeeklyReport.Parse(text, Template);

        Assert.Equal("이슈사항: 서버 장애 대응", bodies["금주실적"]);
        Assert.Equal(" - 없음", bodies["이슈사항"]);
    }

    [Fact]
    public void Parse_RenamedSection_ReadsOldHeadingThroughAlias()
    {
        var template = new WeeklyReportSettings();
        template.Sections[1].Name = "차주보고";
        template.Sections[1].Aliases = ["차주계획"];

        var bodies = WeeklyReport.Parse("제목\n금주실적(10/26 ~ 10/30)\n - A\n\n차주계획(11/2 ~ 11/6)\n - B", template);

        Assert.Equal(" - B", bodies["차주보고"]);
    }

    [Fact]
    public void Parse_WithoutHeadings_KeepsTextAsFirstSection() =>
        Assert.Equal("그냥 메모", WeeklyReport.Parse("그냥 메모\n", Template)["금주실적"]);

    [Fact]
    public void CarryOver_MovesLastWeeksPlanIntoThisWeeksDone()
    {
        var lastWeek = ReportWeek.Of(new DateOnly(2026, 10, 19));
        var previous = WeeklyReport.Write(lastWeek, Template, Bodies(("차주계획", " - 다음 할 일"), ("이슈사항", " - 이슈")));

        var carried = WeeklyReport.CarryOver(previous, Template);

        Assert.Equal(" - 다음 할 일", Assert.Single(carried, kv => kv.Key == "금주실적").Value);
        Assert.Single(carried);
        Assert.Empty(WeeklyReport.CarryOver(WeeklyReport.Write(lastWeek, Template, Bodies()), Template));
    }

    [Fact]
    public void Normalize_FixesBlankDuplicateAndAllDisabledSections()
    {
        var template = new WeeklyReportSettings
        {
            Title = " ",
            Sections = [new(" 금주실적 ") { Enabled = false, Aliases = ["금주실적", "예전"] }, new(""), new("금주실적") { Enabled = false }],
        };

        template.Normalize();

        Assert.Equal(WeeklyReportSettings.DefaultTitle, template.Title);
        var section = Assert.Single(template.Sections);
        Assert.Equal("금주실적", section.Name);
        Assert.True(section.Enabled);
        Assert.Equal(["예전"], section.Aliases);

        var swapped = new WeeklyReportSettings { Sections = [new("A") { Aliases = ["B", "C"] }, new("B")] };
        swapped.Normalize();
        Assert.Equal(["C"], swapped.Sections[0].Aliases); // 다른 항목의 지금 이름은 예전 이름으로 두지 않는다

        var empty = new WeeklyReportSettings { Sections = [] };
        empty.Normalize();
        Assert.Equal(["금주실적", "차주계획", "이슈사항", "문의사항"], empty.Sections.Select(s => s.Name));
    }

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
