using System.Globalization;

namespace DayDeck.Core.Data;

/// <summary>
/// DB에 저장하는 날짜/시각 문자열 형식 (로컬 시각, ISO-8601). 문자열 비교만으로 정렬과 범위 조회가 된다.
/// 날짜만: <c>2026-10-28</c>, 분 단위: <c>2026-10-28T14:00</c>, 기록용: <c>2026-10-28T14:00:05</c>.
/// </summary>
internal static class DbTime
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string MinuteFormat = "yyyy-MM-dd'T'HH:mm";
    private const string StampFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static string Date(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string Stamp(DateTime time) => time.ToString(StampFormat, CultureInfo.InvariantCulture);

    public static string? Due(DateOnly? date, TimeOnly? time) => date switch
    {
        null => null,
        { } d when time is { } t => d.ToDateTime(t).ToString(MinuteFormat, CultureInfo.InvariantCulture),
        { } d => Date(d),
    };

    public static (DateOnly? Date, TimeOnly? Time) ParseDue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (null, null);
        }

        if (value.Length == DateFormat.Length)
        {
            return (ParseDate(value), null);
        }

        var dateTime = DateTime.ParseExact(value, MinuteFormat, CultureInfo.InvariantCulture);
        return (DateOnly.FromDateTime(dateTime), TimeOnly.FromDateTime(dateTime));
    }

    public static DateOnly? ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);

    public static DateTime? ParseStamp(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTime.ParseExact(value, StampFormat, CultureInfo.InvariantCulture);
}
