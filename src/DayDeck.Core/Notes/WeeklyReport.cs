using System.Globalization;
using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

/// <summary>
/// 주간업무보고의 한 주 (월~금). 주차는 목요일 기준: 그 주의 목요일이 있는 달의 몇째 목요일인지로 정한다
/// (예: 2026-10-26(월)~10-30(금)은 목요일 10/29가 10월의 다섯째 목요일이라 "10월 5주차",
/// 2026-12-28~2027-01-01은 목요일 12/31이 있어 2026년 12월 5주차). 날짜는 사람이 쓰지 않고 모두 여기서 계산한다.
/// </summary>
public sealed record ReportWeek(DateOnly Monday)
{
    public DateOnly Thursday => Monday.AddDays(3);

    public DateOnly Friday => Monday.AddDays(4);

    /// <summary>주차가 속한 해 (목요일의 해).</summary>
    public int Year => Thursday.Year;

    /// <summary>주차가 속한 달 (목요일의 달).</summary>
    public int Month => Thursday.Month;

    /// <summary>그 달의 몇째 주 (1~5).</summary>
    public int WeekOfMonth => (Thursday.Day - 1) / 7 + 1;

    public ReportWeek Next => new(Monday.AddDays(7));

    public ReportWeek Previous => new(Monday.AddDays(-7));

    /// <summary>날짜가 들어 있는 주 (토·일은 그 주 월요일의 주).</summary>
    public static ReportWeek Of(DateOnly date) => new(date.AddDays(-(((int)date.DayOfWeek + 6) % 7)));

    /// <summary>"2026년 10월 5주차"</summary>
    public string Label => $"{Year}년 {Month}월 {WeekOfMonth}주차";

    /// <summary>"10/26 ~ 10/30"</summary>
    public string Range => $"{Short(Monday)} ~ {Short(Friday)}";

    private static string Short(DateOnly date) => date.ToString("M/d", CultureInfo.InvariantCulture);
}

/// <summary>
/// 주간업무보고 글 (메모 폴더의 <c>주간업무보고\연도\2026-10 5주차.txt</c>). 제목과 금주실적·차주계획의 날짜는
/// 저장할 때마다 주차에서 다시 만들어 쓰므로 틀릴 수 없고, 사람은 두 내용만 쓴다.
/// <code>
/// 주간업무 보고 10월 5주차
/// 금주실적(10/26 ~ 10/30)
///  - …
///
/// 차주계획(11/2 ~ 11/6)
///  - …
/// </code>
/// </summary>
public static partial class WeeklyReport
{
    public const string FolderName = "주간업무보고";

    /// <summary>새 보고의 빈 항목.</summary>
    public const string EmptyItem = " - ";

    private const string DoneHeading = "금주실적";
    private const string PlanHeading = "차주계획";

    public static string Title(ReportWeek week) => $"주간업무 보고 {week.Month}월 {week.WeekOfMonth}주차";

    public static string DoneHeader(ReportWeek week) => $"{DoneHeading}({week.Range})";

    public static string PlanHeader(ReportWeek week) => $"{PlanHeading}({week.Next.Range})";

    /// <summary>보고 글 전체. 내용이 비었으면 빈 항목(" - ")을 둔다.</summary>
    public static string Write(ReportWeek week, string done, string plan) =>
        string.Join("\n", Title(week), DoneHeader(week), Body(done), "", PlanHeader(week), Body(plan));

    private static string Body(string text)
    {
        var body = text.Replace("\r\n", "\n").TrimEnd('\n');
        return IsBlank(body) ? EmptyItem : body;
    }

    /// <summary>
    /// 저장된 글에서 금주실적·차주계획 내용만 꺼낸다 (제목·머리글 줄의 날짜는 버린다). 머리글이 없으면 글 전체를 금주실적으로 본다.
    /// </summary>
    public static (string Done, string Plan) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var done = Array.FindIndex(lines, l => l.TrimStart().StartsWith(DoneHeading, StringComparison.Ordinal));
        var plan = Array.FindIndex(lines, l => l.TrimStart().StartsWith(PlanHeading, StringComparison.Ordinal));
        // 빈 줄만 앞뒤에서 뗀다 (빈 항목 " - "의 끝 공백은 남겨야 이어 쓸 때 "-글"이 되지 않는다).
        string Join(int from, int to) => string.Join("\n", lines[from..to]).Trim('\n');
        if (done < 0 && plan < 0)
        {
            return (text.Replace("\r\n", "\n").Trim('\n'), "");
        }
        if (plan < 0)
        {
            return (Join(done + 1, lines.Length), "");
        }
        if (done < 0 || done > plan)
        {
            return (done < 0 ? "" : Join(done + 1, lines.Length), Join(plan + 1, done < 0 ? lines.Length : done));
        }
        return (Join(done + 1, plan), Join(plan + 1, lines.Length));
    }

    /// <summary>내용을 쓰지 않은 보고인지 (빈 항목만 있음).</summary>
    public static bool IsBlank(string body) => body.Replace("-", "").Trim().Length == 0;

    /// <summary>보고가 들어가는 폴더 (해마다): "주간업무보고/2026".</summary>
    public static string FolderOf(ReportWeek week) => $"{FolderName}/{week.Year}";

    /// <summary>확장자 없는 파일 이름: "2026-10 5주차" (이름 순 = 날짜 순).</summary>
    public static string NameOf(ReportWeek week) => $"{week.Year}-{week.Month:00} {week.WeekOfMonth}주차";

    /// <summary>메모 폴더 안의 상대 경로: "주간업무보고/2026/2026-10 5주차.txt".</summary>
    public static string PathOf(ReportWeek week) => $"{FolderOf(week)}/{NameOf(week)}.txt";

    /// <summary>파일 이름("2026-10 5주차")에서 주를 찾는다. 그 달에 그 주차가 없으면 null.</summary>
    public static ReportWeek? FromFileName(string fileName)
    {
        if (FileName().Match(Path.GetFileNameWithoutExtension(fileName)) is not { Success: true } match)
        {
            return null;
        }
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups["week"].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || number < 1)
        {
            return null;
        }
        var first = new DateOnly(year, month, 1);
        var firstThursday = first.AddDays(((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7);
        var thursday = firstThursday.AddDays(7 * (number - 1));
        return thursday.Month == month ? ReportWeek.Of(thursday) : null;
    }

    [GeneratedRegex(@"^(?<year>\d{4})-(?<month>\d{2}) (?<week>\d)주차$")]
    private static partial Regex FileName();
}
