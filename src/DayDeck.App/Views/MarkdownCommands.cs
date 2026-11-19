using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DayDeck.App.ViewModels;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 마크다운 원문을 고치는 편집기 (원문 TextBox, 옵시디언 모드 편집기). 서식 동작은 이 공통 기능만 쓴다.
/// </summary>
internal interface IMarkdownTarget
{
    string Text { get; }
    int SelectionStart { get; }
    int SelectionLength { get; }
    int CaretOffset { get; }
    string SelectedText { get; set; }
    void Select(int start, int length);
    bool Focus();
    void ScrollToOffset(int offset);
    FrameworkElement Element { get; }
}

/// <summary>TextBox를 <see cref="IMarkdownTarget"/>으로.</summary>
internal sealed class TextBoxTarget(TextBox box) : IMarkdownTarget
{
    public string Text => box.Text;
    public int SelectionStart => box.SelectionStart;
    public int SelectionLength => box.SelectionLength;
    public int CaretOffset => box.CaretIndex;
    public string SelectedText { get => box.SelectedText; set => box.SelectedText = value; }
    public void Select(int start, int length) => box.Select(start, length);
    public bool Focus() => box.Focus();
    public void ScrollToOffset(int offset) => box.ScrollToLine(Math.Max(0, box.GetLineIndexFromCharacterIndex(offset)));
    public FrameworkElement Element => box;
}

/// <summary>
/// 마크다운 서식 동작 (메모 편집기와 할 일 메모가 함께 쓴다). 모든 변경은 TextBox의 선택 범위를 바꾸는 방식이라
/// Ctrl+Z 한 번으로 되돌릴 수 있고, 바인딩된 값도 그대로 갱신된다.
/// </summary>
internal static class MarkdownCommands
{
    /// <summary>서식 단축키를 받는 TextBox 표시 (Tag). MainWindow는 이 입력란에서 Ctrl+K를 검색 대신 링크로 넘긴다.</summary>
    public const string Tag = "markdown";

    /// <summary>설정 "서식 단축키 사용". 끄면 Ctrl+B 등은 TextBox 기본 동작(또는 앱 단축키)으로 간다.</summary>
    public static bool ShortcutsEnabled => App.Current.Settings.Current.Notes.FormatShortcuts;

    public static IMarkdownTarget AsTarget(this TextBox box) => new TextBoxTarget(box);

    public static void Apply(IMarkdownTarget editor, Func<string, int, int, TextEdit> edit)
    {
        var result = edit(editor.Text, editor.SelectionStart, editor.SelectionLength);
        editor.Select(result.Start, result.Length);
        editor.SelectedText = result.Replacement;
        editor.Select(result.SelectionStart, result.SelectionLength);
        editor.Focus();
    }

    public static void Inline(IMarkdownTarget editor, string open, string close) =>
        Apply(editor, (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, open, close));

    public static void Bold(IMarkdownTarget editor) => Inline(editor, MarkdownEdit.Bold, MarkdownEdit.Bold);

    public static void Italic(IMarkdownTarget editor) => Inline(editor, MarkdownEdit.Italic, MarkdownEdit.Italic);

    public static void Underline(IMarkdownTarget editor) => Inline(editor, "<u>", "</u>");

    public static void Strike(IMarkdownTarget editor) => Inline(editor, "~~", "~~");

    public static void Highlight(IMarkdownTarget editor) => Inline(editor, "<mark>", "</mark>");

    public static void Code(IMarkdownTarget editor) => Inline(editor, "`", "`");

    public static void Line(IMarkdownTarget editor, LineStyle style) =>
        Apply(editor, (t, s, l) => MarkdownEdit.SetLineStyle(t, s, l, style));

    /// <summary>제목 1~3, 또는 "본문"(Paragraph)은 지금 줄의 서식을 지운다.</summary>
    public static void Heading(IMarkdownTarget editor, LineStyle style)
    {
        if (style != LineStyle.Paragraph)
        {
            Line(editor, style);
            return;
        }
        Apply(editor, (t, s, l) =>
        {
            var lineStart = s == 0 ? 0 : t.LastIndexOf('\n', s - 1) + 1;
            var lineEnd = t.IndexOf('\n', s);
            var current = MarkdownEdit.GetLineStyle(t[lineStart..(lineEnd < 0 ? t.Length : lineEnd)]);
            // 같은 서식을 한 번 더 적용하면 본문이 된다 (SetLineStyle의 토글).
            return current == LineStyle.Paragraph ? new TextEdit(s, 0, "", s, l) : MarkdownEdit.SetLineStyle(t, s, l, current);
        });
    }

    public static void Link(IMarkdownTarget editor)
    {
        if (LinkDialog.Ask(Window.GetWindow(editor.Element), editor.SelectedText) is var (url, label))
        {
            Apply(editor, (t, s, l) => MarkdownEdit.InsertLink(t, s, l, label, url));
        }
    }

    /// <summary>"보기" 편집기의 링크 넣기 (같은 대화상자).</summary>
    public static void RichLink(MarkdownRichEditor editor)
    {
        if (LinkDialog.Ask(Window.GetWindow(editor), editor.Selection.Text) is var (url, label))
        {
            editor.InsertLink(url, label);
        }
    }

    public static void ShowHeadingMenu(IMarkdownTarget editor, UIElement anchor) => ShowHeadingMenu(anchor, style => Heading(editor, style));

