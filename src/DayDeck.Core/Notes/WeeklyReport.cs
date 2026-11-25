using System.Globalization;
using System.Text.RegularExpressions;
using DayDeck.Core.Settings;

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
/// 주간업무보고 글 (메모 폴더의 <c>주간업무보고\연도\2026-10 5주차.txt</c>). 양식(<see cref="WeeklyReportSettings"/>)의
/// 제목과 항목 머리글의 날짜는 저장할 때마다 주차에서 다시 만들어 쓰므로 틀릴 수 없고, 사람은 항목 내용만 쓴다.
/// <code>
/// 주간업무 보고 10월 5주차
/// 금주실적(10/26 ~ 10/30)
///  - …
///
/// 차주계획(11/2 ~ 11/6)
///  - …
///
/// 이슈사항
///  - …
/// </code>
/// </summary>
public static partial class WeeklyReport
{
    public const string FolderName = "주간업무보고";

    /// <summary>새 보고의 빈 항목.</summary>
    public const string EmptyItem = " - ";

    /// <summary>제목: 양식의 <c>{년} {월} {주차} {금주} {차주}</c>를 채운다.</summary>
    public static string Title(ReportWeek week, WeeklyReportSettings template) =>
        template.Title
            .Replace("{년}", week.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{월}", week.Month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{주차}", week.WeekOfMonth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{금주}", week.Range, StringComparison.Ordinal)
            .Replace("{차주}", week.Next.Range, StringComparison.Ordinal);

    /// <summary>항목 머리글: "금주실적(10/26 ~ 10/30)", "이슈사항".</summary>
    public static string Header(ReportWeek week, ReportSection section) => section.Dates switch
    {
        ReportDateRange.ThisWeek => $"{section.Name}({week.Range})",
        ReportDateRange.NextWeek => $"{section.Name}({week.Next.Range})",
        _ => section.Name,
    };

    /// <summary>
    /// 보고 글 전체. 켜 둔 항목은 내용이 비었으면 빈 항목(" - ")으로 쓰고,
    /// 꺼 둔 항목은 내용이 있을 때만 쓴다 (예전에 쓴 내용을 지우지 않도록).
    /// </summary>
    public static string Write(ReportWeek week, WeeklyReportSettings template, IReadOnlyDictionary<string, string> bodies)
    {
        var lines = new List<string> { Title(week, template) };
        var first = true;
        foreach (var section in template.Sections)
        {
            var body = bodies.GetValueOrDefault(section.Name) ?? "";
            if (!section.Enabled && IsBlank(body))
            {
                continue;
            }
            if (!first)
            {
                lines.Add("");
            }
            lines.Add(Header(week, section));
            lines.Add(Body(body));
            first = false;
        }
        return string.Join("\n", lines);
    }

    private static string Body(string text)
    {
        var body = text.Replace("\r\n", "\n").TrimEnd('\n');
        return IsBlank(body) ? EmptyItem : body;
    }

    /// <summary>
    /// 저장된 글에서 항목별 내용만 꺼낸다 (키: 항목 이름). 제목 줄과 머리글의 날짜는 버린다.
    /// 머리글은 항목 이름(또는 예전 이름)만 있거나 그 뒤에 "(날짜)"·":"만 붙은 줄이다 ("이슈사항: 서버 장애" 같은 내용 줄은 아님).
    /// 아는 머리글이 하나도 없으면 글 전체를 첫 항목 내용으로 본다.
    /// </summary>
    public static Dictionary<string, string> Parse(string text, WeeklyReportSettings template)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var start = 0;

        // 빈 줄만 앞뒤에서 뗀다 (빈 항목 " - "의 끝 공백은 남겨야 이어 쓸 때 "-글"이 되지 않는다).
        void Flush(int end)
        {
            if (current is null)
            {
                return;
            }
            var body = string.Join("\n", lines[start..end]).Trim('\n');
            bodies[current] = bodies.TryGetValue(current, out var earlier) && earlier.Length > 0 ? earlier + "\n" + body : body;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (HeadingOf(lines[i], template) is { } section)
            {
                Flush(i);
                current = section.Name;
                start = i + 1;
            }
        }
        Flush(lines.Length);

        if (current is null && template.ActiveSections().FirstOrDefault() is { } firstSection)
        {
            bodies[firstSection.Name] = text.Replace("\r\n", "\n").Trim('\n');
        }
        return bodies;
    }

    private static ReportSection? HeadingOf(string line, WeeklyReportSettings template)
    {
        var trimmed = line.Trim();
        foreach (var section in template.Sections)
        {
            foreach (var name in section.Aliases.Prepend(section.Name))
            {
                if (trimmed.StartsWith(name, StringComparison.Ordinal) && HeadingRest().IsMatch(trimmed[name.Length..]))
                {
                    return section;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 새 주의 보고에 미리 넣을 내용: 지난주 보고의 첫 "차주" 항목을 이번 주의 첫 "금주" 항목으로.
    /// 그런 항목이 없거나 지난주 내용이 비었으면 빈 사전.
    /// </summary>
    public static Dictionary<string, string> CarryOver(string previousReport, WeeklyReportSettings template)
    {
        var plan = template.ActiveSections().FirstOrDefault(s => s.Dates == ReportDateRange.NextWeek);
        var done = template.ActiveSections().FirstOrDefault(s => s.Dates == ReportDateRange.ThisWeek);
        if (plan is null || done is null || !Parse(previousReport, template).TryGetValue(plan.Name, out var carried) || IsBlank(carried))
        {
            return [];
        }
        return new(StringComparer.Ordinal) { [done.Name] = carried };
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

    /// <summary>머리글에서 항목 이름 뒤에 올 수 있는 것: 없음, "(10/26 ~ 10/30)", ":".</summary>
    [GeneratedRegex(@"^\s*(\([^)]*\))?\s*[:：]?$")]
    private static partial Regex HeadingRest();

    [GeneratedRegex(@"^(?<year>\d{4})-(?<month>\d{2}) (?<week>\d)주차$")]
    private static partial Regex FileName();
}
