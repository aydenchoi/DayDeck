using System.Text;
using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

public enum TableAlign
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>
/// GFM 표 (<c>| 머리글 | … |</c> 다음 줄에 <c>| --- | ---: |</c>). <see cref="Rows"/>의 첫 줄이 머리글이고,
/// 칸은 마크다운 글자 그대로라 <see cref="MarkdownDoc.ParseInlines"/>로 읽는다. 모든 줄은 칸 수가 <see cref="Alignments"/>와 같다.
/// 엑셀에서 복사한 내용(탭으로 나뉜 글자)은 <see cref="FromTsv"/>로 표가 된다.
/// </summary>
public sealed partial record MarkdownTable(IReadOnlyList<TableAlign> Alignments, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int ColumnCount => Alignments.Count;

    /// <summary>
    /// 보이는 글자만, 원문과 줄 수를 맞춰서 (구분 줄은 빈 줄): 검색 결과 위치가 원문 줄과 어긋나지 않게.
    /// </summary>
    public string PlainText
    {
        get
        {
            var lines = Rows.Select(row => string.Join("  ", row.Select(MarkdownDoc.ToPlainText))).ToList();
            lines.Insert(Math.Min(1, lines.Count), "");
            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// <paramref name="start"/> 줄에서 표가 시작하면 읽는다. 머리글과 구분 줄 모두 <c>|</c>가 있어야 하고(가로줄 <c>---</c>과 헷갈리지 않게)
    /// 칸 수가 같아야 한다. 이어지는 <c>|</c>가 있는 줄이 표의 줄이다. <paramref name="count"/>는 표가 차지한 줄 수.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> lines, int start, out MarkdownTable? table, out int count)
    {
        table = null;
        count = 0;
        if (start + 1 >= lines.Count || !IsRow(lines[start]) || !lines[start + 1].Contains('|') || !DelimiterRow().IsMatch(lines[start + 1]))
        {
            return false;
        }
        var header = SplitRow(lines[start]);
        var alignments = SplitRow(lines[start + 1]).Select(ParseAlign).ToList();
        if (header.Count != alignments.Count)
        {
            return false;
        }
        var rows = new List<IReadOnlyList<string>> { header };
        var i = start + 2;
        for (; i < lines.Count && IsRow(lines[i]); i++)
        {
            rows.Add(Fit(SplitRow(lines[i]), alignments.Count));
        }
        table = new MarkdownTable(alignments, rows);
        count = i - start;
        return true;
    }

    /// <summary>한 칸의 마크다운 (줄이 짧으면 빈 칸).</summary>
    public string Cell(int row, int column) => column < Rows[row].Count ? Rows[row][column] : "";

    /// <summary>이 칸만 바꾼 표.</summary>
    public MarkdownTable WithCell(int row, int column, string cell)
    {
        var rows = Rows.Select(r => Fit(r, ColumnCount).ToList()).ToList();
        rows[row][column] = cell;
        return this with { Rows = rows };
    }

    /// <summary>
    /// "보기"에서 칸을 고칠 때 보일 글자: 표 문법 때문에 붙인 <c>\|</c>는 <c>|</c>로, <c>&lt;br&gt;</c>는 줄바꿈으로.
    /// 다른 서식 기호(<c>**</c>, 링크 등)는 그대로 둔다 (고쳐도 서식이 없어지지 않게).
    /// </summary>
    public static string CellEditText(string cell) => LineBreakTag().Replace(cell.Replace("\\|", "|"), "\n");

    /// <summary><see cref="CellEditText"/>로 고친 글자를 칸 마크다운으로: 줄바꿈은 <c>&lt;br&gt;</c>, <c>|</c>는 <c>\|</c>.</summary>
    public static string CellFromEditText(string text) =>
        UnescapedPipe().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), "\\|").Replace("\n", "<br>");

    /// <summary>마크다운 줄들 (머리글, 구분 줄, 내용).</summary>
    public IReadOnlyList<string> Write()
    {
        var lines = new List<string>(Rows.Count + 1);
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(c => c.Trim())) + " |";
        for (var i = 0; i < Rows.Count; i++)
        {
            lines.Add(Row(Fit(Rows[i], ColumnCount)));
            if (i == 0)
            {
                lines.Add(Row(Alignments.Select(a => a switch
                {
                    TableAlign.Left => ":---",
                    TableAlign.Center => ":---:",
                    TableAlign.Right => "---:",
                    _ => "---",
                })));
            }
        }
        return lines;
    }

    /// <summary>
    /// 엑셀·구글 시트·워드 표를 복사한 글자(칸은 탭, 줄은 줄바꿈, 칸 안에 탭·줄바꿈·따옴표가 있으면 "…"로 감쌈)를 표로.
    /// 첫 줄이 머리글이 되고, 숫자만 있는 칸은 오른쪽 정렬. 칸이 하나뿐(줄 하나·칸 하나)이거나 줄마다 칸 수가 다르면 null.
    /// </summary>
    public static MarkdownTable? FromTsv(string text)
    {
        var rows = ReadTsv(text.Replace("\r\n", "\n"));
        while (rows.Count > 0 && rows[^1].All(c => c.Trim().Length == 0))
        {
            rows.RemoveAt(rows.Count - 1);
        }
        if (rows.Count == 0)
        {
            return null;
        }
        var columns = rows[0].Count;
        if (rows.Any(r => r.Count != columns) || (columns < 2 && rows.Count < 2))
        {
            return null;
        }
        var alignments = Enumerable.Range(0, columns).Select(c =>
        {
            var values = rows.Skip(1).Select(r => r[c].Trim()).Where(v => v.Length > 0).ToList();
            return values.Count > 0 && values.All(v => Number().IsMatch(v)) ? TableAlign.Right : TableAlign.None;
        }).ToList();
        return new MarkdownTable(alignments, rows.Select(r => (IReadOnlyList<string>)r.Select(CellMarkdown).ToList()).ToList());
    }

    /// <summary>글자 그대로 보이도록 서식 기호와 <c>|</c>를 이스케이프하고, 칸 안의 줄바꿈은 &lt;br&gt;로.</summary>
    private static string CellMarkdown(string cell) =>
        MarkdownLines.Escape(cell.Trim()).Replace("|", "\\|").Replace("\n", "<br>");

    private static List<List<string>> ReadTsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var i = 0;
        while (i <= text.Length)
        {
            // 따옴표로 감싼 칸: "" 는 따옴표 하나
            if (cell.Length == 0 && i < text.Length && text[i] == '"' && ClosingQuote(text, i) is var close and > 0)
            {
                cell.Append(text[(i + 1)..close].Replace("\"\"", "\""));
                i = close + 1;
                continue;
            }
            var c = i < text.Length ? text[i] : '\n';
            if (c == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                if (i == text.Length - 1)
                {
                    break; // 엑셀은 마지막 줄 뒤에도 줄바꿈을 붙인다
                }
            }
            else
            {
                cell.Append(c);
            }
            i++;
        }
        return rows;
    }

    /// <summary>
    /// 칸 처음의 따옴표를 닫는 따옴표 위치. 그 뒤가 칸 끝(탭, 줄바꿈, 글 끝)이어야 감싼 칸이다 (아니면 0: 따옴표도 글자).
    /// </summary>
    private static int ClosingQuote(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == '"')
            {
                i++;
                continue;
            }
            return i + 1 == text.Length || text[i + 1] is '\t' or '\n' ? i : 0;
        }
        return 0;
    }

    private static bool IsRow(string line) => line.Trim().Length > 0 && UnescapedPipe().IsMatch(line);

    /// <summary>앞뒤 <c>|</c>를 떼고 이스케이프되지 않은 <c>|</c>로 나눈다 (칸 안의 <c>\|</c>는 그대로 둔다).</summary>
    private static List<string> SplitRow(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|'))
        {
            text = text[1..];
        }
        if (text.EndsWith('|') && !text.EndsWith("\\|", StringComparison.Ordinal))
        {
            text = text[..^1];
        }
        return [.. UnescapedPipe().Split(text).Select(c => c.Trim())];
    }

    private static IReadOnlyList<string> Fit(IReadOnlyList<string> cells, int count) =>
        cells.Count == count ? cells : [.. cells.Take(count), .. Enumerable.Repeat("", Math.Max(0, count - cells.Count))];

    private static TableAlign ParseAlign(string cell) =>
        (cell.StartsWith(':'), cell.EndsWith(':')) switch
        {
            (true, true) => TableAlign.Center,
            (true, false) => TableAlign.Left,
            (false, true) => TableAlign.Right,
            _ => TableAlign.None,
        };

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex DelimiterRow();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    /// <summary>숫자 칸 (1,234 / -5.5 / 12% / ₩3,000 / $1.5).</summary>
    [GeneratedRegex(@"^[-+]?[₩$€¥£]?\s?[-+]?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?\s?%?$")]
    private static partial Regex Number();
}
