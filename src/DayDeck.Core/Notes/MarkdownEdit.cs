using System.Text;
using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

/// <summary>
/// 편집기에 적용할 변경 한 번: [<see cref="Start"/>, +<see cref="Length"/>)를 <see cref="Replacement"/>로 바꾸고
/// 선택을 (<see cref="SelectionStart"/>, <see cref="SelectionLength"/>)로 둔다.
/// 편집기는 이 범위만 바꾸므로 실행 취소 한 번으로 되돌릴 수 있다.
/// </summary>
public readonly record struct TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength)
{
    public string ApplyTo(string text) => text[..Start] + Replacement + text[(Start + Length)..];
}

/// <summary>줄 단위 서식.</summary>
public enum LineStyle
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Numbered,
    Task,
    Quote,
}

/// <summary>
/// 메모 편집기의 서식 도구 (굵게, 색, 링크, 제목, 목록 …). 메모는 마크다운 파일로 저장하므로 서식도 마크다운
/// (색·밑줄·형광펜은 마크다운에 없어 HTML 태그)로 쓴다. 다른 편집기(VS Code, Obsidian 등)에서도 그대로 읽힌다.
/// 모든 동작은 같은 버튼을 다시 누르면 풀린다 (토글).
/// </summary>
public static partial class MarkdownEdit
{
    public const string Bold = "**";
    public const string Italic = "*";

    // ---------- 글자 서식 ----------

    /// <summary>
    /// 선택을 표시로 감싸거나 이미 감싸져 있으면 푼다. 선택이 없으면 표시 한 쌍을 넣고 커서를 가운데에 둔다.
    /// 여러 줄을 고르면 줄마다 (목록 기호·제목 기호 뒤의 내용만) 감싼다.
    /// </summary>
    public static TextEdit ToggleInline(string text, int start, int length, string open, string close)
    {
        (start, length) = Clamp(text, start, length);
        if (length == 0)
        {
            if (IsStar(open))
            {
                // 커서가 **|** 처럼 빈 표시 사이에 있으면 지운다.
                if (Surrounded(text, start, start, open, open))
                {
                    return new TextEdit(start - open.Length, open.Length * 2, "", start - open.Length, 0);
                }
            }
            else if (Surrounded(text, start, start, open, close))
            {
                return new TextEdit(start - open.Length, open.Length + close.Length, "", start - open.Length, 0);
            }
            // 서식 안에서 글자를 친 뒤 다시 누르면(커서가 닫는 표시 바로 앞) 표시 밖으로 나간다 (워드·옵시디언처럼).
            if (ClosesBefore(text, start, open, close))
            {
                return new TextEdit(start, 0, "", start + close.Length, 0);
            }
            return new TextEdit(start, 0, open + close, start + open.Length, 0);
        }

        var selected = text.Substring(start, length);
        if (selected.Contains('\n'))
        {
            return ToggleLines(text, start, length, open, close);
        }
        return IsStar(open) ? ToggleStars(text, start, length, open.Length) : TogglePair(text, start, length, open, close);
    }

