using System.Text.RegularExpressions;

namespace DayDeck.Core.Notes;

/// <summary>옵시디언 모드에서 한 줄이 어떤 블록인지.</summary>
public enum LiveLineKind
{
    Text,
    Heading,
    Quote,
    Bullet,
    Numbered,
    Task,
    CodeFence,
    Code,
    Rule,
    Table,
}

/// <summary>줄 안 글자 범위의 서식. 겹쳐서 쓸 수 있다 (굵게 안의 기울임 등).</summary>
[Flags]
public enum LiveStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
    Highlight = 16,
    Underline = 32,
    Link = 64,
    /// <summary>서식 기호(**, #, ](주소) 등). 커서가 있는 줄에서만 흐리게 보이고, 다른 줄에서는 숨긴다.</summary>
    Marker = 128,
    /// <summary>체크한 할 일의 내용 (흐리게 + 취소선).</summary>
    Done = 256,
}

/// <summary>커서가 없는 줄에서 기호 대신 보일 것.</summary>
public enum LiveReplace
{
    Hide,
    Bullet,
    Checkbox,
    CheckboxDone,
    Rule,
    Image,
}

/// <param name="Url">링크 주소. 위키 링크(<c>[[메모]]</c>)는 <see cref="IsWiki"/>가 true이고 주소는 메모 이름.</param>
public sealed record LiveSpan(int Start, int Length, LiveStyle Style, string? Color = null, string? Url = null, bool IsWiki = false)
{
    public int End => Start + Length;
}

/// <param name="Data">그림 경로.</param>
/// <param name="Width">그림 너비 (<c>![설명|300](경로)</c>, 0이면 원래 크기).</param>
public sealed record LiveMarker(int Start, int Length, LiveReplace Kind, string? Data = null, int Width = 0)
{
    public int End => Start + Length;
}

public sealed record LiveLine(LiveLineKind Kind, int Level, IReadOnlyList<LiveSpan> Spans, IReadOnlyList<LiveMarker> Markers)
{
    /// <summary>체크 상자 "[ ]"/"[x]"의 줄 안 위치 (할 일 줄만, 없으면 -1).</summary>
    public int CheckboxStart { get; init; } = -1;
}

