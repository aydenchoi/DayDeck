using System.Globalization;
using System.Text;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Exchange;

/// <summary>할 일을 CSV로 내보낸다 (엑셀에서 한글이 깨지지 않도록 UTF-8 BOM).</summary>
public static class CsvExport
{
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Tasks(IEnumerable<TaskItem> tasks)
    {
        var sb = new StringBuilder();
        sb.Append("제목,목록,마감일,마감 시간,우선순위,상태,완료일,오늘 할 일,메모,만든 날짜\r\n");
        foreach (var t in tasks)
        {
            string[] fields =
            [
                t.Title,
                t.ListName ?? "",
                t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                t.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "",
                t.Priority switch { Priority.High => "높음", Priority.Medium => "보통", Priority.Low => "낮음", _ => "" },
                t.Status switch { ItemStatus.Completed => "완료", ItemStatus.Cancelled => "취소", _ => "미완료" },
                t.CompletedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                t.MyDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                t.Notes ?? "",
                t.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ];
            sb.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
