using System.Text;
using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
    Code = 16,
    Highlight = 32,
}

/// <summary>
/// 같은 서식의 글자 조각. <see cref="Link"/>가 있으면 링크, <see cref="Color"/>는 #RRGGBB.
/// <see cref="Image"/>가 있으면 그림 <c>![설명](경로)</c>이고 <see cref="Text"/>는 설명이다.
/// <see cref="ImageWidth"/>는 사용자가 정한 그림 너비(px, 높이는 비율대로)로 <c>![설명|300](경로)</c>처럼 쓴다 (Obsidian 방식).
/// </summary>
public sealed record InlineRun(string Text, InlineStyle Style = InlineStyle.None, string? Color = null, string? Link = null, string? Image = null, int? ImageWidth = null);

public enum BlockKind
{
    Paragraph,
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
/// 문단 하나. <see cref="Level"/>은 제목 수준(1~6) 또는 목록 들여쓰기 수준(0부터), <see cref="Number"/>는 번호 목록 번호,
/// <see cref="Checked"/>는 체크 목록, <see cref="Line"/>은 원문 줄 번호(0부터), <see cref="Table"/>은 표.
/// </summary>
public sealed record MarkdownBlock(BlockKind Kind, IReadOnlyList<InlineRun> Inlines, int Level = 0, int Number = 0, bool Checked = false, int Line = 0, MarkdownTable? Table = null)
{
    public string PlainText => Table?.PlainText ?? string.Concat(Inlines.Select(i => i.Text));
}

/// <summary>
/// 메모 미리보기용 마크다운 해석 (CommonMark의 자주 쓰는 부분만): 제목, 목록(글머리·번호·체크), 인용, 코드 블록, 가로줄, 표(GFM),
/// 굵게/기울임/취소선/코드, 링크 [이름](주소)와 http 주소, 그림 ![설명](경로), 그리고 편집기가 쓰는 HTML 태그(색 span, u, mark, br).
/// 모르는 문법은 글자 그대로 둔다. 한 줄 바꿈은 줄바꿈으로 보인다 (메모장처럼).
/// </summary>
public static partial class MarkdownDoc
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var paragraphLine = 0;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new MarkdownBlock(BlockKind.Paragraph, ParseInlines(string.Join("\n", paragraph)), Line: paragraphLine));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence().Match(line) is { Success: true } fence)
            {
                FlushParagraph();
                var start = i;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence.Groups["fence"].Value, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }
                blocks.Add(new MarkdownBlock(BlockKind.Code, [new InlineRun(string.Join("\n", code), InlineStyle.Code)], Line: start));
                continue;
            }
            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (MarkdownTable.TryParse(lines, i, out var table, out var tableLines))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Table, [], Line: i, Table: table));
                i += tableLines - 1;
                continue;
            }
            if (Rule().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Rule, [], Line: i));
                continue;
            }
            if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Heading, ParseInlines(heading.Groups["text"].Value.TrimEnd('#', ' ')),
                    heading.Groups["level"].Value.Length, Line: i));
                continue;
            }
            if (ListLine().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                var level = item.Groups["indent"].Value.Replace("\t", "    ").Length / 2;
                var text = ParseInlines(item.Groups["text"].Value);
                if (item.Groups["check"].Success)
                {
                    blocks.Add(new MarkdownBlock(BlockKind.Task, text, level,
                        Checked: item.Groups["check"].Value.Contains('x', StringComparison.OrdinalIgnoreCase), Line: i));
                }
                else if (item.Groups["number"].Success)
                {
                    blocks.Add(new MarkdownBlock(BlockKind.Numbered, text, level, int.Parse(item.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture), Line: i));
                }
                else
                {
                    blocks.Add(new MarkdownBlock(BlockKind.Bullet, text, level, Line: i));
                }
                continue;
            }
            if (Quote().Match(line) is { Success: true } quote)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(BlockKind.Quote, ParseInlines(quote.Groups["text"].Value), Line: i));
                continue;
            }
            if (paragraph.Count == 0)
            {
                paragraphLine = i;
            }
            paragraph.Add(line);
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>서식 기호를 뺀 글자만 (목록 미리보기, 제목 제안, 할 일 제목).</summary>
    public static string ToPlainText(string markdownLine) => string.Concat(ParseInlines(markdownLine).Select(r => r.Text));

    /// <summary>한 문단 안의 글자 서식.</summary>
    public static IReadOnlyList<InlineRun> ParseInlines(string text)
    {
        var runs = new List<InlineRun>();
        var current = new StringBuilder();
        var style = InlineStyle.None;
        var colors = new Stack<string?>();
        var spanIsColor = new Stack<bool>();

        void Flush()
        {
            if (current.Length > 0)
            {
                runs.Add(new InlineRun(current.ToString(), style, colors.Count > 0 ? colors.Peek() : null));
                current.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // 역슬래시 이스케이프: \* \[ 등
            if (c == '\\' && i + 1 < text.Length && @"\`*_{}[]()#+-.!~<>|".Contains(text[i + 1]))
            {
                current.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // 인라인 코드 `...`
            if (c == '`' && text.IndexOf('`', i + 1) is var codeEnd and > 0)
            {
                Flush();
                runs.Add(new InlineRun(text[(i + 1)..codeEnd], style | InlineStyle.Code, colors.Count > 0 ? colors.Peek() : null));
                i = codeEnd + 1;
                continue;
            }

            // 그림 ![설명](경로), 너비를 정했으면 ![설명|300](경로)
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && LinkAt().Match(text, i + 1) is { Success: true } image && image.Index == i + 1)
            {
                Flush();
                var label = image.Groups["label"].Value;
                int? width = null;
                if (ImageWidthSuffix().Match(label) is { Success: true } suffix && int.TryParse(suffix.Groups["width"].Value, out var parsed) && parsed > 0)
                {
                    label = label[..suffix.Index];
                    width = parsed;
                }
                runs.Add(new InlineRun(label, style, Image: image.Groups["url"].Value, ImageWidth: width));
                i += image.Length + 1;
                continue;
            }

            // 링크 [이름](주소)
            if (c == '[' && LinkAt().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Flush();
                foreach (var inner in ParseInlines(link.Groups["label"].Value))
                {
                    runs.Add(inner with { Style = inner.Style | style, Link = link.Groups["url"].Value, Color = inner.Color ?? (colors.Count > 0 ? colors.Peek() : null) });
                }
                i += link.Length;
                continue;
            }

            // 맨 주소 http://... https://...
            if ((c == 'h' || c == 'w') && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && BareUrl().Match(text, i) is { Success: true } bare && bare.Index == i)
            {
                Flush();
                var url = bare.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?');
                runs.Add(new InlineRun(url, style, colors.Count > 0 ? colors.Peek() : null, url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url));
                i += url.Length;
                continue;
            }

            // HTML 태그 (편집기가 쓰는 것만)
            if (c == '<' && Tag().Match(text, i) is { Success: true } tag && tag.Index == i)
            {
                Flush();
                var name = tag.Groups["name"].Value.ToLowerInvariant();
                var closing = tag.Groups["close"].Success;
                switch (name)
                {
                    case "u":
                        style = closing ? style & ~InlineStyle.Underline : style | InlineStyle.Underline;
                        break;
                    case "mark":
                        style = closing ? style & ~InlineStyle.Highlight : style | InlineStyle.Highlight;
                        break;
                    case "b" or "strong":
                        style = closing ? style & ~InlineStyle.Bold : style | InlineStyle.Bold;
                        break;
                    case "i" or "em":
                        style = closing ? style & ~InlineStyle.Italic : style | InlineStyle.Italic;
                        break;
                    case "s" or "del":
                        style = closing ? style & ~InlineStyle.Strike : style | InlineStyle.Strike;
                        break;
                    case "br":
                        current.Append('\n');
                        break;
                    case "span" when closing && spanIsColor.Count > 0:
                        if (spanIsColor.Pop())
                        {
                            colors.Pop();
                        }
                        else
                        {
                            current.Append(tag.Value);
                        }
                        break;
                    case "span" when !closing && ColorStyle().Match(tag.Groups["attrs"].Value) is { Success: true } color:
                        spanIsColor.Push(true);
                        colors.Push(color.Groups["hex"].Value.ToUpperInvariant());
                        break;
                    case "span" when !closing:
                        spanIsColor.Push(false);
                        current.Append(tag.Value);
                        break;
                    default:
                        // 모르는 태그(<div>, <https://…> 등)는 지우지 않고 글자 그대로 둔다 (보기에서 고쳐 저장해도 잃지 않도록).
                        current.Append(tag.Value);
                        break;
                }
                i += tag.Length;
                continue;
            }

            // 별표: ***, **, * (닫는 표시가 뒤에 있을 때만 서식)
            if (c == '*' || (c == '~' && i + 1 < text.Length && text[i + 1] == '~'))
            {
                var marker = c == '~' ? "~~" : new string('*', Math.Min(3, CountRun(text, i, '*')));
                var flag = marker switch
                {
                    "~~" => InlineStyle.Strike,
                    "***" => InlineStyle.Bold | InlineStyle.Italic,
                    "**" => InlineStyle.Bold,
                    _ => InlineStyle.Italic,
                };
                var opening = (style & flag) != flag;
                var canOpen = i + marker.Length < text.Length && !char.IsWhiteSpace(text[i + marker.Length])
                              && text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal) > i + marker.Length;
                var canClose = i > 0 && !char.IsWhiteSpace(text[i - 1]);
                if ((opening && canOpen) || (!opening && canClose))
                {
                    Flush();
                    style = opening ? style | flag : style & ~flag;
                    i += marker.Length;
                    continue;
                }
            }

            current.Append(c);
            i++;
        }
        Flush();
        return runs;
    }

    private static int CountRun(string text, int index, char c)
    {
        var n = 0;
        while (index + n < text.Length && text[index + n] == c)
        {
            n++;
        }
        return n;
    }

    [GeneratedRegex(@"^\s*(?<fence>```|~~~)")]
    internal static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    internal static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}(?<level>#{1,6})\s+(?<text>.*)$")]
    internal static partial Regex Heading();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:[-*+]|(?<number>\d+)[.)])\s+(?:\[(?<check>[ xX])\]\s+)?(?<text>.*)$")]
    internal static partial Regex ListLine();

    [GeneratedRegex(@"^\s{0,3}>\s?(?<text>.*)$")]
    internal static partial Regex Quote();

    [GeneratedRegex(@"\[(?<label>(?:\\.|[^\]\\])*)\]\((?<url>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    internal static partial Regex LinkAt();

    [GeneratedRegex(@"\|(?<width>\d{1,5})$")]
    internal static partial Regex ImageWidthSuffix();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    internal static partial Regex BareUrl();

    [GeneratedRegex(@"<(?<close>/)?(?<name>[a-zA-Z]+)(?<attrs>[^>]*)/?>")]
    internal static partial Regex Tag();

    [GeneratedRegex(@"color\s*:\s*(?<hex>#[0-9A-Fa-f]{6})")]
    internal static partial Regex ColorStyle();
}