/// <summary>
/// 옵시디언 "라이브 미리보기"처럼 원문을 그대로 두고 서식만 입힌다. 파일 내용은 원문 그대로이므로
/// 이 클래스는 줄마다 "어디를 어떤 서식으로 칠하고, 커서가 없을 때 어떤 기호를 숨기거나 바꿀지"만 알려 준다.
/// </summary>
public static partial class LivePreview
{
    /// <summary>
    /// 줄마다 코드 블록 상태: 0 = 보통, 1 = 코드 울타리(```), 2 = 코드 블록 안. 울타리가 닫히지 않으면 끝까지 코드다.
    /// </summary>
    public static byte[] CodeBlockStates(IReadOnlyList<string> lines)
    {
        var states = new byte[lines.Count];
        string? fence = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var match = FenceRegex().Match(lines[i]);
            if (fence is null)
            {
                if (match.Success)
                {
                    fence = match.Groups["fence"].Value;
                    states[i] = 1;
                }
            }
            else if (match.Success && match.Groups["fence"].Value.StartsWith(fence[0]) && match.Groups["fence"].Length >= fence.Length
                     && lines[i].Trim().Length == match.Groups["fence"].Length)
            {
                fence = null;
                states[i] = 1;
            }
            else
            {
                states[i] = 2;
            }
        }
        return states;
    }

    /// <param name="codeState"><see cref="CodeBlockStates"/>의 값.</param>
    public static LiveLine ParseLine(string line, byte codeState = 0)
    {
        if (codeState == 1)
        {
            return new LiveLine(LiveLineKind.CodeFence, 0, [new LiveSpan(0, line.Length, LiveStyle.Marker)], []);
        }
        if (codeState == 2)
        {
            return new LiveLine(LiveLineKind.Code, 0, [], []);
        }

        var spans = new List<LiveSpan>();
        var markers = new List<LiveMarker>();

        if (RuleRegex().IsMatch(line))
        {
            spans.Add(new LiveSpan(0, line.Length, LiveStyle.Marker));
            markers.Add(new LiveMarker(0, line.Length, LiveReplace.Rule));
            return new LiveLine(LiveLineKind.Rule, 0, spans, markers);
        }
        if (line.TrimStart().StartsWith('|'))
        {
            // 표: 줄 맞춤이 깨지지 않게 기호를 숨기지 않고 고정폭 글꼴로만 보인다.
            if (TableDelimiterRegex().IsMatch(line))
            {
                spans.Add(new LiveSpan(0, line.Length, LiveStyle.Marker));
            }
            return new LiveLine(LiveLineKind.Table, 0, spans, []);
        }

        var kind = LiveLineKind.Text;
        var level = 0;
        var contentStart = 0;
        var checkbox = -1;

        if (HeadingRegex().Match(line) is { Success: true } heading)
        {
            kind = LiveLineKind.Heading;
            level = heading.Groups["marks"].Length;
            contentStart = heading.Length;
            if (contentStart < line.Length)
            {
                AddMarker(spans, markers, 0, heading.Length, LiveReplace.Hide);
            }
            else
            {
                spans.Add(new LiveSpan(0, line.Length, LiveStyle.Marker)); // 아직 글자가 없는 제목은 기호를 숨기지 않는다
            }
        }
        else if (QuoteRegex().Match(line) is { Success: true } quote)
        {
            kind = LiveLineKind.Quote;
            level = quote.Value.Count(c => c == '>');
            contentStart = quote.Length;
            AddMarker(spans, markers, 0, quote.Length, LiveReplace.Hide);
        }
        else if (TaskRegex().Match(line) is { Success: true } task)
        {
            kind = LiveLineKind.Task;
            level = task.Groups["indent"].Length;
            var bullet = task.Groups["bullet"];
            var box = task.Groups["box"];
            checkbox = box.Index;
            var done = box.Value is "[x]" or "[X]";
            if (char.IsDigit(bullet.Value[0]))
            {
                // 번호 할 일은 번호를 그대로 보인다.
                AddMarker(spans, markers, box.Index, box.Length, done ? LiveReplace.CheckboxDone : LiveReplace.Checkbox);
            }
            else
            {
                AddMarker(spans, markers, bullet.Index, box.Index - bullet.Index, LiveReplace.Hide);
                AddMarker(spans, markers, box.Index, box.Length, done ? LiveReplace.CheckboxDone : LiveReplace.Checkbox);
            }
            contentStart = task.Length;
            if (done && contentStart < line.Length)
            {
                spans.Add(new LiveSpan(contentStart, line.Length - contentStart, LiveStyle.Done));
            }
        }
        else if (BulletRegex().Match(line) is { Success: true } bulletLine)
        {
            kind = LiveLineKind.Bullet;
            level = bulletLine.Groups["indent"].Length;
            var bullet = bulletLine.Groups["bullet"];
            AddMarker(spans, markers, bullet.Index, 1, LiveReplace.Bullet);
            contentStart = bulletLine.Length;
        }
        else if (NumberedRegex().Match(line) is { Success: true } numbered)
        {
            kind = LiveLineKind.Numbered;
            level = numbered.Groups["indent"].Length;
            contentStart = numbered.Length;
        }

        ParseInline(line, contentStart, line.Length, spans, markers);
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        markers.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new LiveLine(kind, level, spans, markers) { CheckboxStart = checkbox };
    }

    /// <summary>체크 상자를 눌렀을 때의 줄: "[ ]" ↔ "[x]". 할 일 줄이 아니면 null.</summary>
    public static string? ToggleCheckbox(string line)
    {
        if (TaskRegex().Match(line) is not { Success: true } task)
        {
            return null;
        }
        var box = task.Groups["box"];
        var done = box.Value is "[x]" or "[X]";
        return line[..box.Index] + (done ? "[ ]" : "[x]") + line[(box.Index + box.Length)..];
    }

    /// <summary>
    /// 줄에서 Enter를 눌렀을 때 다음 줄 앞에 붙일 것 (목록·번호·체크·인용을 이어 쓴다).
    /// <paramref name="endList"/>가 true면 내용 없는 항목이라 목록을 끝낸다 (기호를 지움).
    /// </summary>
    public static string? ContinuePrefix(string line, out bool endList)
    {
        endList = false;
        Match match;
        string next;
        if ((match = TaskRegex().Match(line)).Success)
        {
            var bullet = match.Groups["bullet"].Value;
            next = match.Groups["indent"].Value + NextBullet(bullet) + " [ ] ";
        }
        else if ((match = BulletRegex().Match(line)).Success)
        {
            next = match.Groups["indent"].Value + match.Groups["bullet"].Value + " ";
        }
        else if ((match = NumberedRegex().Match(line)).Success)
        {
            next = match.Groups["indent"].Value + NextBullet(match.Groups["number"].Value) + " ";
        }
        else if ((match = QuoteRegex().Match(line)).Success)
        {
            next = match.Value;
        }
        else
        {
            return null;
        }
        endList = line[match.Length..].Trim().Length == 0;
        return next;
    }

    private static string NextBullet(string bullet)
    {
        var digits = bullet.TrimEnd('.', ')');
        return int.TryParse(digits, out var n) ? $"{n + 1}{bullet[digits.Length..]}" : bullet;
    }

    private static void AddMarker(List<LiveSpan> spans, List<LiveMarker> markers, int start, int length, LiveReplace kind,
        string? data = null, int width = 0)
    {
        if (length <= 0)
        {
            return;
        }
        spans.Add(new LiveSpan(start, length, LiveStyle.Marker));
        markers.Add(new LiveMarker(start, length, kind, data, width));
    }

    private static readonly (string Open, string Close, LiveStyle Style)[] Delimited =
    [
        ("**", "**", LiveStyle.Bold),
        ("__", "__", LiveStyle.Bold),
        ("~~", "~~", LiveStyle.Strike),
        ("==", "==", LiveStyle.Highlight),
        ("<mark>", "</mark>", LiveStyle.Highlight),
        ("<u>", "</u>", LiveStyle.Underline),
        ("<b>", "</b>", LiveStyle.Bold),
        ("<i>", "</i>", LiveStyle.Italic),
        ("<s>", "</s>", LiveStyle.Strike),
        ("*", "*", LiveStyle.Italic),
        ("_", "_", LiveStyle.Italic),
    ];

    /// <summary>[start, end) 범위의 글자 서식. 서식 기호는 표시(Marker)와 숨김 대상으로, 안쪽 글자는 서식으로 넣는다.</summary>
    private static void ParseInline(string s, int start, int end, List<LiveSpan> spans, List<LiveMarker> markers)
    {
        var i = start;
        while (i < end)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
            {
                AddMarker(spans, markers, i, 1, LiveReplace.Hide);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var ticks = CountRun(s, i, end, '`');
                var codeEnd = s.IndexOf(new string('`', ticks), i + ticks, end - i - ticks, StringComparison.Ordinal);
                if (codeEnd > i + ticks)
                {
                    AddMarker(spans, markers, i, ticks, LiveReplace.Hide);
                    spans.Add(new LiveSpan(i + ticks, codeEnd - i - ticks, LiveStyle.Code));
                    AddMarker(spans, markers, codeEnd, ticks, LiveReplace.Hide);
                    i = codeEnd + ticks;
                    continue;
                }
                i += ticks;
                continue;
            }
            if (c == '!' && Slice(s, i, end) is var rest && ImageRegex().Match(rest) is { Success: true } image)
            {
                var (_, width) = SplitWidth(image.Groups["alt"].Value);
                AddMarker(spans, markers, i, image.Length, LiveReplace.Image, image.Groups["url"].Value, width);
                i += image.Length;
                continue;
            }
            if (c == '!' && WikiImageRegex().Match(Slice(s, i, end)) is { Success: true } wikiImage)
            {
                var (_, width) = SplitWidth(wikiImage.Groups["alias"].Success ? wikiImage.Groups["alias"].Value : "");
                AddMarker(spans, markers, i, wikiImage.Length, LiveReplace.Image, wikiImage.Groups["target"].Value, width);
                i += wikiImage.Length;
                continue;
            }
            if (c == '[' && WikiLinkRegex().Match(Slice(s, i, end)) is { Success: true } wiki)
            {
                var target = wiki.Groups["target"];
                if (wiki.Groups["alias"].Success)
                {
                    var alias = wiki.Groups["alias"];
                    AddMarker(spans, markers, i, alias.Index, LiveReplace.Hide); // "[[대상|"
                    spans.Add(new LiveSpan(i + alias.Index, alias.Length, LiveStyle.Link, Url: target.Value, IsWiki: true));
                }
                else
                {
                    AddMarker(spans, markers, i, 2, LiveReplace.Hide);
                    spans.Add(new LiveSpan(i + 2, target.Length, LiveStyle.Link, Url: target.Value, IsWiki: true));
                }
                AddMarker(spans, markers, i + wiki.Length - 2, 2, LiveReplace.Hide);
                i += wiki.Length;
                continue;
            }
            if (c == '[' && LinkRegex().Match(Slice(s, i, end)) is { Success: true } link)
            {
                var text = link.Groups["text"];
                AddMarker(spans, markers, i, 1, LiveReplace.Hide);
                spans.Add(new LiveSpan(i + 1, text.Length, LiveStyle.Link, Url: link.Groups["url"].Value));
                ParseInline(s, i + 1, i + 1 + text.Length, spans, markers);
                AddMarker(spans, markers, i + 1 + text.Length, link.Length - 1 - text.Length, LiveReplace.Hide);
                i += link.Length;
                continue;
            }
            if (c == 'h' && (i == start || !char.IsLetterOrDigit(s[i - 1])) && UrlRegex().Match(Slice(s, i, end)) is { Success: true } url)
            {
                spans.Add(new LiveSpan(i, url.Length, LiveStyle.Link, Url: url.Value));
                i += url.Length;
                continue;
            }
            if (c == '<' && ColorSpanRegex().Match(Slice(s, i, end)) is { Success: true } colored)
            {
                var inner = colored.Groups["text"];
                AddMarker(spans, markers, i, inner.Index, LiveReplace.Hide);
                spans.Add(new LiveSpan(i + inner.Index, inner.Length, LiveStyle.None, Color: colored.Groups["color"].Value));
                ParseInline(s, i + inner.Index, i + inner.Index + inner.Length, spans, markers);
                AddMarker(spans, markers, i + inner.Index + inner.Length, colored.Length - inner.Index - inner.Length, LiveReplace.Hide);
                i += colored.Length;
                continue;
            }
            if (TryDelimited(s, i, start, end, out var open, out var close, out var closeLength, out var style))
            {
                AddMarker(spans, markers, i, open, LiveReplace.Hide);
                spans.Add(new LiveSpan(i + open, close - i - open, style));
                ParseInline(s, i + open, close, spans, markers);
                AddMarker(spans, markers, close, closeLength, LiveReplace.Hide);
                i = close + closeLength;
                continue;
            }
            i++;
        }
    }

    private static bool TryDelimited(string s, int i, int start, int end, out int openLength, out int closeIndex, out int closeLength, out LiveStyle style)
    {
        foreach (var (open, close, kind) in Delimited)
        {
            if (!s.AsSpan(i, end - i).StartsWith(open, StringComparison.Ordinal))
            {
                continue;
            }
            var contentStart = i + open.Length;
            if (contentStart >= end || char.IsWhiteSpace(s[contentStart]))
            {
                continue;
            }
            var isHtml = open[0] == '<';
            // "_"·"__"는 단어 안(snake_case)에서는 서식이 아니다.
            if (open[0] == '_' && i > start && char.IsLetterOrDigit(s[i - 1]))
            {
                continue;
            }
            // "*"는 "**"의 일부가 아닐 때만.
            if (open == "*" && contentStart < end && s[contentStart] == '*')
            {
                continue;
            }
            var search = contentStart;
            while (search < end)
            {
                var found = s.IndexOf(close, search, end - search, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }
                // "***둘다***"처럼 기호가 이어지면 닫는 기호는 그 끝에 붙은 것으로 본다.
                while (close.Length > 1 && close[0] is '*' or '_' && found + close.Length < end && s[found + close.Length] == close[0])
                {
                    found++;
                }
                var ok = found > contentStart && (isHtml || !char.IsWhiteSpace(s[found - 1]));
                if (ok && close.Length == 1 && found + 1 < end && s[found + 1] == close[0])
                {
                    ok = false; // "*" 닫기가 "**"의 일부
                }
                if (ok && close[0] == '_' && found + close.Length < end && char.IsLetterOrDigit(s[found + close.Length]))
                {
                    ok = false;
                }
                if (ok)
                {
                    openLength = open.Length;
                    closeIndex = found;
                    closeLength = close.Length;
                    style = kind;
                    return true;
                }
                search = found + close.Length;
            }
        }
        openLength = 0;
        closeIndex = 0;
        closeLength = 0;
        style = LiveStyle.None;
        return false;
    }

    private static (string Alt, int Width) SplitWidth(string alt)
    {
        var bar = alt.LastIndexOf('|');
        if (bar >= 0 && int.TryParse(alt[(bar + 1)..], out var width) && width > 0)
        {
            return (alt[..bar], width);
        }
        return int.TryParse(alt, out var only) && only > 0 ? ("", only) : (alt, 0);
    }

    private static string Slice(string s, int start, int end) => s[start..end];

    private static int CountRun(string s, int i, int end, char c)
    {
        var n = 0;
        while (i + n < end && s[i + n] == c)
        {
            n++;
        }
        return n;
    }

    private static bool IsEscapable(char c) => @"\`*_{}[]()#+-.!|~=<>".Contains(c);

    [GeneratedRegex(@"^\s{0,3}(?<fence>`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^ {0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex TableDelimiterRegex();

    [GeneratedRegex(@"^(?<marks>#{1,6})(\s+|$)")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*>\s?)+")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^(?<indent>\s*)(?<bullet>[-*+]|\d+[.)])\s+(?<box>\[[ xX]\])(\s+|$)")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"^(?<indent>\s*)(?<bullet>[-*+])(\s+|$)")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(?<indent>\s*)(?<number>\d+[.)])(\s+|$)")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^!\[(?<alt>[^\]]*)\]\((?<url><[^>]+>|[^)\s]+)(\s+""[^""]*"")?\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^!\[\[(?<target>[^\]|]+)(\|(?<alias>[^\]]*))?\]\]")]
    private static partial Regex WikiImageRegex();

    [GeneratedRegex(@"^\[\[(?<target>[^\]|]+)(\|(?<alias>[^\]]+))?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"^\[(?<text>[^\]]+)\]\((?<url><[^>]+>|[^)\s]+)(\s+""[^""]*"")?\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^https?://[^\s<>()]+[^\s<>().,;:!?""')\]]")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^<span\s+style\s*=\s*""\s*color\s*:\s*(?<color>#[0-9a-fA-F]{3,8})\s*;?\s*"">(?<text>.+?)</span>")]
    private static partial Regex ColorSpanRegex();
}
