using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using DayDeck.App.Services;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 옵시디언 모드 편집기 ("라이브 미리보기"). 내용은 마크다운 원문 그대로이고, 서식만 입혀 보인다.
/// 커서가 있는 줄은 서식 기호(**, #, [링크](주소) 등)를 흐리게 보이고, 다른 줄은 기호를 숨기거나
/// 글머리표·체크 상자·가로줄·그림으로 바꿔 보인다 (<see cref="LivePreview"/>).
/// 원문을 그대로 고치므로 실행 취소·찾기·서식 도구는 원문 편집과 같고, 저장도 바뀐 그대로다.
/// </summary>
internal sealed class LivePreviewEditor : TextEditor, IMarkdownTarget
{
    private const string IndentUnit = "  ";

    private readonly Func<string, string?> _resolveImage;
    private readonly Dictionary<int, (string Text, byte State, LiveLine Line)> _parsed = [];
    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _codeStates = [];
    private int _activeFirst = -1;
    private int _activeLast = -1;
    private bool _loading;

    public LivePreviewEditor(Func<string, string?> resolveImage)
    {
        _resolveImage = resolveImage;
        WordWrap = true;
        ShowLineNumbers = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(24, 16, 24, 16);
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.EnableRectangularSelection = false;
        Options.HighlightCurrentLine = false;
        Options.AllowScrollBelowDocument = false;
        Options.InheritWordWrapIndentation = true;
        Options.CutCopyWholeLine = false;
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TextArea.SelectionCornerRadius = 2;
        TextArea.SelectionBorder = null;
        ApplyThemeBrushes();

        TextArea.TextView.LineTransformers.Add(new Colorizer(this));
        TextArea.TextView.ElementGenerators.Add(new MarkerGenerator(this));
        TextArea.TextView.BackgroundRenderers.Add(new BlockRenderer(this));

        Document.Changed += (_, _) =>
        {
            _parsed.Clear();
            UpdateCodeStates();
            if (!_loading)
            {
                MarkdownChanged?.Invoke(this, EventArgs.Empty);
            }
        };
        TextArea.Caret.PositionChanged += (_, _) => UpdateActiveLines();
        TextArea.SelectionChanged += (_, _) => UpdateActiveLines();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateActiveLines();
        TextArea.PreviewKeyDown += OnPreviewKeyDownInText;
        TextArea.PreviewMouseLeftButtonDown += OnPreviewMouseDownInText;
        Loaded += (_, _) => ThemeService.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
        ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "실행 취소", Command = ApplicationCommands.Undo, InputGestureText = "Ctrl+Z" },
                new MenuItem { Header = "다시 실행", Command = ApplicationCommands.Redo, InputGestureText = "Ctrl+Y" },
                new Separator(),
                new MenuItem { Header = "잘라내기", Command = ApplicationCommands.Cut, InputGestureText = "Ctrl+X" },
                new MenuItem { Header = "복사", Command = ApplicationCommands.Copy, InputGestureText = "Ctrl+C" },
                new MenuItem { Header = "붙여넣기", Command = ApplicationCommands.Paste, InputGestureText = "Ctrl+V" },
                new Separator(),
                new MenuItem { Header = "모두 선택", Command = ApplicationCommands.SelectAll, InputGestureText = "Ctrl+A" },
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(this, "메모 편집기 (옵시디언 모드)");
        this.AutomationId("NoteLiveEditor");
    }

    /// <summary>원문이 바뀌었다 (입력, 서식 도구, 체크 상자). <see cref="Load"/>로 읽을 때는 알리지 않는다.</summary>
    public event EventHandler? MarkdownChanged;

    /// <summary>위키 링크(<c>[[메모]]</c>)를 Ctrl+클릭했을 때. 메모를 열었으면 true.</summary>
    public Func<string, bool>? OpenWikiLink { get; set; }

    public string Markdown => Text;

    /// <summary>원문을 새로 읽는다 (실행 취소 기록은 지운다).</summary>
    public void LoadMarkdown(string markdown)
    {
        _loading = true;
        try
        {
            Document.Text = markdown;
            Document.UndoStack.ClearAll();
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------- IMarkdownTarget ----------

    int IMarkdownTarget.CaretOffset => CaretOffset;

    FrameworkElement IMarkdownTarget.Element => this;

    bool IMarkdownTarget.Focus()
    {
        var focused = TextArea.Focus();
        return focused || Focus();
    }

    public void ScrollToOffset(int offset)
    {
        var location = Document.GetLocation(Math.Clamp(offset, 0, Document.TextLength));
        ScrollTo(location.Line, location.Column);
    }

    // ---------- 줄 해석 ----------

    internal LiveLine Parse(DocumentLine line)
    {
        var state = line.LineNumber - 1 < _codeStates.Length ? _codeStates[line.LineNumber - 1] : (byte)0;
        var text = Document.GetText(line);
        if (_parsed.TryGetValue(line.LineNumber, out var cached) && cached.State == state && cached.Text == text)
        {
            return cached.Line;
        }
        var parsed = LivePreview.ParseLine(text, state);
        _parsed[line.LineNumber] = (text, state, parsed);
        return parsed;
    }

    /// <summary>커서(또는 선택)가 걸친 줄인지. 이 줄은 서식 기호를 그대로 보인다. 편집기에 초점이 없으면 모든 줄을 다 그린다.</summary>
    internal bool IsActive(int lineNumber) => lineNumber >= _activeFirst && lineNumber <= _activeLast;

    private void UpdateCodeStates()
    {
        var lines = Document.Lines.Select(l => Document.GetText(l)).ToList();
        var states = LivePreview.CodeBlockStates(lines);
        if (!states.AsSpan().SequenceEqual(_codeStates))
        {
            _codeStates = states;
            // 코드 울타리가 생기거나 없어지면 아래 줄들이 모두 바뀐다.
            Dispatcher.BeginInvoke(() => TextArea.TextView.Redraw());
        }
    }

    private void UpdateActiveLines()
    {
        int first, last;
        if (!IsKeyboardFocusWithin)
        {
            first = last = -1;
        }
        else if (TextArea.Selection.IsEmpty)
        {
            first = last = TextArea.Caret.Line;
        }
        else
        {
            var segment = TextArea.Selection.SurroundingSegment;
            first = Document.GetLineByOffset(segment.Offset).LineNumber;
            last = Document.GetLineByOffset(segment.EndOffset).LineNumber;
        }
        if (first == _activeFirst && last == _activeLast)
        {
            return;
        }
        var oldFirst = _activeFirst;
        var oldLast = _activeLast;
        _activeFirst = first;
        _activeLast = last;
        RedrawLines(oldFirst, oldLast);
        RedrawLines(first, last);
    }

    private void RedrawLines(int first, int last)
    {
        if (first < 1)
        {
            return;
        }
        for (var n = first; n <= Math.Min(last, Document.LineCount); n++)
        {
            TextArea.TextView.Redraw(Document.GetLineByNumber(n));
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyThemeBrushes();
        TextArea.TextView.Redraw();
    }

    private void ApplyThemeBrushes()
    {
        var accent = ThemeService.Color("AccentBrush");
        var selection = new SolidColorBrush(Color.FromArgb(0x48, accent.R, accent.G, accent.B));
        selection.Freeze();
        TextArea.SelectionBrush = selection;
        TextArea.SelectionForeground = null;
        TextArea.Caret.CaretBrush = ThemeService.Brush("TextBrush");
    }

    // ---------- 입력 ----------

    private void OnPreviewKeyDownInText(object sender, KeyEventArgs e)
    {
        if (MarkdownCommands.HandleShortcut(this, e))
        {
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Enter && TextArea.Selection.IsEmpty && ContinueList())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift && IndentListLines(Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
        }
    }

    /// <summary>목록·번호·체크·인용 줄에서 Enter: 다음 줄에 같은 기호를 이어 쓴다. 빈 항목이면 기호를 지워 목록을 끝낸다.</summary>
    private bool ContinueList()
    {
        var line = Document.GetLineByOffset(CaretOffset);
        var text = Document.GetText(line);
        var column = CaretOffset - line.Offset;
        var parsed = Parse(line);
        if (parsed.Kind is LiveLineKind.Code or LiveLineKind.CodeFence or LiveLineKind.Table)
        {
            return false;
        }
        if (LivePreview.ContinuePrefix(text, out var endList) is not { } prefix)
        {
            return false;
        }
        var contentStart = text.Length - text.TrimStart().Length;
        if (column <= contentStart)
        {
            return false; // 기호 앞에서 누른 Enter는 그냥 줄바꿈
        }
        if (endList)
        {
            Document.Replace(line.Offset, line.Length, "");
            return true;
        }
        Document.Insert(CaretOffset, "\n" + prefix);
        return true;
    }

    /// <summary>목록 줄에서 Tab/Shift+Tab: 선택한 목록 줄들을 한 단계 들이거나 내어 쓴다.</summary>
    private bool IndentListLines(bool outdent)
    {
        var first = Document.GetLineByOffset(SelectionStart);
        var last = Document.GetLineByOffset(SelectionStart + SelectionLength);
        var lines = new List<DocumentLine>();
        for (var line = first; line is not null; line = line.NextLine)
        {
            lines.Add(line);
            if (line == last)
            {
                break;
            }
        }
        if (!lines.All(l => Parse(l).Kind is LiveLineKind.Bullet or LiveLineKind.Numbered or LiveLineKind.Task))
        {
            return false;
        }
        Document.BeginUpdate();
        try
        {
            foreach (var line in lines)
            {
                var text = Document.GetText(line);
                if (!outdent)
                {
                    Document.Insert(line.Offset, IndentUnit);
                }
                else if (text.StartsWith('\t'))
                {
                    Document.Remove(line.Offset, 1);
                }
                else
                {
                    var spaces = text.Length - text.TrimStart(' ').Length;
                    if (spaces > 0)
                    {
                        Document.Remove(line.Offset, Math.Min(spaces, IndentUnit.Length));
                    }
                }
            }
        }
        finally
        {
            Document.EndUpdate();
        }
        return true;
    }

    /// <summary>Ctrl+클릭: 링크 열기 (웹·메일 주소만, 위키 링크는 그 메모).</summary>
    private void OnPreviewMouseDownInText(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || GetPositionFromPoint(e.GetPosition(this)) is not { } position)
        {
            return;
        }
        var offset = Document.GetOffset(position.Location);
        var line = Document.GetLineByOffset(offset);
        var column = offset - line.Offset;
        var span = Parse(line).Spans.FirstOrDefault(s => s.Style.HasFlag(LiveStyle.Link) && column >= s.Start && column <= s.End);
        if (span?.Url is not { } url)
        {
            return;
        }
        e.Handled = true;
        if (span.IsWiki)
        {
            OpenWikiLink?.Invoke(url);
            return;
        }
        if (MarkdownEdit.NormalizeUrl(url.Trim('<', '>')) is { } normalized && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && MarkdownEdit.IsSafeScheme(uri))
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn($"링크를 열 수 없음: {uri}", ex);
            }
        }
    }

    // ---------- 바꿔 보이는 요소 ----------

    private void ToggleCheckbox(int lineNumber)
    {
        if (lineNumber > Document.LineCount)
        {
            return;
        }
        var line = Document.GetLineByNumber(lineNumber);
        if (LivePreview.ToggleCheckbox(Document.GetText(line)) is { } toggled)
        {
            Document.Replace(line.Offset, line.Length, toggled);
        }
    }

    internal UIElement CreateCheckbox(int lineNumber, bool done)
    {
        var box = new CheckBox
        {
            IsChecked = done,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, done ? "완료한 항목" : "할 항목");
        // 누르는 순간 바꾼다 (그 줄로 커서가 옮겨 가면 체크 상자가 원문으로 바뀌기 때문).
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ToggleCheckbox(lineNumber);
            e.Handled = true;
        };
        return box;
    }

    internal UIElement CreateRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderStrongBrush");
        rule.SetBinding(WidthProperty, new Binding(nameof(ActualWidth))
        {
            Source = TextArea.TextView,
            Converter = new MinusConverter(8),
        });
        return rule;
    }

    internal UIElement? CreateImage(LiveMarker marker)
    {
        var link = marker.Data ?? "";
        if (!_images.TryGetValue(link, out var source))
        {
            var path = _resolveImage(link);
            if (path is null && !link.Contains('/') && !link.Contains('\\'))
            {
                path = _resolveImage(".images/" + link); // ![[그림.png]]: 그림 폴더에서도 찾는다
            }
            source = path is not null && System.IO.File.Exists(path) ? MarkdownRenderer.LoadImage(path) : null;
            _images[link] = source;
        }
        if (source is null)
        {
            return null;
        }
        var width = marker.Width > 0 ? marker.Width : Math.Min(source.Width, MarkdownRenderer.MaxImageWidth);
        var image = new Image
        {
            Source = source,
            Width = width,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "그림 — 원문을 고치려면 그 줄을 누르세요",
        };
        System.Windows.Automation.AutomationProperties.SetName(image, "그림");
        return image;
    }

    /// <summary>그림 파일이 바뀌었을 수 있으니 다시 읽는다 (탭을 다시 보일 때).</summary>
    public void RefreshImages()
    {
        _images.Clear();
        TextArea.TextView.Redraw();
    }

    internal static double HeadingScale(int level) => level switch
    {
        1 => 1.75,
        2 => 1.5,
        3 => 1.3,
        4 => 1.15,
        5 => 1.05,
        _ => 1.0,
    };

    private sealed class MinusConverter(double amount) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double d ? Math.Max(20, d - amount) : 100.0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>커서가 없는 줄에서 서식 기호를 숨긴다 (글자 폭 0).</summary>
    private sealed class HiddenElement(int documentLength) : VisualLineElement(1, documentLength)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new TextHidden(1);
    }

    /// <summary>커서가 없는 줄의 기호를 숨기거나 글머리표·체크 상자·가로줄·그림으로 바꾼다.</summary>
    private sealed class MarkerGenerator(LivePreviewEditor owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var document = CurrentContext.Document;
            var line = document.GetLineByOffset(startOffset);
            if (owner.IsActive(line.LineNumber))
            {
                return -1;
            }
            var column = startOffset - line.Offset;
            foreach (var marker in owner.Parse(line).Markers)
            {
                if (marker.Start >= column)
                {
                    return line.Offset + marker.Start;
                }
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            var column = offset - line.Offset;
            var parsed = owner.Parse(line);
            var marker = parsed.Markers.FirstOrDefault(m => m.Start == column);
            if (marker is null || marker.End > line.Length)
            {
                return null;
            }
            if (parsed.Kind == LiveLineKind.Quote && marker.Start == 0)
            {
                // 인용 기호 자리는 왼쪽 막대(BlockRenderer)를 그릴 만큼 비워 둔다.
                return new FormattedTextElement(new string(' ', 3 * Math.Max(1, parsed.Level)), marker.Length);
            }
            return marker.Kind switch
            {
                LiveReplace.Hide => new HiddenElement(marker.Length),
                LiveReplace.Bullet => new FormattedTextElement("•", marker.Length),
                LiveReplace.Checkbox => new InlineObjectElement(marker.Length, owner.CreateCheckbox(line.LineNumber, done: false)),
                LiveReplace.CheckboxDone => new InlineObjectElement(marker.Length, owner.CreateCheckbox(line.LineNumber, done: true)),
                LiveReplace.Rule => new InlineObjectElement(marker.Length, owner.CreateRule()),
                LiveReplace.Image => owner.CreateImage(marker) is { } image ? new InlineObjectElement(marker.Length, image) : null,
                _ => null,
            };
        }
    }

    /// <summary>제목 크기, 굵게·기울임·취소선·코드·형광펜·링크·글자색, 서식 기호는 흐리게.</summary>
    private sealed class Colorizer(LivePreviewEditor owner) : DocumentColorizingTransformer
    {
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, D2Coding, Malgun Gothic");

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0)
            {
                return;
            }
            var parsed = owner.Parse(line);
            var start = line.Offset;
            var end = line.EndOffset;
            switch (parsed.Kind)
            {
                case LiveLineKind.Heading:
                    var size = owner.FontSize * HeadingScale(parsed.Level);
                    ChangeLinePart(start, end, e =>
                    {
                        e.TextRunProperties.SetFontRenderingEmSize(size);
                        SetTypeface(e, weight: FontWeights.Bold);
                    });
                    break;
                case LiveLineKind.Code or LiveLineKind.CodeFence or LiveLineKind.Table:
                    ChangeLinePart(start, end, e => SetTypeface(e, family: Mono));
                    break;
                case LiveLineKind.Quote:
                    ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(ThemeService.Brush("SubTextBrush")));
                    break;
            }

            foreach (var span in parsed.Spans)
            {
                if (span.Length <= 0 || span.End > line.Length)
                {
                    continue;
                }
                var style = span.Style;
                var color = span.Color is { } hex ? TaskRowViewModelBrush(hex) : null;
                ChangeLinePart(start + span.Start, start + span.End, e => Apply(e, style, color));
            }
        }

        private static Brush? TaskRowViewModelBrush(string hex) => ViewModels.TaskRowViewModel.TryParseBrush(hex);

        private static void Apply(VisualLineElement e, LiveStyle style, Brush? color)
        {
            var props = e.TextRunProperties;
            if (color is not null)
            {
                props.SetForegroundBrush(color);
            }
            if (style.HasFlag(LiveStyle.Bold))
            {
                SetTypeface(e, weight: FontWeights.Bold);
            }
            if (style.HasFlag(LiveStyle.Italic))
            {
                SetTypeface(e, style: FontStyles.Italic);
            }
            if (style.HasFlag(LiveStyle.Strike))
            {
                AddDecorations(e, TextDecorations.Strikethrough);
            }
            if (style.HasFlag(LiveStyle.Underline))
            {
                AddDecorations(e, TextDecorations.Underline);
            }
            if (style.HasFlag(LiveStyle.Code))
            {
                SetTypeface(e, family: Mono);
                props.SetBackgroundBrush(ThemeService.Brush("ChipBrush"));
            }
            if (style.HasFlag(LiveStyle.Highlight))
            {
                props.SetBackgroundBrush(HighlightBrush);
                props.SetForegroundBrush(HighlightText);
            }
            if (style.HasFlag(LiveStyle.Link))
            {
                props.SetForegroundBrush(ThemeService.Brush("AccentBrush"));
                AddDecorations(e, TextDecorations.Underline);
            }
            if (style.HasFlag(LiveStyle.Done))
            {
                props.SetForegroundBrush(ThemeService.Brush("MutedBrush"));
                AddDecorations(e, TextDecorations.Strikethrough);
            }
            if (style.HasFlag(LiveStyle.Marker))
            {
                props.SetForegroundBrush(ThemeService.Brush("MutedBrush"));
            }
        }

        private static readonly Brush HighlightBrush = Frozen(Color.FromRgb(0xFF, 0xF3, 0xA3));
        private static readonly Brush HighlightText = Frozen(Color.FromRgb(0x1B, 0x1B, 0x1B));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void SetTypeface(VisualLineElement e, FontWeight? weight = null, FontStyle? style = null, FontFamily? family = null)
        {
            var current = e.TextRunProperties.Typeface;
            e.TextRunProperties.SetTypeface(new Typeface(family ?? current.FontFamily, style ?? current.Style, weight ?? current.Weight, current.Stretch));
        }

        private static void AddDecorations(VisualLineElement e, TextDecorationCollection add)
        {
            var merged = new TextDecorationCollection();
            if (e.TextRunProperties.TextDecorations is { } existing)
            {
                foreach (var decoration in existing)
                {
                    merged.Add(decoration);
                }
            }
            foreach (var decoration in add)
            {
                merged.Add(decoration);
            }
            merged.Freeze();
            e.TextRunProperties.SetTextDecorations(merged);
        }
    }

    /// <summary>코드 블록 배경, 인용 왼쪽 막대, 제목 1·2 아래 줄.</summary>
    private sealed class BlockRenderer(LivePreviewEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid)
            {
                return;
            }
            var codeBrush = ThemeService.Brush("SubtleBrush");
            var quoteBrush = ThemeService.Brush("BorderStrongBrush");
            var linePen = new Pen(ThemeService.Brush("LineBrush"), 1);
            linePen.Freeze();
            var width = textView.ActualWidth;
            foreach (var visual in textView.VisualLines)
            {
                var line = visual.FirstDocumentLine;
                var parsed = owner.Parse(line);
                var top = visual.VisualTop - textView.ScrollOffset.Y;
                var height = visual.Height;
                switch (parsed.Kind)
                {
                    case LiveLineKind.Code or LiveLineKind.CodeFence:
                        drawingContext.DrawRectangle(codeBrush, null, new Rect(0, top, width, height));
                        break;
                    case LiveLineKind.Quote when !owner.IsActive(line.LineNumber):
                        for (var level = 0; level < Math.Max(1, parsed.Level); level++)
                        {
                            drawingContext.DrawRectangle(quoteBrush, null, new Rect(2 + level * 12, top, 3, height));
                        }
                        break;
                    case LiveLineKind.Heading when parsed.Level <= 2 && !owner.IsActive(line.LineNumber):
                        var y = Math.Round(top + height) - 0.5;
                        drawingContext.DrawLine(linePen, new Point(0, y), new Point(width, y));
                        break;
                }
            }
        }
    }
}