    /// <summary>제목 메뉴. 고른 서식(제목 1~3, 본문=Paragraph)을 <paramref name="apply"/>로 넘긴다.</summary>
    public static void ShowHeadingMenu(UIElement anchor, Action<LineStyle> apply)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (label, style, size) in new[] { ("제목 1", LineStyle.Heading1, 18.0), ("제목 2", LineStyle.Heading2, 15.0), ("제목 3", LineStyle.Heading3, 13.5), ("본문", LineStyle.Paragraph, 12.5) })
        {
            var item = new MenuItem { Header = new TextBlock { Text = label, FontSize = size, FontWeight = style == LineStyle.Paragraph ? FontWeights.Normal : FontWeights.SemiBold } };
            System.Windows.Automation.AutomationProperties.SetName(item, label);
            item.Click += (_, _) => apply(style);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>글자색 메뉴. 고른 색은 <paramref name="picked"/>로 알린다 (단추의 색 표시용).</summary>
    public static void ShowColorMenu(IMarkdownTarget editor, UIElement anchor, Action<string>? picked = null) =>
        ShowColorMenu(anchor, hex => Apply(editor, (t, s, l) => MarkdownEdit.SetColor(t, s, l, hex)), picked);

    /// <summary>글자색 메뉴. 고른 색(#RRGGBB, 지우기는 null)을 <paramref name="apply"/>로 넘긴다.</summary>
    public static void ShowColorMenu(UIElement anchor, Action<string?> apply, Action<string>? picked = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (name, hex) in ListPalette.Colors)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = name, Foreground = TaskRowViewModel.TryParseBrush(hex), FontWeight = FontWeights.SemiBold },
                Icon = new System.Windows.Shapes.Rectangle { Width = 14, Height = 14, Fill = TaskRowViewModel.TryParseBrush(hex), RadiusX = 2, RadiusY = 2 },
            };
            System.Windows.Automation.AutomationProperties.SetName(item, name);
            item.Click += (_, _) =>
            {
                picked?.Invoke(hex);
                apply(hex);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "색 지우기" };
        clear.Click += (_, _) => apply(null);
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 서식 단축키 (워드·구글 문서와 같은 키): Ctrl+B/I/U, Ctrl+E 코드, Ctrl+K 링크, Ctrl+Shift+X 취소선, Ctrl+Shift+H 형광펜,
    /// Ctrl+Shift+7/8/9 번호·글머리·체크 목록, Ctrl+Alt+1~3 제목, Ctrl+Alt+0 본문. 처리했으면 true.
    /// </summary>
    public static bool HandleShortcut(TextBox editor, KeyEventArgs e) => HandleShortcut(editor.AsTarget(), e);

    public static bool HandleShortcut(IMarkdownTarget editor, KeyEventArgs e)
    {
        if (!ShortcutsEnabled)
        {
            return false;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Action? action = Keyboard.Modifiers switch
        {
            ModifierKeys.Control => key switch
            {
                Key.B => () => Bold(editor),
                Key.I => () => Italic(editor),
                Key.U => () => Underline(editor),
                Key.E => () => Code(editor),
                Key.K => () => Link(editor),
                _ => null,
            },
            ModifierKeys.Control | ModifierKeys.Shift => key switch
            {
                Key.X => () => Strike(editor),
                Key.H => () => Highlight(editor),
                Key.D7 => () => Line(editor, LineStyle.Numbered),
                Key.D8 => () => Line(editor, LineStyle.Bullet),
                Key.D9 => () => Line(editor, LineStyle.Task),
                _ => null,
            },
            ModifierKeys.Control | ModifierKeys.Alt => key switch
            {
                Key.D1 => () => Heading(editor, LineStyle.Heading1),
                Key.D2 => () => Heading(editor, LineStyle.Heading2),
                Key.D3 => () => Heading(editor, LineStyle.Heading3),
                Key.D0 => () => Heading(editor, LineStyle.Paragraph),
                _ => null,
            },
            _ => null,
        };
        if (action is null)
        {
            return false;
        }
        action();
        e.Handled = true;
        return true;
    }

    /// <summary>미리보기에서 체크 상자를 누르면 원문 줄의 [ ]/[x]를 바꾼다 (실행 취소 가능).</summary>
    public static void ToggleTaskLine(TextBox editor, int line, bool done) => ToggleTaskLine(editor.AsTarget(), line, done);

    public static void ToggleTaskLine(IMarkdownTarget editor, int line, bool done)
    {
        var text = editor.Text;
        var start = 0;
        for (var i = 0; i < line && start >= 0; i++)
        {
            start = text.IndexOf('\n', start) is var n and >= 0 ? n + 1 : -1;
        }
        if (start < 0)
        {
            return;
        }
        var end = text.IndexOf('\n', start);
        var original = text[start..(end < 0 ? text.Length : end)];
        var updated = System.Text.RegularExpressions.Regex.Replace(original, @"^(?<head>\s*(?:[-*+]|\d+[.)])\s+)\[[ xX]\]",
            m => m.Groups["head"].Value + (done ? "[x]" : "[ ]"));
        if (updated == original)
        {
            return;
        }
        editor.Select(start, original.Length);
        editor.SelectedText = updated;
        editor.Select(start + updated.Length, 0);
    }
}
