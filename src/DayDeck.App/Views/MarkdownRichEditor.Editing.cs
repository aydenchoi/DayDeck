using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 키 입력과 서식 명령. 워드처럼 동작한다: 목록에서 Enter는 다음 항목, 빈 항목에서 Enter는 목록 끝,
/// 문단 맨 앞 Backspace는 목록·제목 해제, Tab/Shift+Tab은 목록 들여쓰기. 문단 맨 앞에서 "- ", "1. ", "[ ] ", "# ", "> " 를
/// 치면 목록·체크·제목·인용으로 바뀐다. 마크다운으로 저장할 수 없는 RichTextBox 기본 서식(정렬, 글자 크기 등)은 막는다.
/// </summary>
internal sealed partial class MarkdownRichEditor
{
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 표 칸 글 상자의 키도 여기를 먼저 지난다: 칸 이동만 보고 나머지는 글 상자에 맡긴다.
        if (IsInTableCellBox(e.OriginalSource))
        {
            if (HandleTableCellKey(e))
            {
                e.Handled = true;
                return;
            }
            base.OnPreviewKeyDown(e);
            return;
        }
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System))
        {
            HideImageResize(); // 글자를 치거나 지우면 그림이 바뀌거나 사라질 수 있다
        }
        if (HandleKey(e))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private bool HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control) && HandleShortcut(key, modifiers))
        {
            return true;
        }
        switch (key)
        {
            case Key.Enter or Key.F2 when modifiers == ModifierKeys.None && SelectedTable() is { } table:
                EditTableCell(table, 0, 0); // 고른 표: 첫 칸부터 고친다 (Enter로 표를 지우지 않도록)
                return true;
            case Key.Enter when modifiers == ModifierKeys.None:
                return HandleEnter();
            case Key.Back when modifiers == ModifierKeys.None:
                return HandleBackspace();
            case Key.Tab when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                return HandleTab(modifiers == ModifierKeys.Shift);
            default:
                return false;
        }
    }

    /// <summary>
    /// 빠르게 친 글자를 먼저 문서에 넣는다. RichTextBox는 화면 갱신이 입력을 못 따라가면 친 글자를 모아 두었다가 나중에(Background) 넣는다.
    /// WPF의 Enter·Backspace·Tab·서식 명령은 처리하기 전에 이 글자들을 넣지만, 여기서 가로채는 키는 그보다 먼저 온다.
    /// 그대로 두면 항목을 치고 바로 Enter를 눌렀을 때 아직 빈 항목으로 보여 목록이 끝나 버린다.
    /// 공개 API 중에는 Copy()가 맨 먼저 이 글자들을 넣고, 선택이 비어 있으면 클립보드는 건드리지 않는다.
    /// </summary>
    private void FlushTypedText()
    {
        if (Selection.IsEmpty)
        {
            Copy();
        }
    }

    /// <summary>서식 단축키. 마크다운에 없는 RichTextBox 기본 단축키(정렬, 글자 크기, 줄 간격 등)는 아무것도 하지 않게 막는다.</summary>
    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        Action? action = null;
        if (MarkdownCommands.ShortcutsEnabled)
        {
            action = modifiers switch
            {
                ModifierKeys.Control => key switch
                {
                    Key.B => ToggleBold,
                    Key.I => ToggleItalic,
                    Key.U => ToggleUnderline,
                    Key.E => ToggleCode,
                    Key.K => () => MarkdownCommands.RichLink(this),
                    _ => null,
                },
                ModifierKeys.Control | ModifierKeys.Shift => key switch
                {
                    Key.X => ToggleStrike,
                    Key.H => ToggleHighlight,
                    Key.D7 => () => ToggleLineKind(LineKind.Numbered),
                    Key.D8 => () => ToggleLineKind(LineKind.Bullet),
                    Key.D9 => () => ToggleLineKind(LineKind.Task),
                    _ => null,
                },
                ModifierKeys.Control | ModifierKeys.Alt => key switch
                {
                    Key.D1 => () => SetHeading(1),
                    Key.D2 => () => SetHeading(2),
                    Key.D3 => () => SetHeading(3),
                    Key.D0 => () => SetHeading(0),
                    _ => null,
                },
                _ => null,
            };
        }
        if (action is not null)
        {
            FlushTypedText();
            action();
            return true;
        }
        if ((key == Key.V && modifiers == ModifierKeys.Control) && TryPasteImage())
        {
            return true;
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            return false;
        }
        // 복사·붙여 넣기·실행 취소·이동만 그대로 두고, 나머지 Ctrl 조합(RichTextBox 기본 서식)은 막는다.
        return key is not (Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Insert or Key.Delete or Key.Back
            or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.Space);
    }

    private bool HandleEnter()
    {
        FlushTypedText();
        if (CurrentParagraph() is not { } paragraph)
        {
            return false;
        }
        var info = Describe(paragraph);
        var isList = info.Kind is LineKind.Bullet or LineKind.Numbered or LineKind.Task;
        BeginChange();
        try
        {
            if (!Selection.IsEmpty)
            {
                Selection.Text = "";
                paragraph = CurrentParagraph() ?? paragraph;
            }
            // 빈 항목에서 Enter: 들여쓴 항목이면 한 단계 내어 쓰고, 아니면 목록·인용·제목을 끝낸다.
            if (IsEmptyContent(paragraph) && (isList || info.Kind is LineKind.Quote or LineKind.Heading))
            {
                ApplyKind(paragraph, isList && info.Level > 0 ? info with { Level = info.Level - 1, Checked = false } : new LineInfo(LineKind.Text));
                MoveCaret(ContentStart(paragraph));
                return true;
            }
            if (MarkerOf(paragraph) is { } marker && CaretPosition.CompareTo(marker.ElementEnd) < 0)
            {
                MoveCaret(marker.ElementEnd); // 기호 앞에서 나누지 않는다.
            }
            EditingCommands.EnterParagraphBreak.Execute(null, this);
            if (CurrentParagraph() is not { } next)
            {
                return true;
            }
            if (next.PreviousBlock is Paragraph previous)
            {
                ApplyKind(previous, info);
            }
            var nextInfo = info.Kind switch
            {
                LineKind.Heading when IsEmptyContent(next) => new LineInfo(LineKind.Text),
                LineKind.Task => info with { Checked = false },
                LineKind.Numbered => info with { Number = info.Number + 1 },
                LineKind.Code => info with { Language = null },
                LineKind.Blank => new LineInfo(LineKind.Text),
                _ => info,
            };
            RemoveMarkers(next);
            ApplyKind(next, nextInfo);
            MoveCaret(ContentStart(next));
            RefreshMarkers();
            return true;
        }
        finally
        {
            EndChange();
        }
    }

    private bool HandleBackspace()
    {
        FlushTypedText();
        if (!Selection.IsEmpty || CurrentParagraph() is not { } paragraph)
        {
            return false;
        }
        var info = Describe(paragraph);
        if (info.Kind is LineKind.Text or LineKind.Blank || CaretPosition.CompareTo(ContentStart(paragraph)) > 0)
        {
            return false;
        }
        // 문단 맨 앞 Backspace: 목록·제목·인용·코드를 보통 줄로.
        BeginChange();
        try
        {
            ApplyKind(paragraph, new LineInfo(LineKind.Text));
            MoveCaret(paragraph.ContentStart);
        }
        finally
        {
            EndChange();
        }
        RefreshMarkers();
        return true;
    }

    private bool HandleTab(bool outdent)
    {
        FlushTypedText();
        var paragraphs = SelectedParagraphs();
        var lists = paragraphs.Select(p => (Paragraph: p, Info: Describe(p))).Where(x => x.Info.Kind is LineKind.Bullet or LineKind.Numbered or LineKind.Task).ToList();
        if (lists.Count == 0)
        {
            if (outdent)
            {
                return true; // 보통 줄에서 Shift+Tab은 아무것도 안 한다 (들여쓰기를 저장할 수 없음).
            }
            Selection.Text = "\t";
            Selection.Select(Selection.End, Selection.End);
            return true;
        }
        BeginChange();
        try
        {
            foreach (var (paragraph, info) in lists)
            {
                ApplyKind(paragraph, info with { Level = Math.Clamp(info.Level + (outdent ? -1 : 1), 0, 8) });
            }
        }
        finally
        {
            EndChange();
        }
        RefreshMarkers();
        return true;
    }

    /// <summary>
    /// 빈 줄에 마크다운 기호 + 스페이스("- ", "1. ", "[] ", "# ", "> ", "``` ")를 치면 목록·번호·체크·제목·인용·코드로 바꾼다.
    /// 키를 누를 때가 아니라 스페이스가 들어간 뒤(TextChanged)에 본다: 그때는 글자가 확실히 들어가 있고,
    /// 줄 전체가 정확히 "기호 + 스페이스"인 문단만 바꾸므로 커서 위치를 잘못 짚어도 다른 줄이 바뀌지 않는다.
    /// </summary>
    private void AutoFormatAfterSpace()
    {
        var current = CurrentParagraph();
        var candidates = new[] { current, current?.PreviousBlock as Paragraph, current?.NextBlock as Paragraph };
        foreach (var paragraph in candidates.OfType<Paragraph>())
        {
            var info = Describe(paragraph);
            var text = PlainText(paragraph);
            if (!text.EndsWith(' ') || text.Length > 8 || info.Kind is not (LineKind.Text or LineKind.Bullet))
            {
                continue;
            }
            var typed = text[..^1];
            LineInfo? target = info.Kind == LineKind.Bullet
                ? typed is "[]" or "[ ]" ? info with { Kind = LineKind.Task } : typed is "[x]" or "[X]" ? info with { Kind = LineKind.Task, Checked = true } : null
                : typed switch
                {
                    "-" or "*" or "+" => new LineInfo(LineKind.Bullet),
                    "[]" or "[ ]" => new LineInfo(LineKind.Task),
                    "[x]" or "[X]" => new LineInfo(LineKind.Task, Checked: true),
                    ">" => new LineInfo(LineKind.Quote),
                    "```" => new LineInfo(LineKind.Code, Language: ""),
                    _ when HeadingPrefix().IsMatch(typed) => new LineInfo(LineKind.Heading, typed.Length),
                    _ when NumberPrefix().Match(typed) is { Success: true } number =>
                        new LineInfo(LineKind.Numbered, Number: int.Parse(number.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)),
                    _ => null,
                };
            if (target is null)
            {
                continue;
            }
            BeginChange();
            try
            {
                new TextRange(ContentStart(paragraph), paragraph.ContentEnd).Text = "";
                ApplyKind(paragraph, target);
                MoveCaret(ContentStart(paragraph));
            }
            finally
            {
                EndChange();
            }
            RefreshMarkers();
            return;
        }
        var shortLines = candidates.OfType<Paragraph>().Select(p => (Kind: Describe(p).Kind, Text: PlainText(p)))
            .Where(x => x.Text.Length is > 0 and <= 4 && x.Text.EndsWith(' ')).ToList();
        if (shortLines.Count > 0)
        {
            Log.Info($"자동 서식 안 됨: 커서 문단 {(current is null ? "없음" : "있음")}, 후보 {string.Join(", ", shortLines.Select(x => $"{x.Kind} '{x.Text}'"))}");
        }
    }

    [GeneratedRegex(@"^#{1,6}$")]
    private static partial Regex HeadingPrefix();

    [GeneratedRegex(@"^(\d{1,4})[.)]$")]
    private static partial Regex NumberPrefix();

    private static void RemoveMarkers(Paragraph paragraph)
    {
        while (MarkerOf(paragraph) is { } marker)
        {
            paragraph.Inlines.Remove(marker);
        }
    }

    // ---------- 링크 열기 (Ctrl+클릭) ----------

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && GetPositionFromPoint(e.GetPosition(this), snapToText: false) is { } position
            && FindAncestor<Hyperlink>(position.Parent) is { Tag: string link }
            && Uri.TryCreate(link, UriKind.Absolute, out var uri) && MarkdownEdit.IsSafeScheme(uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn($"링크를 열 수 없음: {uri}", ex);
            }
            e.Handled = true;
            return;
        }
        // 그림 크기 조절 손잡이는 그대로 Thumb이 받는다.
        if (_imageResize is not null && IsInside(e.OriginalSource as DependencyObject, _imageResize))
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }
        if (e.OriginalSource is Image { Tag: string tag, Parent: InlineUIContainer container } image && tag.StartsWith(ImagePrefix, StringComparison.Ordinal))
        {
            Focus();
            Selection.Select(container.ElementStart, container.ElementEnd); // Delete·복사가 그림에 바로 되도록
            ShowImageResize(image);
            e.Handled = true;
            return;
        }
        HideImageResize();
        if (IsInTableCellBox(e.OriginalSource))
        {
            base.OnPreviewMouseLeftButtonDown(e); // 고치고 있는 칸 안: 커서·선택은 글 상자가 한다
            return;
        }
        if (TableAt(e.OriginalSource as DependencyObject) is { } table)
        {
            if (MarkdownTableView.CellAt(e.OriginalSource as DependencyObject) is { } cell)
            {
                EditTableCell(table, Grid.GetRow(cell), Grid.GetColumn(cell), e.GetPosition(cell));
            }
            else if (_tableEdit?.Container == table)
            {
                FinishTableEdit(TableExit.SelectTable);
            }
            else
            {
                // 표 테두리: 표 전체를 고른다 (Delete·Ctrl+C·Ctrl+X가 표 전체에 되도록)
                FinishTableEdit(TableExit.Stay);
                Focus();
                Selection.Select(table.ElementStart, table.ElementEnd);
            }
            e.Handled = true;
            return;
        }
        FinishTableEdit(TableExit.Stay);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    /// <summary>누른 곳이 표면 그 표의 블록. 칸 글자(Run 등 Visual이 아닌 것)는 논리 트리로 올라간다.</summary>
    private static BlockUIContainer? TableAt(DependencyObject? element)
    {
        for (var current = element; current is not null;
             current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Parent: BlockUIContainer { Tag: MarkdownTable } container })
            {
                return container;
            }
        }
        return null;
    }

    /// <summary>선택 안에 통째로 들어간 표는 테두리로 강조한다.</summary>
    private void UpdateTableSelection()
    {
        foreach (var container in Document.Blocks.OfType<BlockUIContainer>())
        {
            if (container is { Tag: MarkdownTable, Child: Border outline })
            {
                MarkdownTableView.SetSelected(outline, IsTableSelected(container));
            }
        }
    }

    private bool IsTableSelected(BlockUIContainer container) =>
        !Selection.IsEmpty && Selection.Start.CompareTo(container.ContentStart) <= 0 && Selection.End.CompareTo(container.ContentEnd) >= 0;

    private static bool IsInside(DependencyObject? element, Visual ancestor)
    {
        // 글자를 누르면 OriginalSource가 Paragraph·Run(Visual이 아님)이라 VisualTreeHelper.GetParent가 예외를 낸다.
        // 손잡이는 모두 Visual이므로 Visual이 아닌 것은 adorner 밖이다.
        for (var current = element; current is Visual or System.Windows.Media.Media3D.Visual3D; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    // ---------- 그림 크기 조절 ----------

    private ImageResizeAdorner? _imageResize;

    private void ShowImageResize(Image image)
    {
        if (_imageResize?.Image == image)
        {
            return;
        }
        HideImageResize();
        if (AdornerLayer.GetAdornerLayer(image) is not { } layer)
        {
            return;
        }
        var adorner = new ImageResizeAdorner(image);
        adorner.Resized += width =>
        {
            if (image.Tag is string tag && tag.StartsWith(ImagePrefix, StringComparison.Ordinal))
            {
                var parts = tag[ImagePrefix.Length..].Split('\n', 3);
                image.Tag = ImageTag(parts[0], width, parts.Length > 2 ? parts[2] : "");
                // 그림 크기는 TextChanged가 생기지 않으므로 직접 변경을 알린다.
                _changeTimer.Stop();
                _changeTimer.Start();
            }
        };
        layer.Add(adorner);
        _imageResize = adorner;
    }

    private void HideImageResize()
    {
        if (_imageResize is { } adorner)
        {
            _imageResize = null;
            AdornerLayer.GetAdornerLayer(adorner.Image)?.Remove(adorner);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    // ---------- 붙여 넣기, 끌어다 놓기 ----------

    /// <summary>
    /// 붙여 넣기는 글자만 받는다 (다른 프로그램의 글꼴·크기·색이 섞이면 마크다운으로 저장할 수 없음).
    /// 글자 없이 그림만 있으면(화면 캡처, 그림 파일 복사) 메모 폴더에 저장해 넣는다.
    /// </summary>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (IsInTableCellBox(e.OriginalSource))
        {
            return; // 표 칸 글 상자: 글자 그대로 붙여 넣는다
        }
        var data = e.DataObject;
        // 엑셀 등에서 복사한 표는 표로 넣는다.
        if (!e.IsDragDrop && MarkdownTableView.FromClipboard(data) is { } table)
        {
            e.CancelCommand();
            Dispatcher.BeginInvoke(() => InsertTable(table));
            return;
        }
        if (data.GetDataPresent(DataFormats.UnicodeText, true))
        {
            var text = data.GetData(DataFormats.UnicodeText, true) as string ?? "";
            var plain = new DataObject();
            plain.SetData(DataFormats.UnicodeText, text);
            e.DataObject = plain;
            e.FormatToApply = DataFormats.UnicodeText;
            return;
        }
        e.CancelCommand();
        if (!PasteImagesEnabled())
        {
            return;
        }
        if (DroppedImageFiles(data) is { Count: > 0 } files)
        {
            Dispatcher.BeginInvoke(() => InsertImages(ImportImageFiles?.Invoke(files) ?? []));
        }
        else if (data.GetDataPresent(DataFormats.Bitmap, true) && data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (SavePastedImage?.Invoke(bitmap) is { } link)
                {
                    InsertImages([link]);
                }
            });
        }
    }

    /// <summary>Ctrl+V: 클립보드에 글자 없이 그림만 있으면 그림으로 넣는다 (RichTextBox 기본 붙여 넣기는 그림을 저장할 수 없음).</summary>
    private bool TryPasteImage()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || data.GetDataPresent(DataFormats.UnicodeText, true))
            {
                return false;
            }
            if (DroppedImageFiles(data).Count == 0 && !data.GetDataPresent(DataFormats.Bitmap, true))
            {
                return false;
            }
            var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.Bitmap);
            FlushTypedText();
            OnPasting(this, args);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("클립보드를 읽을 수 없음", ex);
            return false;
        }
    }

    /// <summary>
    /// 복사·잘라내기는 글자만 클립보드에 둔다 (목록 기호·체크 상자 같은 화면 요소를 다른 곳에 섞지 않도록).
    /// RichTextBox 기본 복사보다 먼저(Preview) 처리한다.
    /// </summary>
    private void OnPreviewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if ((e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) || IsInTableCellBox(e.OriginalSource))
        {
            return;
        }
        e.Handled = true;
        if (Selection.IsEmpty)
        {
            return;
        }
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, SelectedText()), copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("클립보드에 복사하지 못함", ex);
            return;
        }
        if (e.Command == ApplicationCommands.Cut)
        {
            Change(() => Selection.Text = "");
        }
    }

    /// <summary>선택한 글자. 표는 화면 요소라 Selection.Text에 없으므로 그 자리에 표의 마크다운을 넣는다.</summary>
    private string SelectedText()
    {
        var tables = Document.Blocks.OfType<BlockUIContainer>().Where(b => b.Tag is MarkdownTable && IsTableSelected(b)).ToList();
        if (tables.Count == 0)
        {
            return Selection.Text;
        }
        var parts = new List<string>();
        foreach (var block in Document.Blocks.Where(b => b.ContentEnd.CompareTo(Selection.Start) > 0 && b.ContentStart.CompareTo(Selection.End) < 0))
        {
            if (block is BlockUIContainer { Tag: MarkdownTable table } container && tables.Contains(container))
            {
                parts.Add(string.Join("\n", table.Write()));
            }
            else
            {
                var start = block.ContentStart.CompareTo(Selection.Start) < 0 ? Selection.Start : block.ContentStart;
                var end = block.ContentEnd.CompareTo(Selection.End) > 0 ? Selection.End : block.ContentEnd;
                parts.Add(new TextRange(start, end).Text.TrimEnd('\r', '\n'));
            }
        }
        return string.Join("\n", parts);
    }

    private static List<string> DroppedImageFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => NoteStore.ImageExtensions.Contains(System.IO.Path.GetExtension(f)) && System.IO.File.Exists(f))]
            : [];

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (DroppedImageFiles(e.Data).Count > 0 && !IsInTableCellBox(e.OriginalSource))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (DroppedImageFiles(e.Data) is { Count: > 0 } files && !IsInTableCellBox(e.OriginalSource))
        {
            FinishTableEdit(TableExit.Stay);
            if (GetPositionFromPoint(e.GetPosition(this), snapToText: true) is { } position)
            {
                CaretPosition = position;
            }
            InsertImages(ImportImageFiles?.Invoke(files) ?? []);
            e.Handled = true;
        }
    }

    // ---------- 서식 명령 (도구 모음, 단축키) ----------

    public void ToggleBold() => Change(() => EditingCommands.ToggleBold.Execute(null, this));

    public void ToggleItalic() => Change(() => EditingCommands.ToggleItalic.Execute(null, this));

    public void ToggleUnderline() => ToggleDecoration(TextDecorationLocation.Underline);

    public void ToggleStrike() => ToggleDecoration(TextDecorationLocation.Strikethrough);

    private void ToggleDecoration(TextDecorationLocation location)
    {
        var current = Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = current?.Any(d => d.Location == location) == true;
        var updated = new TextDecorationCollection(current?.Where(d => d.Location != location) ?? []);
        if (!has)
        {
            updated.Add(location == TextDecorationLocation.Underline ? TextDecorations.Underline : TextDecorations.Strikethrough);
        }
        Change(() => Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, updated));
    }

    public void ToggleHighlight()
    {
        var has = Selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush { Color.A: > 0 }
                  && !IsCodeSelection();
        Change(() => Selection.ApplyPropertyValue(TextElement.BackgroundProperty, has ? null : HighlightBrush()));
    }

    public void ToggleCode()
    {
        if (IsCodeSelection())
        {
            Change(() =>
            {
                Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, (FontFamily)FindResource("UiFont"));
                Selection.ApplyPropertyValue(TextElement.BackgroundProperty, null);
            });
            return;
        }
        Change(() =>
        {
            Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, CodeFont);
            Selection.ApplyPropertyValue(TextElement.BackgroundProperty, (Brush)FindResource("SubtleBrush"));
        });
    }

    private bool IsCodeSelection() => Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily family && IsCodeFont(family)
                                      && Describe(CurrentParagraph() ?? new Paragraph()).Kind != LineKind.Code;

    /// <summary>글자색. null이면 지운다.</summary>
    public void SetColor(string? hex)
    {
        if (hex is not null && ColorConverter.ConvertFromString(hex) is Color color)
        {
            Change(() => Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Frozen(color)));
            return;
        }
        // 지우기: 선택 범위에 걸친 글자 조각을 경계에서 나눈 뒤 로컬 색을 지운다.
        Change(() =>
        {
            Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
            var start = Selection.Start;
            var end = Selection.End;
            foreach (var paragraph in SelectedParagraphs())
            {
                ClearLocal(paragraph.Inlines, start, end, TextElement.ForegroundProperty);
            }
        });
    }

    private static void ClearLocal(InlineCollection inlines, TextPointer start, TextPointer end, DependencyProperty property)
    {
        foreach (var inline in inlines.ToList())
        {
            if (inline.ContentEnd.CompareTo(start) <= 0 || inline.ContentStart.CompareTo(end) >= 0)
            {
                continue;
            }
            inline.ClearValue(property);
            if (inline is Span span)
            {
                ClearLocal(span.Inlines, start, end, property);
            }
        }
    }

    /// <summary>선택한 글자를 링크로 (선택이 없으면 <paramref name="label"/>을 넣는다).</summary>
    public void InsertLink(string url, string? label)
    {
        Change(() =>
        {
            if (Selection.IsEmpty)
            {
                var text = string.IsNullOrWhiteSpace(label) ? url : label.Trim();
                var insertAt = CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
                var run = new Run(text, insertAt);
                Selection.Select(run.ContentStart, run.ContentEnd);
            }
            else if (!string.IsNullOrWhiteSpace(label) && label.Trim() != Selection.Text)
            {
                Selection.Text = label.Trim();
            }
            // 링크는 한 문단 안에서만 만들 수 있다.
            if (Selection.Start.Paragraph is { } first && Selection.End.CompareTo(first.ContentEnd) > 0)
            {
                Selection.Select(Selection.Start, first.ContentEnd);
            }
            Unlink(Selection.Start, Selection.End);
            var created = new Hyperlink(Selection.Start, Selection.End) { Tag = url, ToolTip = $"{url}\nCtrl+클릭으로 열기" };
            created.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            Selection.Select(created.ElementEnd, created.ElementEnd);
        });
    }

    /// <summary>선택 범위에 걸친 링크를 풀어 글자만 남긴다 (링크 안에 링크를 만들 수 없음).</summary>
    private void Unlink(TextPointer start, TextPointer end)
    {
        foreach (var paragraph in SelectedParagraphs())
        {
            foreach (var hyperlink in paragraph.Inlines.OfType<Hyperlink>().ToList())
            {
                if (hyperlink.ContentEnd.CompareTo(start) < 0 || hyperlink.ContentStart.CompareTo(end) > 0)
                {
                    continue;
                }
                foreach (var child in hyperlink.Inlines.ToList())
                {
                    hyperlink.Inlines.Remove(child);
                    paragraph.Inlines.InsertBefore(hyperlink, child);
                }
                paragraph.Inlines.Remove(hyperlink);
            }
        }
    }

    /// <summary>선택한 문단들을 목록·체크·인용으로. 모두 이미 그 종류면 보통 줄로 되돌린다.</summary>
    public void ToggleLineKind(LineKind kind)
    {
        var paragraphs = SelectedParagraphs();
        var all = paragraphs.All(p => Describe(p).Kind == kind);
        Change(() =>
        {
            foreach (var paragraph in paragraphs)
            {
                var info = Describe(paragraph);
                ApplyKind(paragraph, all ? new LineInfo(LineKind.Text) : new LineInfo(kind, info.Kind is LineKind.Bullet or LineKind.Numbered or LineKind.Task ? info.Level : 0, Number: 1));
            }
        });
    }

    /// <summary>제목 1~6, 0은 본문. 같은 제목을 다시 고르면 본문.</summary>
    public void SetHeading(int level)
    {
        var paragraphs = SelectedParagraphs();
        var same = level > 0 && paragraphs.All(p => Describe(p) is { Kind: LineKind.Heading } info && info.Level == level);
        Change(() =>
        {
            foreach (var paragraph in paragraphs)
            {
                ApplyKind(paragraph, level == 0 || same ? new LineInfo(LineKind.Text) : new LineInfo(LineKind.Heading, level));
            }
        });
    }

    /// <summary>지금 문단의 제목 수준 (0 = 본문). 제목 메뉴의 "본문"용.</summary>
    public int CurrentHeading => CurrentParagraph() is { } p && Describe(p) is { Kind: LineKind.Heading } info ? info.Level : 0;

    /// <summary>그림을 지금 문단 다음에 한 줄씩 넣는다 (지금 문단이 비어 있으면 그 자리에).</summary>
    public void InsertImages(IReadOnlyList<string> links)
    {
        if (links.Count == 0)
        {
            return;
        }
        Change(() =>
        {
            var current = CurrentParagraph();
            Paragraph? last = null;
            foreach (var link in links)
            {
                Paragraph target;
                if (last is null && current is not null && IsEmptyContent(current) && Describe(current).Kind is LineKind.Text or LineKind.Blank)
                {
                    target = current;
                }
                else
                {
                    target = NewParagraph();
                    var anchor = (Block?)last ?? current;
                    if (anchor is not null && anchor.Parent is FlowDocument)
                    {
                        Document.Blocks.InsertAfter(anchor, target);
                    }
                    else
                    {
                        Document.Blocks.Add(target);
                    }
                }
                target.Inlines.Add(CreateImage(link, ""));
                last = target;
            }
            var after = NewParagraph();
            Document.Blocks.InsertAfter(last!, after);
            MoveCaret(after.ContentStart);
        });
    }

    /// <summary>
    /// 표를 넣는다: 커서가 빈 줄이면 그 줄 대신, 줄 맨 앞이면 그 위에, 아니면 그 아래에. 마크다운에서 앞뒤 글과 붙지 않도록
    /// 빈 줄로 떼고, 커서는 표 다음 빈 줄로 간다.
    /// </summary>
    public void InsertTable(MarkdownTable table)
    {
        Change(() =>
        {
            if (!Selection.IsEmpty)
            {
                Selection.Text = "";
            }
            var block = CreateBlock(new MarkdownLine(LineKind.Table, [], Table: table));
            var current = CurrentParagraph();
            if (current is null || current.Parent is not FlowDocument)
            {
                Document.Blocks.Add(block);
            }
            else if (IsEmptyContent(current) && Describe(current).Kind is LineKind.Text or LineKind.Blank)
            {
                Document.Blocks.InsertAfter(current, block);
                Document.Blocks.Remove(current);
            }
            else if (CaretPosition.CompareTo(ContentStart(current)) <= 0)
            {
                Document.Blocks.InsertBefore(current, block);
            }
            else
            {
                Document.Blocks.InsertAfter(current, block);
            }
            if (block.PreviousBlock is { } previous && !(previous is Paragraph p && IsEmptyContent(p)))
            {
                Document.Blocks.InsertBefore(block, NewParagraph());
            }
            var next = block.NextBlock as Paragraph;
            if (next is null || !IsEmptyContent(next))
            {
                next = NewParagraph();
                Document.Blocks.InsertAfter(block, next);
            }
            MoveCaret(next.ContentStart);
        });
    }

    /// <summary>한 번의 실행 취소 단위로 고치고, 바로 마크다운에 반영한다.</summary>
    private void Change(Action change)
    {
        BeginChange();
        try
        {
            change();
        }
        finally
        {
            EndChange();
        }
        Focus();
        Flush();
    }

    // ---------- 찾기 / 바꾸기 ----------

    /// <summary>선택 뒤에서부터 찾는다 (끝까지 가면 처음부터). 글자 조각 하나 안에서만 찾는다.</summary>
    public bool FindNext(string query)
    {
        if (query.Length == 0)
        {
            return false;
        }
        var matches = Matches(query).ToList();
        if (matches.Count == 0)
        {
            return false;
        }
        var next = matches.FirstOrDefault(m => m.Start.CompareTo(Selection.End) >= 0) ?? matches[0];
        Selection.Select(next.Start, next.End);
        next.Start.Paragraph?.BringIntoView();
        Focus();
        return true;
    }

    public int CountMatches(string query) => query.Length == 0 ? 0 : Matches(query).Count();

    /// <summary>지금 선택이 찾는 말이면 바꾸고 다음을 찾는다.</summary>
    public void ReplaceSelection(string query, string replacement)
    {
        if (!Selection.IsEmpty && string.Equals(Selection.Text, query, StringComparison.CurrentCultureIgnoreCase))
        {
            Change(() => Selection.Text = replacement);
        }
        FindNext(query);
    }

    public int ReplaceAll(string query, string replacement)
    {
        var matches = Matches(query).ToList();
        if (matches.Count == 0)
        {
            return 0;
        }
        Change(() =>
        {
            // 뒤에서부터 바꿔야 앞의 위치가 그대로다.
            foreach (var match in Enumerable.Reverse(matches))
            {
                match.Text = replacement;
            }
        });
        return matches.Count;
    }

    private IEnumerable<TextRange> Matches(string query)
    {
        foreach (var run in AllParagraphs().SelectMany(p => Runs(p.Inlines)).ToList())
        {
            var text = run.Text;
            for (var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); index >= 0;
                 index = text.IndexOf(query, index + query.Length, StringComparison.CurrentCultureIgnoreCase))
            {
                yield return new TextRange(run.ContentStart.GetPositionAtOffset(index), run.ContentStart.GetPositionAtOffset(index + query.Length));
            }
        }
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (var inner in Runs(span.Inlines))
                {
                    yield return inner;
                }
            }
        }
    }
}
