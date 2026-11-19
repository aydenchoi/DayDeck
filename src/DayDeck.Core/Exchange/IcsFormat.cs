using System.Globalization;
using System.Text;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Exchange;

/// <summary>ICS에서 읽은 내용.</summary>
public sealed record IcsImport(IReadOnlyList<CalendarEvent> Events, IReadOnlyList<TaskItem> Tasks, int Skipped);

/// <summary>
/// iCalendar(.ics, RFC 5545) 내보내기/가져오기. 구글 캘린더, Outlook, 아이폰 달력과 주고받을 수 있다.
/// 일정(VEVENT)과 할 일(VTODO)을 다루고, 반복은 이 앱이 지원하는 RRULE 부분만 유지한다.
/// 시간대가 있는 시각(TZID)은 그 시간대로, Z(UTC)는 이 PC 시간대로 바꾼다.
/// </summary>
public static class IcsFormat
{
    private const string ProductId = "-//DayDeck//DayDeck 0.1//KO";

    public static string Export(IEnumerable<CalendarEvent> events, IEnumerable<TaskItem> tasks, DateTime now)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:" + ProductId);
        Line(sb, "CALSCALE:GREGORIAN");
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        foreach (var e in events)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, $"UID:event-{e.Id}@daydeck");
            Line(sb, "DTSTAMP:" + stamp);
            if (e.IsAllDay)
            {
                Line(sb, "DTSTART;VALUE=DATE:" + Date(e.Start));
                Line(sb, "DTEND;VALUE=DATE:" + Date(e.End.AddDays(1))); // ICS의 종일 끝은 다음 날(제외)
            }
            else
            {
                Line(sb, "DTSTART:" + Local(e.Start));
                Line(sb, "DTEND:" + Local(e.End));
            }
            Line(sb, "SUMMARY:" + Escape(e.Title));
            if (!string.IsNullOrEmpty(e.Location))
            {
                Line(sb, "LOCATION:" + Escape(e.Location));
            }
            if (!string.IsNullOrEmpty(e.Notes))
            {
                Line(sb, "DESCRIPTION:" + Escape(e.Notes));
            }
            if (e.Rule is { } rule)
            {
                Line(sb, "RRULE:" + rule);
            }
            if (e.ListName is { } list)
            {
                Line(sb, "CATEGORIES:" + Escape(list));
            }
            Line(sb, "END:VEVENT");
        }

        foreach (var t in tasks)
        {
            Line(sb, "BEGIN:VTODO");
            Line(sb, $"UID:task-{t.Id}@daydeck");
            Line(sb, "DTSTAMP:" + stamp);
            Line(sb, "SUMMARY:" + Escape(t.Title));
            if (t.DueDate is { } due)
            {
                Line(sb, t.DueTime is { } time ? "DUE:" + Local(due.ToDateTime(time)) : "DUE;VALUE=DATE:" + Date(due.ToDateTime(TimeOnly.MinValue)));
            }
            if (!string.IsNullOrEmpty(t.Notes))
            {
                Line(sb, "DESCRIPTION:" + Escape(t.Notes));
            }
            if (t.Priority != Priority.None)
            {
                // ICS 우선순위: 1(높음)~9(낮음)
                Line(sb, "PRIORITY:" + (t.Priority switch { Priority.High => 1, Priority.Medium => 5, _ => 9 }));
            }
            Line(sb, "STATUS:" + (t.Status == ItemStatus.Open ? "NEEDS-ACTION" : t.Status == ItemStatus.Completed ? "COMPLETED" : "CANCELLED"));
            if (t.CompletedAt is { } completed)
            {
                Line(sb, "COMPLETED:" + completed.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            }
            if (t.ListName is { } list)
            {
                Line(sb, "CATEGORIES:" + Escape(list));
            }
            Line(sb, "END:VTODO");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    public static IcsImport Import(string content)
    {
        var events = new List<CalendarEvent>();
        var tasks = new List<TaskItem>();
        var skipped = 0;
        Dictionary<string, (string Value, Dictionary<string, string> Params)>? current = null;
        string? component = null;

        foreach (var line in Unfold(content))
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[6..].Trim().ToUpperInvariant();
                if (name is "VEVENT" or "VTODO")
                {
                    component = name;
                    current = new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);
                }
                else if (component is not null)
                {
                    // VALARM 등 하위 구성 요소는 건너뛴다.
                    component += "/" + name;
                }
                continue;
            }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[4..].Trim().ToUpperInvariant();
                if (component is not null && component.Contains('/'))
                {
                    component = component[..component.LastIndexOf('/')];
                    continue;
                }
                if (current is not null && name == component)
                {
                    try
                    {
                        if (name == "VEVENT" && ToEvent(current) is { } e)
                        {
                            events.Add(e);
                        }
                        else if (name == "VTODO" && ToTask(current) is { } t)
                        {
                            tasks.Add(t);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (FormatException)
                    {
                        skipped++;
                    }
                }
                current = null;
                component = null;
                continue;
            }
            if (current is null || component is null || component.Contains('/'))
            {
                continue;
            }

            var colon = FindValueColon(line);
            if (colon < 0)
            {
                continue;
            }
            var head = line[..colon].Split(';');
            var parameters = head.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].Trim('"'), StringComparer.OrdinalIgnoreCase);
            current.TryAdd(head[0].ToUpperInvariant(), (line[(colon + 1)..], parameters));
        }

        return new IcsImport(events, tasks, skipped);
    }

    private static CalendarEvent? ToEvent(Dictionary<string, (string Value, Dictionary<string, string> Params)> p)
    {
        if (!p.TryGetValue("DTSTART", out var startProp))
        {
            return null;
        }
        var (start, allDay) = ParsePoint(startProp.Value, startProp.Params);
        DateTime end;
        if (p.TryGetValue("DTEND", out var endProp))
        {
            end = ParsePoint(endProp.Value, endProp.Params).Time;
            if (allDay)
            {
                end = end.AddDays(-1); // 다음 날(제외) → 마지막 날(포함)
            }
        }
        else if (p.TryGetValue("DURATION", out var duration) && TryDuration(duration.Value, out var span))
        {
            end = start + span - (allDay ? TimeSpan.FromDays(1) : TimeSpan.Zero);
        }
        else
        {
            end = allDay ? start : start.AddHours(1);
        }
        if (end < start)
        {
            end = start;
        }

        return new CalendarEvent
        {
            Title = Text(p, "SUMMARY") ?? "(제목 없음)",
            Location = Text(p, "LOCATION"),
            Notes = Text(p, "DESCRIPTION"),
            IsAllDay = allDay,
            Start = start,
            End = end,
            RRule = p.TryGetValue("RRULE", out var rule) ? RecurrenceRule.Parse(rule.Value)?.ToString() : null,
            ListName = Text(p, "CATEGORIES")?.Split(',')[0],
        };
    }

    private static TaskItem? ToTask(Dictionary<string, (string Value, Dictionary<string, string> Params)> p)
    {
        var item = new TaskItem { Title = Text(p, "SUMMARY") ?? "(제목 없음)", Notes = Text(p, "DESCRIPTION"), ListName = Text(p, "CATEGORIES")?.Split(',')[0] };
        if (p.TryGetValue("DUE", out var due))
        {
            var (time, allDay) = ParsePoint(due.Value, due.Params);
            item.DueDate = DateOnly.FromDateTime(time);
            item.DueTime = allDay ? null : TimeOnly.FromDateTime(time);
        }
        if (p.TryGetValue("PRIORITY", out var priority) && int.TryParse(priority.Value, out var level) && level > 0)
        {
            item.Priority = level <= 4 ? Priority.High : level == 5 ? Priority.Medium : Priority.Low;
        }
        if (p.TryGetValue("STATUS", out var status) && status.Value.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            item.Status = ItemStatus.Completed;
            item.CompletedAt = p.TryGetValue("COMPLETED", out var completed) ? ParsePoint(completed.Value, completed.Params).Time : DateTime.Now;
        }
        return item;
    }

    private static (DateTime Time, bool AllDay) ParsePoint(string value, Dictionary<string, string> parameters)
    {
        value = value.Trim();
        if (value.Length == 8 || (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase)))
        {
            return (DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture), true);
        }

        var utc = value.EndsWith('Z');
        var local = DateTime.ParseExact(value.TrimEnd('Z')[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc)
        {
            return (DateTime.SpecifyKind(local, DateTimeKind.Utc).ToLocalTime(), false);
        }
        if (parameters.TryGetValue("TZID", out var tzid) && TryFindZone(tzid, out var zone) && zone.Id != TimeZoneInfo.Local.Id)
        {
            return (TimeZoneInfo.ConvertTime(local, zone, TimeZoneInfo.Local), false);
        }
        return (local, false);
    }

    private static bool TryFindZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Local;
            return false;
        }
    }

    private static bool TryDuration(string value, out TimeSpan span)
    {
        try
        {
            span = System.Xml.XmlConvert.ToTimeSpan(value);
            return true;
        }
        catch (FormatException)
        {
            span = default;
            return false;
        }
    }

    private static string? Text(Dictionary<string, (string Value, Dictionary<string, string> Params)> p, string key) =>
        p.TryGetValue(key, out var v) && v.Value.Length > 0 ? Unescape(v.Value) : null;

    /// <summary>값 안의 ':'이 아닌, 이름/매개변수와 값을 나누는 첫 ':' (따옴표 안 제외).</summary>
    private static int FindValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>접힌 줄(다음 줄이 공백/탭으로 시작)을 펼친다.</summary>
    private static IEnumerable<string> Unfold(string content)
    {
        var current = new StringBuilder();
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (current.Length > 0)
            {
                yield return current.ToString();
            }
            current.Clear().Append(raw);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>75바이트마다 접어서 쓴다 (RFC 5545 3.1).</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var bytes = 0;
        var segment = new StringBuilder();
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 74)
            {
                sb.Append(segment).Append("\r\n ");
                segment.Clear();
                bytes = 1;
            }
            segment.Append(rune.ToString());
            bytes += size;
        }
        sb.Append(segment).Append("\r\n");
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static string Date(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Local(DateTime value) => value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
}
