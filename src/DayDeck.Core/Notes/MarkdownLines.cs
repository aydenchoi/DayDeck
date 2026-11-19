using System.Text;
using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

public enum LineKind
{
    Text,
    Blank,
    Heading,
    Bullet,
    Numbered,
    Task,
    Quote,
    Code,
    Rule,
    Table,
}

/// <summary>
/// 마크다운 한 줄. 메모의 "보기"에서 바로 고칠 때 문단 하나가 한 줄이다.
/// <see cref="Level"/>은 제목 수준(1~6) 또는 목록 들여쓰기(0부터), <see cref="Language"/>는 코드 블록 첫 줄의 언어 표시.
/// 표는 예외로 여러 줄(<see cref="Table"/>)을 한 항목으로 둔다.
/// </summary>
public sealed record MarkdownLine(LineKind Kind, IReadOnlyList<InlineRun> Inlines, int Level = 0, int Number = 0, bool Checked = false, string? Language = null,
    MarkdownTable? Table = null)
{
    public string PlainText => Table?.PlainText ?? string.Concat(Inlines.Select(i => i.Text));

    public static MarkdownLine Blank { get; } = new(LineKind.Blank, []);
}

/// <summary>
/// 마크다운 ↔ 줄 목록. 줄 구조(빈 줄 포함)를 그대로 지키므로 <c>Write(Parse(md))</c>는 원문과 같다
/// (서식 표시만 표준 모양으로 정리될 수 있다: "* 항목" → "- 항목", &lt;b&gt; → **). 모르는 문법은 글자 그대로 남긴다.
/// </summary>
public static partial class MarkdownLines
{
    public static IReadOnlyList<MarkdownLine> Parse(string markdown)
    {
        var lines = new List<MarkdownLine>();
        var source = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < source.Length; i++)
        {
            var line = source[i];
            if (MarkdownDoc.Fence().Match(line) is { Success: true } fence)
            {
                var marker = fence.Groups["fence"].Value;
                var language = line.Trim()[marker.Length..].Trim();
                var first = true;
                for (i++; i < source.Length && !source[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                {
                    lines.Add(new MarkdownLine(LineKind.Code, [new InlineRun(source[i])], Language: first ? language : null));
                    first = false;
                }
                if (first)
                {
                    lines.Add(new MarkdownLine(LineKind.Code, [], Language: language)); // 빈 코드 블록
                }
                continue;
            }
            if (MarkdownTable.TryParse(source, i, out var table, out var count))
            {
                lines.Add(new MarkdownLine(LineKind.Table, [], Table: table));
                i += count - 1;
                continue;
            }
            lines.Add(ParseLine(line));
        }
        return lines;
    }

    private static MarkdownLine ParseLine(string line)
    {
        if (line.Trim().Length == 0)
        {
            return MarkdownLine.Blank;
        }
        if (MarkdownDoc.Rule().IsMatch(line))
        {
            return new MarkdownLine(LineKind.Rule, []);
        }
        if (MarkdownDoc.Heading().Match(line) is { Success: true } heading)
        {
            // 닫는 #은 앞에 공백이 있을 때만 뺀다 ("# C#"의 #은 글자).
            var title = ClosingHashes().Replace(heading.Groups["text"].Value, "");
            return new MarkdownLine(LineKind.Heading, MarkdownDoc.ParseInlines(title), heading.Groups["level"].Value.Length);
        }
        if (MarkdownDoc.ListLine().Match(line) is { Success: true } item)
        {
            var level = item.Groups["indent"].Value.Replace("\t", "    ").Length / 2;
            var text = MarkdownDoc.ParseInlines(item.Groups["text"].Value);
            if (item.Groups["check"].Success)
            {
                return new MarkdownLine(LineKind.Task, text, level, Checked: item.Groups["check"].Value.Contains('x', StringComparison.OrdinalIgnoreCase));
            }
            return item.Groups["number"].Success
                ? new MarkdownLine(LineKind.Numbered, text, level, int.Parse(item.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
                : new MarkdownLine(LineKind.Bullet, text, level);
        }
        if (MarkdownDoc.Quote().Match(line) is { Success: true } quote)
        {
            return new MarkdownLine(LineKind.Quote, MarkdownDoc.ParseInlines(quote.Groups["text"].Value));
        }
        return new MarkdownLine(LineKind.Text, MarkdownDoc.ParseInlines(line));
    }

    /// <summary>
    /// "보기"에 보이는 글자만 (검색·미리보기용): 서식 기호, 태그, 목록 기호, 링크 주소, 그림 경로를 빼고 줄 구조는 지킨다.
    /// 그림은 설명 글자만 남는다.
    /// </summary>
    public static string ToPlainText(string markdown) =>
        string.Join("\n", Parse(markdown).Select(l => l.Kind == LineKind.Rule ? "" : l.PlainText));

    public static string Write(IEnumerable<MarkdownLine> lines)
    {
        var output = new List<string>();
        var inCode = false;
        foreach (var line in lines)
        {
            if (line.Kind == LineKind.Code)
            {
                if (!inCode || line.Language is not null)
                {
                    if (inCode)
                    {
                        output.Add("```");
                    }
                    output.Add("```" + (line.Language ?? ""));
                    inCode = true;
                }
                if (line.Inlines.Count > 0)
                {
                    output.Add(line.PlainText);
                }
                continue;
            }
            if (inCode)
            {
                output.Add("```");
                inCode = false;
            }
            output.Add(WriteLine(line));
        }
        if (inCode)
        {
            output.Add("```");
        }
        return string.Join("\n", output);
    }

    public static string WriteLine(MarkdownLine line)
    {
        var indent = new string(' ', Math.Max(0, line.Level) * 2);
        var text = WriteInlines(line.Inlines);
        return line.Kind switch
        {
            LineKind.Blank => "",
            LineKind.Rule => "---",
            LineKind.Table => line.Table is { } table ? string.Join("\n", table.Write()) : "",
            LineKind.Heading => new string('#', Math.Clamp(line.Level, 1, 6)) + " " + text,
            LineKind.Bullet => indent + "- " + EscapeCheckBox(text),
            LineKind.Numbered => indent + $"{Math.Max(0, line.Number)}. " + EscapeCheckBox(text),
            LineKind.Task => indent + (line.Checked ? "- [x] " : "- [ ] ") + text,
            LineKind.Quote => "> " + text,
            LineKind.Code => line.PlainText,
            _ => text.Trim().Length == 0 ? "" : EscapeLineStart(text),
        };
    }

    /// <summary>
    /// 글자 조각들을 마크다운으로. 서식 표시는 바깥에서 안쪽 순서(색 › 형광펜 › 밑줄 › 취소선 › 굵게 › 기울임 › 코드)로 겹치고,
    /// 표시 바로 안쪽에 공백이 오지 않도록 공백은 표시 밖으로 뺀다 (그래야 다시 읽을 때 같은 서식이 된다).
    /// </summary>
    public static string WriteInlines(IEnumerable<InlineRun> runs)
    {
        var output = new StringBuilder();
        var open = new List<Marker>();
        var pending = new StringBuilder(); // 표시 밖으로 미뤄 둔 공백

        void Transition(IReadOnlyList<Marker> wanted)
        {
            var common = 0;
            while (common < open.Count && common < wanted.Count && open[common] == wanted[common])
            {
                common++;
            }
            for (var i = open.Count - 1; i >= common; i--)
            {
                output.Append(open[i].Close);
            }
            open.RemoveRange(common, open.Count - common);
            output.Append(pending);
            pending.Clear();
            for (var i = common; i < wanted.Count; i++)
            {
                output.Append(wanted[i].Open);
                open.Add(wanted[i]);
            }
        }

        foreach (var group in Group(Merge(runs)))
        {
            if (group.Link is { } link)
            {
                // 링크: 바깥 서식을 닫고 [이름](주소). 이름 안의 서식은 그 안에서 다시 쓴다.
                var label = string.Concat(group.Runs.Select(r => r.Text));
                if (group.Runs.All(r => r.Style == InlineStyle.None && r.Color is null) && IsBareUrl(label, link))
                {
                    Transition([]);
                    output.Append(label);
                    continue;
                }
                Transition([]);
                output.Append('[').Append(WriteInlines(group.Runs.Select(r => r with { Link = null })).Replace("]", "\\]")).Append("](")
                    .Append(link.Replace(" ", "%20").Replace(")", "%29")).Append(')');
                continue;
            }

            foreach (var run in group.Runs)
            {
                if (run.Image is { } image)
                {
                    Transition(Markers(run with { Style = run.Style & ~InlineStyle.Code }));
                    output.Append("![").Append(Escape(run.Text).Replace("]", "\\]"));
                    if (run.ImageWidth is { } width)
                    {
                        output.Append('|').Append(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    output.Append("](").Append(image.Replace(" ", "%20").Replace(")", "%29")).Append(')');
                    continue;
                }
                var text = run.Text;
                var lead = text.Length - text.TrimStart().Length;
                var trail = text.Length - text.TrimEnd().Length;
                if (lead == text.Length)
                {
                    pending.Append(EscapeBreaks(text));
                    continue;
                }
                pending.Append(EscapeBreaks(text[..lead]));
                var core = text[lead..^trail];
                var style = run.Style;
                if (style.HasFlag(InlineStyle.Code) && (core.Contains('`') || core.Contains('\n')))
                {
                    style &= ~InlineStyle.Code; // 코드 표시 안에 ` 나 줄바꿈은 쓸 수 없다.
                }
                Transition(Markers(run with { Style = style }));
                output.Append(style.HasFlag(InlineStyle.Code) ? core : EscapeBreaks(Escape(core)));
                pending.Append(EscapeBreaks(text[^trail..]));
            }
        }
        Transition([]);
        return output.ToString();
    }

    private sealed record Marker(string Open, string Close);

    private static readonly Marker HighlightMarker = new("<mark>", "</mark>");
    private static readonly Marker UnderlineMarker = new("<u>", "</u>");
    private static readonly Marker StrikeMarker = new("~~", "~~");
    private static readonly Marker BoldMarker = new("**", "**");
    private static readonly Marker ItalicMarker = new("*", "*");
    private static readonly Marker CodeMarker = new("`", "`");

    private static List<Marker> Markers(InlineRun run)
    {
        var markers = new List<Marker>();
        if (run.Color is { } color)
        {
            markers.Add(new Marker($"<span style=\"color:{color}\">", "</span>"));
        }
        if (run.Style.HasFlag(InlineStyle.Highlight))
        {
            markers.Add(HighlightMarker);
        }
        if (run.Style.HasFlag(InlineStyle.Underline))
        {
            markers.Add(UnderlineMarker);
        }
        if (run.Style.HasFlag(InlineStyle.Strike))
        {
            markers.Add(StrikeMarker);
        }
        if (run.Style.HasFlag(InlineStyle.Bold))
        {
            markers.Add(BoldMarker);
        }
        if (run.Style.HasFlag(InlineStyle.Italic))
        {
            markers.Add(ItalicMarker);
        }
        if (run.Style.HasFlag(InlineStyle.Code))
        {
            markers.Add(CodeMarker);
        }
        return markers;
    }

    /// <summary>같은 서식이 이어지는 조각은 하나로 (그림은 따로).</summary>
    private static List<InlineRun> Merge(IEnumerable<InlineRun> runs)
    {
        var merged = new List<InlineRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0 && run.Image is null)
            {
                continue;
            }
            if (merged.Count > 0 && merged[^1] is var last && last.Image is null && run.Image is null
                && last.Style == run.Style && last.Color == run.Color && last.Link == run.Link)
            {
                merged[^1] = last with { Text = last.Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }
        return merged;
    }

    private sealed record RunGroup(string? Link, List<InlineRun> Runs);

    private static List<RunGroup> Group(List<InlineRun> runs)
    {
        var groups = new List<RunGroup>();
        foreach (var run in runs)
        {
            if (groups.Count > 0 && groups[^1].Link == run.Link)
            {
                groups[^1].Runs.Add(run);
            }
            else
            {
                groups.Add(new RunGroup(run.Link, [run]));
            }
        }
        return groups;
    }

    private static bool IsBareUrl(string label, string link) =>
        MarkdownDoc.BareUrl().Match(label) is { Success: true } bare && bare.Index == 0 && bare.Length == label.Length
        && label.TrimEnd('.', ',', ')', ';', ':', '!', '?').Length == label.Length
        && (link == label || link == "https://" + label);

    /// <summary>목록 글자가 "[ ] "로 시작하면 체크 목록으로 읽히지 않게 한다.</summary>
    private static string EscapeCheckBox(string text) =>
        text.Length >= 4 && text[0] == '[' && text[2] == ']' && text[1] is ' ' or 'x' or 'X' && char.IsWhiteSpace(text[3]) ? "\\" + text : text;

    private static string EscapeBreaks(string text) => text.Replace("\n", "<br>");

    /// <summary>글자 그대로 남아야 하는 기호 앞에 \ 를 붙인다 (다시 읽을 때 서식으로 바뀌지 않도록).</summary>
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var escape = c switch
            {
                '*' or '`' => true,
                '\\' => i + 1 < text.Length && @"\`*_{}[]()#+-.!~<>|".Contains(text[i + 1]),
                '~' => (i + 1 < text.Length && text[i + 1] == '~') || (i > 0 && text[i - 1] == '~'),
                '[' => MarkdownDoc.LinkAt().Match(text, i) is { Success: true } link && link.Index == i,
                '<' => MarkdownDoc.Tag().Match(text, i) is { Success: true } tag && tag.Index == i && IsFormattingTag(tag),
                _ => false,
            };
            if (escape)
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>읽을 때 서식으로 바뀌는 태그인지 (모르는 태그는 원래 글자로 남으므로 이스케이프하지 않는다).</summary>
    private static bool IsFormattingTag(Match tag) =>
        tag.Groups["name"].Value.ToLowerInvariant() switch
        {
            "u" or "mark" or "b" or "strong" or "i" or "em" or "s" or "del" or "br" => true,
            "span" => !tag.Groups["close"].Success && MarkdownDoc.ColorStyle().IsMatch(tag.Groups["attrs"].Value),
            _ => false,
        };

    /// <summary>보통 줄이 제목·목록·인용·가로줄·코드 시작처럼 읽히지 않도록 첫 기호를 이스케이프한다.</summary>
    private static string EscapeLineStart(string line)
    {
        var start = line.Length - line.TrimStart().Length;
        if (MarkdownDoc.ListLine().Match(line) is { Success: true } item && item.Groups["number"].Success)
        {
            var dot = start + item.Groups["number"].Length;
            return line[..dot] + "\\" + line[dot..]; // "1. 항목" → "1\. 항목"
        }
        var looksLikeBlock = MarkdownDoc.Heading().IsMatch(line) || MarkdownDoc.ListLine().IsMatch(line) || MarkdownDoc.Quote().IsMatch(line)
                             || MarkdownDoc.Rule().IsMatch(line) || MarkdownDoc.Fence().IsMatch(line);
        return looksLikeBlock ? line[..start] + "\\" + line[start..] : line;
    }

    [GeneratedRegex(@"(^|\s+)#+\s*$")]
    private static partial Regex ClosingHashes();
}