    /// <summary>커서 바로 뒤가 닫는 표시이고, 같은 줄 앞에 그 서식을 연 표시(사이에 글자가 있음)가 있는지.</summary>
    private static bool ClosesBefore(string text, int caret, string open, string close)
    {
        if (!text.AsSpan(caret).StartsWith(close, StringComparison.Ordinal))
        {
            return false;
        }
        // "**"의 닫기에서 "*" 하나만 보고 기울임으로 착각하지 않게 한다.
        if (open == "*" && caret + 1 < text.Length && text[caret + 1] == '*')
        {
            return false;
        }
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var before = text[lineStart..caret];
        var openAt = before.LastIndexOf(open, StringComparison.Ordinal);
        if (openAt < 0 || openAt + open.Length >= before.Length)
        {
            return false;
        }
        // 같은 표시 문자로 이루어진 "***" 등에서는 여는 쪽 개수가 짝이 맞아야 한다: 앞쪽 표시 수가 홀수 번(열린 상태)일 때만.
        if (open == close)
        {
            var count = 0;
            for (var i = before.IndexOf(open, StringComparison.Ordinal); i >= 0; i = before.IndexOf(open, i + open.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count % 2 == 1;
        }
        return before.LastIndexOf(close, StringComparison.Ordinal) < openAt;
    }

    /// <summary>글자색. <paramref name="hex"/>가 null이면 색을 지운다. 이미 색이 있으면 바꾼다.</summary>
    public static TextEdit SetColor(string text, int start, int length, string? hex)
    {
        (start, length) = Clamp(text, start, length);
        var selected = text.Substring(start, length);

        // 선택 자체가 <span style="color:…">…</span>
        if (ColorSpan().Match(selected) is { Success: true } inside && inside.Length == selected.Length)
        {
            var content = inside.Groups["content"].Value;
            return hex is null
                ? new TextEdit(start, length, content, start, content.Length)
                : Wrap(start, length, content, ColorOpen(hex), SpanClose);
        }

        // 선택 바깥이 색 표시
        if (OpenColorBefore(text, start) is { } before && text.AsSpan(start + length).StartsWith(SpanClose))
        {
            var openStart = start - before.Length;
            if (hex is null)
            {
                return new TextEdit(openStart, before.Length + length + SpanClose.Length, selected, openStart, length);
            }
            var open = ColorOpen(hex);
            return new TextEdit(openStart, before.Length, open, openStart + open.Length, length);
        }

        if (hex is null)
        {
            return new TextEdit(start, 0, "", start, length);
        }
        if (length == 0)
        {
            return new TextEdit(start, 0, ColorOpen(hex) + SpanClose, start + ColorOpen(hex).Length, 0);
        }
        return Wrap(start, length, selected, ColorOpen(hex), SpanClose);
    }

    /// <summary>링크 [이름](주소). 선택한 글자가 있으면 그 글자가 이름이 된다.</summary>
    public static TextEdit InsertLink(string text, int start, int length, string? label, string url)
    {
        (start, length) = Clamp(text, start, length);
        var name = string.IsNullOrWhiteSpace(label) ? (length > 0 ? text.Substring(start, length) : url) : label.Trim();
        name = name.Replace("[", "\\[").Replace("]", "\\]");
        var target = url.Trim().Replace(" ", "%20").Replace(")", "%29");
        var link = $"[{name}]({target})";
        return new TextEdit(start, length, link, start + link.Length, 0);
    }

    /// <summary>
    /// 그림 <c>![](경로)</c>를 넣는다. 줄 가운데면 앞뒤로 줄을 바꿔 그림이 한 줄을 차지하게 하고, 커서는 그림 다음 줄로 간다.
    /// 여러 장이면 한 줄에 하나씩.
    /// </summary>
    public static TextEdit InsertImages(string text, int start, int length, IReadOnlyList<string> paths)
    {
        (start, length) = Clamp(text, start, length);
        var body = string.Join("\n", paths.Select(p => $"![]({p.Trim().Replace(" ", "%20").Replace(")", "%29")})"));
        var before = start > 0 && text[start - 1] != '\n' ? "\n" : "";
        var end = start + length;
        var after = end < text.Length && text[end] == '\n' ? "" : "\n";
        var replacement = before + body + after;
        var caret = start + replacement.Length + (after.Length == 0 ? 1 : 0);
        return new TextEdit(start, length, replacement, Math.Min(caret, text.Length - length + replacement.Length), 0);
    }

    /// <summary>
    /// 여러 줄 블록(표 등)을 넣는다. 앞뒤 글과 빈 줄 하나로 떨어지게 하고(표가 앞뒤 줄과 붙지 않도록), 커서는 블록 다음 줄로 간다.
    /// </summary>
    public static TextEdit InsertBlock(string text, int start, int length, string block)
    {
        (start, length) = Clamp(text, start, length);
        var end = start + length;
        var before = start == 0 || text[..start].EndsWith("\n\n", StringComparison.Ordinal) ? "" : text[start - 1] == '\n' ? "\n" : "\n\n";
        var after = end == text.Length ? "\n" : text[end..].StartsWith("\n\n", StringComparison.Ordinal) ? "" : text[end] == '\n' ? "\n" : "\n\n";
        var replacement = before + block + after;
        var caret = Math.Min(start + before.Length + block.Length + 2, text.Length - length + replacement.Length);
        return new TextEdit(start, length, replacement, caret, 0);
    }

    /// <summary>주소처럼 보이면 그대로, "www."로 시작하면 https://를 붙인다. 쓸 수 없는 값이면 null.</summary>
    public static string? NormalizeUrl(string input)
    {
        var url = input.Trim();
        if (url.Length == 0)
        {
            return null;
        }
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        if (url.Contains('@') && !url.Contains(':') && !url.Contains('/'))
        {
            url = "mailto:" + url;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsSafeScheme(uri) ? url : null;
    }

    /// <summary>미리보기에서 열어도 되는 주소 (웹, 메일). file:, javascript: 같은 것은 열지 않는다.</summary>
    public static bool IsSafeScheme(Uri uri) => uri.Scheme is "http" or "https" or "mailto";

    // ---------- 줄 서식 ----------

    /// <summary>
    /// 선택한 줄들(커서만 있으면 그 줄)을 제목/목록/인용으로. 모든 줄이 이미 그 서식이면 보통 문단으로 되돌린다.
    /// 번호 목록은 1부터 다시 매긴다. 들여쓰기는 유지한다.
    /// </summary>
    public static TextEdit SetLineStyle(string text, int start, int length, LineStyle style)
    {
        (start, length) = Clamp(text, start, length);
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var endIndex = start + length;
        if (length > 0 && endIndex > lineStart && text[endIndex - 1] == '\n')
        {
            endIndex--; // 다음 줄 첫머리까지 고른 것은 그 줄을 포함하지 않는다.
        }
        var lineEnd = text.IndexOf('\n', endIndex);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var lines = text[lineStart..lineEnd].Split('\n');
        var parsed = lines.Select(ParseLinePrefix).ToList();
        var nonEmpty = parsed.Where(p => p.Content.Trim().Length > 0 || p.Style != LineStyle.Paragraph).ToList();
        var allSame = nonEmpty.Count > 0 && nonEmpty.All(p => p.Style == style);
        var target = allSame ? LineStyle.Paragraph : style;

        var sb = new StringBuilder();
        var number = 1;
        for (var i = 0; i < parsed.Count; i++)
        {
            var (indent, current, checkedTask, content) = parsed[i];
            if (i > 0)
            {
                sb.Append('\n');
            }
            if (content.Trim().Length == 0 && current == LineStyle.Paragraph)
            {
                sb.Append(lines[i]); // 빈 줄은 그대로
                continue;
            }
            sb.Append(indent).Append(Prefix(target, number++, checkedTask && target == LineStyle.Task)).Append(content);
        }

        var replacement = sb.ToString();
        // 한 줄에 커서만 있었으면 커서를 줄 끝(내용 끝)으로, 아니면 바뀐 줄 전체를 고른다.
        return length == 0 && lines.Length == 1
            ? new TextEdit(lineStart, lineEnd - lineStart, replacement, lineStart + replacement.Length, 0)
            : new TextEdit(lineStart, lineEnd - lineStart, replacement, lineStart, replacement.Length);
    }

    /// <summary>줄의 서식 (커서가 있는 줄 표시용).</summary>
    public static LineStyle GetLineStyle(string line) => ParseLinePrefix(line).Style;

    private static string Prefix(LineStyle style, int number, bool checkedTask) => style switch
    {
        LineStyle.Heading1 => "# ",
        LineStyle.Heading2 => "## ",
        LineStyle.Heading3 => "### ",
        LineStyle.Bullet => "- ",
        LineStyle.Numbered => $"{number}. ",
        LineStyle.Task => checkedTask ? "- [x] " : "- [ ] ",
        LineStyle.Quote => "> ",
        _ => "",
    };

    private static (string Indent, LineStyle Style, bool Checked, string Content) ParseLinePrefix(string line)
    {
        var match = LinePrefix().Match(line);
        var indent = match.Groups["indent"].Value;
        var content = line[match.Length..];
        LineStyle style;
        var isChecked = false;
        if (match.Groups["heading"].Success)
        {
            style = match.Groups["heading"].Value.Length switch { 1 => LineStyle.Heading1, 2 => LineStyle.Heading2, _ => LineStyle.Heading3 };
        }
        else if (match.Groups["task"].Success)
        {
            style = LineStyle.Task;
            isChecked = match.Groups["task"].Value.Contains('x', StringComparison.OrdinalIgnoreCase);
        }
        else if (match.Groups["bullet"].Success)
        {
            style = LineStyle.Bullet;
        }
        else if (match.Groups["number"].Success)
        {
            style = LineStyle.Numbered;
        }
        else if (match.Groups["quote"].Success)
        {
            style = LineStyle.Quote;
        }
        else
        {
            style = LineStyle.Paragraph;
        }
        return (indent, style, isChecked, content);
    }

    // ---------- 내부 ----------

    private const string SpanClose = "</span>";

    private static string ColorOpen(string hex) => $"<span style=\"color:{hex.ToUpperInvariant()}\">";

    private static bool IsStar(string marker) => marker is Bold or Italic;

    private static (int, int) Clamp(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        return (start, Math.Clamp(length, 0, text.Length - start));
    }

    private static TextEdit Wrap(int start, int length, string content, string open, string close) =>
        new(start, length, open + content + close, start + open.Length, content.Length);

    private static bool Surrounded(string text, int start, int end, string open, string close) =>
        start >= open.Length && text.AsSpan(start - open.Length, open.Length).SequenceEqual(open)
        && text.AsSpan(end).StartsWith(close);

    private static TextEdit TogglePair(string text, int start, int length, string open, string close)
    {
        var selected = text.Substring(start, length);
        if (selected.Length >= open.Length + close.Length && selected.StartsWith(open, StringComparison.Ordinal)
            && selected.EndsWith(close, StringComparison.Ordinal))
        {
            var inner = selected[open.Length..^close.Length];
            return new TextEdit(start, length, inner, start, inner.Length);
        }
        if (Surrounded(text, start, start + length, open, close))
        {
            return new TextEdit(start - open.Length, open.Length + length + close.Length, selected, start - open.Length, length);
        }
        return Wrap(start, length, selected, open, close);
    }

    /// <summary>
    /// 별표(굵게 **, 기울임 *)는 겹칠 수 있다 (***굵게+기울임***). 내용 양쪽의 별 개수 k로 판단한다:
    /// k가 홀수면 기울임, 2 이상이면 굵게. 해당 서식만 더하거나 뺀다.
    /// </summary>
    private static TextEdit ToggleStars(string text, int start, int length, int markerLength)
    {
        var selected = text.Substring(start, length);
        // 선택 안쪽 양 끝의 별
        var leadingInside = selected.TakeWhile(c => c == '*').Count();
        var trailingInside = selected.Reverse().TakeWhile(c => c == '*').Count();
        var inside = Math.Min(leadingInside, trailingInside);
        if (inside * 2 >= selected.Length)
        {
            inside = 0; // 별만 고른 경우
        }
        var contentStart = start + inside;
        var contentEnd = start + length - inside;

        // 선택 바깥 양쪽의 별
        var before = 0;
        while (start - before - 1 >= 0 && text[start - before - 1] == '*')
        {
            before++;
        }
        var after = 0;
        while (start + length + after < text.Length && text[start + length + after] == '*')
        {
            after++;
        }
        var outside = Math.Min(before, after);
        var stars = inside + outside;

        var hasItalic = stars % 2 == 1;
        var hasBold = stars >= 2;
        var newStars = markerLength == 2
            ? (hasBold ? stars - 2 : stars + 2)
            : (hasItalic ? stars - 1 : stars + 1);

        var content = text[contentStart..contentEnd];
        var marks = new string('*', newStars);
        var replaceStart = contentStart - stars;
        return new TextEdit(replaceStart, contentEnd - contentStart + stars * 2, marks + content + marks, replaceStart + newStars, content.Length);
    }

    /// <summary>여러 줄: 줄마다 목록/제목 기호 뒤 내용을 감싼다. 모두 이미 감싸져 있으면 푼다.</summary>
    private static TextEdit ToggleLines(string text, int start, int length, string open, string close)
    {
        var lines = text.Substring(start, length).Split('\n');
        var parts = lines.Select(l =>
        {
            var prefix = LinePrefix().Match(l).Length;
            var body = l[prefix..];
            var trimmed = body.TrimEnd();
            return (Prefix: l[..prefix], Body: trimmed, Tail: body[trimmed.Length..]);
        }).ToList();
        var filled = parts.Where(p => p.Body.Length > 0).ToList();
        bool IsWrapped(string body) => body.Length >= open.Length + close.Length
            && body.StartsWith(open, StringComparison.Ordinal) && body.EndsWith(close, StringComparison.Ordinal)
            && (!IsStar(open) || open == Bold || !body.StartsWith("**", StringComparison.Ordinal) || body.StartsWith("***", StringComparison.Ordinal));
        var unwrap = filled.Count > 0 && filled.All(p => IsWrapped(p.Body));

        var result = string.Join('\n', parts.Select(p => p.Body.Length == 0
            ? p.Prefix + p.Tail
            : p.Prefix + (unwrap ? p.Body[open.Length..^close.Length] : open + p.Body + close) + p.Tail));
        return new TextEdit(start, length, result, start, result.Length);
    }

    private static string? OpenColorBefore(string text, int start)
    {
        var head = text[..start];
        var match = OpenColorAtEnd().Match(head);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:(?<heading>#{1,6})[ \t]+|(?<task>[-*+][ \t]+\[[ xX]\])[ \t]+|(?<bullet>[-*+])[ \t]+|(?<number>\d+[.)])[ \t]+|(?<quote>>)[ \t]?)?")]
    private static partial Regex LinePrefix();

    [GeneratedRegex("^<span style=\"color:\\s*(?<hex>#[0-9A-Fa-f]{6})\\s*;?\">(?<content>.*)</span>$", RegexOptions.Singleline)]
    private static partial Regex ColorSpan();

    [GeneratedRegex("<span style=\"color:\\s*#[0-9A-Fa-f]{6}\\s*;?\">$")]
    private static partial Regex OpenColorAtEnd();
}
