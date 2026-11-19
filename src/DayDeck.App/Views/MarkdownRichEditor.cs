using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DayDeck.App.Services;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 메모 "보기": 서식이 적용된 모양 그대로 고치는 편집기. 파일은 여전히 마크다운이며, 문단 하나가 마크다운 한 줄이다
/// (<see cref="MarkdownLines"/>). 고치면 잠시 뒤 마크다운으로 바꿔 <see cref="MarkdownChanged"/>로 알린다.
/// 목록 기호·체크 상자·번호는 문단 맨 앞의 작은 요소이고, 제목·인용·코드는 문단 표시(Tag)로 구분한다.
/// 글자 서식은 Run의 로컬 값(굵기, 기울임, 밑줄/취소선, 배경, 글자색, 코드 글꼴)을 그대로 읽는다.
/// </summary>
internal sealed partial class MarkdownRichEditor : RichTextBox
{
    public const string EditorAutomationId = "NoteRichEditor";

    private const string MarkerPrefix = "marker|";
    private const string ImagePrefix = "image\n";
    private const string CodeTag = "code";
    private const string QuoteTag = "quote";
    private const string RuleTag = "rule";
    private const double IndentStep = 22;
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, D2Coding, Malgun Gothic");

    private readonly Func<string, string?> _resolveImage;
    private readonly DispatcherTimer _changeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;
    private bool _autoFormatting;

    /// <param name="resolveImage">그림 경로 → 실제 파일 (메모 폴더 안만).</param>
    public MarkdownRichEditor(Func<string, string?> resolveImage)
    {
        _resolveImage = resolveImage;
        AcceptsReturn = true;
        AcceptsTab = true;
        IsDocumentEnabled = true; // 체크 상자를 누를 수 있게
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SpellCheck.SetIsEnabled(this, false);
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "메모 보기 편집기");
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, EditorAutomationId);

        _changeTimer.Tick += (_, _) => Flush();
        TextChanged += (_, e) =>
        {
            // 표 칸 글 상자의 TextChanged도 여기로 올라온다 (그 글자는 칸 고치기가 따로 다룬다).
            if (_loading || e.OriginalSource != this)
            {
                return;
            }
            _changeTimer.Stop();
            _changeTimer.Start();
            // 글자가 들어왔으면 바로 본다 (미루면 다음 글자가 먼저 들어와 "기호 + 스페이스"만 있는 줄이 아니게 된다).
            // 스페이스는 RichTextBox가 TextInput을 거치지 않고 넣을 때가 있어 입력 이벤트 대신 바뀐 내용으로 판단한다.
            if (!_autoFormatting && e.Changes.Any(c => c.AddedLength > 0 && c.RemovedLength == 0))
            {
                _autoFormatting = true;
                try
                {
                    AutoFormatAfterSpace();
                }
                finally
                {
                    _autoFormatting = false;
                }
            }
        };
        DataObject.AddPastingHandler(this, OnPasting);
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewCommand);
        SelectionChanged += (_, _) => UpdateTableSelection();
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
    }

    /// <summary>마지막으로 읽거나 만든 마크다운.</summary>
    public string Markdown { get; private set; } = "";

    /// <summary>사용자가 고쳐서 <see cref="Markdown"/>이 바뀌었다.</summary>
    public event EventHandler? MarkdownChanged;

    /// <summary>그림 파일들을 메모 폴더로 복사하고 메모에서 쓸 경로들을 돌려준다 (끌어다 놓기).</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? ImportImageFiles { get; set; }

    /// <summary>붙여 넣은 그림을 저장하고 경로를 돌려준다.</summary>
    public Func<BitmapSource, string?>? SavePastedImage { get; set; }

    /// <summary>그림 붙여 넣기를 할지 (설정 "Ctrl+V로 그림 붙여 넣기").</summary>
    public Func<bool> PasteImagesEnabled { get; set; } = () => true;

    /// <summary>글자 수 (상태 표시줄).</summary>
    public int TextLength => new TextRange(Document.ContentStart, Document.ContentEnd).Text.Length;

    // ---------- 읽기 / 쓰기 ----------

    public void Load(string markdown)
    {
        _tableEdit = null;
        _loading = true;
        try
        {
            var document = new FlowDocument { PagePadding = new Thickness(24, 16, 24, 24), FontSize = 14, TextAlignment = TextAlignment.Left };
            document.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            foreach (var line in MarkdownLines.Parse(markdown))
            {
                document.Blocks.Add(CreateBlock(line));
            }
            if (document.Blocks.Count == 0)
            {
                document.Blocks.Add(NewParagraph());
            }
            HideImageResize();
            Document = document;
            // 줄바꿈만 다른(\r\n) 내용을 고친 것으로 보지 않도록 \n으로 맞춰 둔다 (Flush는 \n으로 쓴 결과와 비교한다).
            Markdown = markdown.Replace("\r\n", "\n");
            RefreshMarkers();
        }
        finally
        {
            _loading = false;
        }
        _changeTimer.Stop();
    }

    /// <summary>기다리던 변경을 바로 마크다운으로 반영한다 (저장, 보기 전환, 창 닫기 전).</summary>
    public void Flush()
    {
        _changeTimer.Stop();
        RefreshMarkers();
        var markdown = MarkdownLines.Write(ToLines());
        if (markdown != Markdown)
        {
            Markdown = markdown;
            MarkdownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<MarkdownLine> ToLines()
    {
        var lines = new List<MarkdownLine>();
        var numbers = ComputeNumbers();
        foreach (var block in Document.Blocks)
        {
            AddLines(block, lines, numbers);
        }
        return lines;
    }

    /// <summary>선택한 문단(없으면 커서가 있는 문단)들의 마크다운 줄 (할 일로 만들기).</summary>
    public IEnumerable<string> SelectedMarkdownLines()
    {
        var numbers = ComputeNumbers();
        return SelectedParagraphs().Select(p => MarkdownLines.WriteLine(LineFor(p, numbers)));
    }

    private void AddLines(Block block, List<MarkdownLine> lines, Dictionary<Paragraph, int> numbers)
    {
        switch (block)
        {
            case BlockUIContainer { Tag: RuleTag }:
                lines.Add(new MarkdownLine(LineKind.Rule, []));
                break;
            case BlockUIContainer { Tag: MarkdownTable table } container:
                lines.Add(new MarkdownLine(LineKind.Table, [], Table: EditingTable(container) ?? table));
                break;
            case Paragraph paragraph:
                var line = LineFor(paragraph, numbers);
                // 코드 블록의 언어 표시는 이어지는 코드 줄의 첫 줄에만 둔다.
                if (line.Kind == LineKind.Code && lines.Count > 0 && lines[^1].Kind == LineKind.Code)
                {
                    line = line with { Language = null };
                }
                lines.Add(line);
                break;
            case Section section:
                foreach (var child in section.Blocks)
                {
                    AddLines(child, lines, numbers);
                }
                break;
            case List list:
                foreach (var item in list.ListItems)
                {
                    foreach (var child in item.Blocks)
                    {
                        AddLines(child, lines, numbers);
                    }
                }
                break;
            case Table or BlockUIContainer:
                var text = new TextRange(block.ContentStart, block.ContentEnd).Text.Replace("\r\n", "\n");
                lines.AddRange(text.Split('\n').Select(t => t.Length == 0 ? MarkdownLine.Blank : new MarkdownLine(LineKind.Text, [new InlineRun(t)])));
                break;
        }
    }

    private MarkdownLine LineFor(Paragraph paragraph, Dictionary<Paragraph, int> numbers)
    {
        var info = Describe(paragraph);
        if (info.Kind == LineKind.Code)
        {
            return new MarkdownLine(LineKind.Code, [new InlineRun(PlainText(paragraph))], Language: info.Language);
        }
        var inlines = ExtractInlines(paragraph);
        if (info.Kind == LineKind.Text && inlines.All(r => r.Image is null && r.Text.Trim().Length == 0))
        {
            return MarkdownLine.Blank;
        }
        return new MarkdownLine(info.Kind, inlines, info.Level, numbers.GetValueOrDefault(paragraph, info.Number), info.Checked);
    }

    private sealed record LineInfo(LineKind Kind, int Level = 0, int Number = 0, bool Checked = false, string? Language = null);

    private static LineInfo Describe(Paragraph paragraph)
    {
        if (MarkerOf(paragraph) is { } marker && ParseMarker(marker) is { } info)
        {
            return info;
        }
        return paragraph.Tag switch
        {
            string tag when tag.Length == 2 && tag[0] == 'h' && char.IsAsciiDigit(tag[1]) => new LineInfo(LineKind.Heading, tag[1] - '0'),
            QuoteTag => new LineInfo(LineKind.Quote),
            string tag when tag == CodeTag || tag.StartsWith(CodeTag + ":", StringComparison.Ordinal) =>
                new LineInfo(LineKind.Code, Language: tag.Length > CodeTag.Length ? tag[(CodeTag.Length + 1)..] : (tag == CodeTag + ":" ? "" : null)),
            _ => new LineInfo(LineKind.Text),
        };
    }

    // ---------- 문단 만들기 ----------

    private Block CreateBlock(MarkdownLine line)
    {
        if (line.Kind == LineKind.Rule)
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
            rule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            return new BlockUIContainer(rule) { Tag = RuleTag };
        }
        if (line is { Kind: LineKind.Table, Table: { } table })
        {
            // 표는 블록 하나. 칸을 누르면 그 자리에서 고친다 (MarkdownRichEditor.Tables.cs).
            return new BlockUIContainer(MarkdownTableView.Create(table, TableToolTip)) { Tag = table };
        }
        var paragraph = NewParagraph();
        if (line.Kind == LineKind.Code)
        {
            paragraph.Inlines.Add(new Run(line.PlainText));
        }
        else
        {
            AddInlines(paragraph.Inlines, line.Inlines);
        }
        ApplyKind(paragraph, new LineInfo(line.Kind, line.Level, line.Number, line.Checked, line.Language));
        return paragraph;
    }

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0) };

    /// <summary>문단을 이 종류로 바꾼다 (표시를 모두 다시 정한다). 목록이면 맨 앞에 기호를 둔다.</summary>
    private void ApplyKind(Paragraph paragraph, LineInfo info)
    {
        foreach (var property in new DependencyProperty[]
                 {
                     FrameworkContentElement.TagProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontFamilyProperty,
                     TextElement.BackgroundProperty, TextElement.ForegroundProperty, Block.BorderThicknessProperty, Block.BorderBrushProperty,
                     Block.PaddingProperty, Paragraph.TextDecorationsProperty, Paragraph.TextIndentProperty, Block.TextAlignmentProperty,
                 })
        {
            paragraph.ClearValue(property);
        }
        paragraph.Margin = new Thickness(0);

        var existing = MarkerOf(paragraph);
        var isList = info.Kind is LineKind.Bullet or LineKind.Numbered or LineKind.Task;
        if (!isList && existing is not null)
        {
            paragraph.Inlines.Remove(existing);
        }
        switch (info.Kind)
        {
            case LineKind.Heading:
                var level = Math.Clamp(info.Level, 1, 6);
                paragraph.Tag = "h" + level;
                paragraph.FontSize = level switch { 1 => 22, 2 => 18, 3 => 15.5, _ => 14 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, level <= 2 ? 8 : 4, 0, 2);
                break;
            case LineKind.Quote:
                paragraph.Tag = QuoteTag;
                paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.SetResourceReference(Block.BorderBrushProperty, "BorderStrongBrush");
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "SubTextBrush");
                paragraph.Padding = new Thickness(10, 0, 0, 0);
                break;
            case LineKind.Code:
                paragraph.Tag = info.Language is null ? CodeTag : CodeTag + ":" + info.Language;
                paragraph.FontFamily = CodeFont;
                paragraph.FontSize = 12.5;
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
                paragraph.Padding = new Thickness(10, 0, 10, 0);
                break;
            case LineKind.Bullet or LineKind.Numbered or LineKind.Task:
                paragraph.Margin = new Thickness(Math.Max(0, info.Level) * IndentStep, 0, 0, 0);
                var marker = CreateMarker(info);
                if (existing is not null)
                {
                    paragraph.Inlines.Remove(existing);
                }
                if (paragraph.Inlines.FirstInline is { } first)
                {
                    paragraph.Inlines.InsertBefore(first, marker);
                }
                else
                {
                    paragraph.Inlines.Add(marker);
                }
                ApplyChecked(paragraph, info.Kind == LineKind.Task && info.Checked);
                break;
        }
    }

    private InlineUIContainer CreateMarker(LineInfo info)
    {
        FrameworkElement child;
        var tag = $"{MarkerPrefix}{info.Kind}|{Math.Max(0, info.Level)}|{Math.Max(0, info.Number)}";
        if (info.Kind == LineKind.Task)
        {
            var box = new CheckBox { IsChecked = info.Checked, Margin = new Thickness(0, 0, 6, -3), Cursor = Cursors.Hand, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetName(box, "체크");
            box.Click += (_, _) =>
            {
                if (FindParagraph(box) is { } paragraph)
                {
                    ApplyChecked(paragraph, box.IsChecked == true);
                    Flush();
                }
            };
            child = box;
        }
        else
        {
            var text = new TextBlock
            {
                Text = info.Kind == LineKind.Numbered ? $"{Math.Max(1, info.Number)}." : (info.Level % 2 == 0 ? "•" : "◦"),
                MinWidth = 16,
                Margin = new Thickness(0, 0, 4, 0),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            child = text;
        }
        child.Tag = tag;
        return new InlineUIContainer(child) { BaselineAlignment = info.Kind == LineKind.Task ? BaselineAlignment.Center : BaselineAlignment.Baseline };
    }

    private static InlineUIContainer? MarkerOf(Paragraph paragraph) =>
        paragraph.Inlines.OfType<InlineUIContainer>().FirstOrDefault(c => c.Child is FrameworkElement { Tag: string tag } && tag.StartsWith(MarkerPrefix, StringComparison.Ordinal));

    private static LineInfo? ParseMarker(InlineUIContainer marker)
    {
        var parts = ((string)((FrameworkElement)marker.Child).Tag)[MarkerPrefix.Length..].Split('|');
        if (parts.Length != 3 || !Enum.TryParse<LineKind>(parts[0], out var kind))
        {
            return null;
        }
        return new LineInfo(kind, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture),
            marker.Child is CheckBox { IsChecked: true });
    }

    private Paragraph? FindParagraph(DependencyObject child)
    {
        foreach (var paragraph in AllParagraphs())
        {
            if (MarkerOf(paragraph)?.Child == child)
            {
                return paragraph;
            }
        }
        return null;
    }

    private static void ApplyChecked(Paragraph paragraph, bool done)
    {
        if (done)
        {
            paragraph.TextDecorations = TextDecorations.Strikethrough;
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        }
        else
        {
            paragraph.ClearValue(Paragraph.TextDecorationsProperty);
            paragraph.ClearValue(TextElement.ForegroundProperty);
        }
    }

    /// <summary>번호 목록의 번호를 다시 매긴다. 같은 들여쓰기의 이어지는 번호 항목은 첫 항목 번호부터 1씩.</summary>
    private Dictionary<Paragraph, int> ComputeNumbers()
    {
        var numbers = new Dictionary<Paragraph, int>();
        var counters = new Dictionary<int, int>();
        foreach (var paragraph in AllParagraphs())
        {
            var info = MarkerOf(paragraph) is { } marker ? ParseMarker(marker) : null;
            if (info is null)
            {
                counters.Clear();
                continue;
            }
            foreach (var deeper in counters.Keys.Where(l => l > info.Level).ToList())
            {
                counters.Remove(deeper);
            }
            if (info.Kind != LineKind.Numbered)
            {
                counters.Remove(info.Level);
                continue;
            }
            var number = counters.TryGetValue(info.Level, out var previous) ? previous + 1 : Math.Max(1, info.Number);
            counters[info.Level] = number;
            numbers[paragraph] = number;
        }
        return numbers;
    }

    private void RefreshMarkers()
    {
        foreach (var (paragraph, number) in ComputeNumbers())
        {
            if (MarkerOf(paragraph)?.Child is TextBlock text)
            {
                text.Text = $"{number}.";
            }
        }
    }

    private IEnumerable<Paragraph> AllParagraphs()
    {
        static IEnumerable<Paragraph> Walk(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        yield return paragraph;
                        break;
                    case Section section:
                        foreach (var inner in Walk(section.Blocks))
                        {
                            yield return inner;
                        }
                        break;
                    case List list:
                        foreach (var inner in list.ListItems.SelectMany(i => Walk(i.Blocks)))
                        {
                            yield return inner;
                        }
                        break;
                }
            }
        }
        return Walk(Document.Blocks);
    }

    private List<Paragraph> SelectedParagraphs()
    {
        var start = Selection.Start;
        var end = Selection.End;
        var selected = AllParagraphs().Where(p => p.ContentEnd.CompareTo(start) >= 0 && p.ContentStart.CompareTo(end) <= 0).ToList();
        if (selected.Count == 0 && CurrentParagraph() is { } current)
        {
            selected.Add(current);
        }
        return selected;
    }

    // ---------- 글자 ↔ 조각 ----------

    private sealed record Format(bool Bold, bool Italic, bool Underline, bool Strike, bool Code, bool Highlight, string? Color, string? Link)
    {
        public InlineStyle Style =>
            (Bold ? InlineStyle.Bold : 0) | (Italic ? InlineStyle.Italic : 0) | (Underline ? InlineStyle.Underline : 0)
            | (Strike ? InlineStyle.Strike : 0) | (Code ? InlineStyle.Code : 0) | (Highlight && !Code ? InlineStyle.Highlight : 0);
    }

    private static List<InlineRun> ExtractInlines(Paragraph paragraph)
    {
        var runs = new List<InlineRun>();
        Walk(paragraph.Inlines, new Format(false, false, false, false, false, false, null, null), runs);
        return runs;
    }

    private static void Walk(InlineCollection inlines, Format format, List<InlineRun> runs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    var f = Combine(format, run);
                    runs.Add(new InlineRun(run.Text, f.Style, f.Color, f.Link));
                    break;
                case LineBreak:
                    runs.Add(new InlineRun("\n", Link: format.Link));
                    break;
                case InlineUIContainer { Child: FrameworkElement { Tag: string tag } } when tag.StartsWith(ImagePrefix, StringComparison.Ordinal):
                    var parts = tag[ImagePrefix.Length..].Split('\n', 3);
                    int? width = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : null;
                    runs.Add(new InlineRun(parts.Length > 2 ? parts[2] : "", Link: format.Link, Image: parts[0], ImageWidth: width));
                    break;
                case Hyperlink hyperlink:
                    var link = hyperlink.Tag as string ?? hyperlink.NavigateUri?.OriginalString;
                    Walk(hyperlink.Inlines, Combine(format, hyperlink) with { Link = link }, runs);
                    break;
                case Span span:
                    Walk(span.Inlines, Combine(format, span), runs);
                    break;
            }
        }
    }

    /// <summary>요소의 로컬 서식 값을 바깥 서식 위에 겹친다 (안쪽 값이 이긴다).</summary>
    private static Format Combine(Format outer, TextElement element)
    {
        var f = outer;
        if (element is Bold)
        {
            f = f with { Bold = true };
        }
        if (element is Italic)
        {
            f = f with { Italic = true };
        }
        if (element is Underline)
        {
            f = f with { Underline = true };
        }
        if (element.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight weight)
        {
            f = f with { Bold = weight.ToOpenTypeWeight() >= 700 };
        }
        if (element.ReadLocalValue(TextElement.FontStyleProperty) is FontStyle style)
        {
            f = f with { Italic = style != FontStyles.Normal };
        }
        if (element.ReadLocalValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations)
        {
            f = f with
            {
                Underline = decorations.Any(d => d.Location == TextDecorationLocation.Underline),
                Strike = decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough),
            };
        }
        if (element.ReadLocalValue(TextElement.FontFamilyProperty) is FontFamily family)
        {
            f = f with { Code = IsCodeFont(family) };
        }
        var background = element.ReadLocalValue(TextElement.BackgroundProperty);
        if (background is SolidColorBrush highlight)
        {
            f = f with { Highlight = highlight.Color.A > 0 };
        }
        else if (background is null)
        {
            f = f with { Highlight = false };
        }
        if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush color)
        {
            f = f with { Color = $"#{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}" };
        }
        return f;
    }

    private static bool IsCodeFont(FontFamily family) =>
        family.Source.Contains("Cascadia Mono", StringComparison.OrdinalIgnoreCase) || family.Source.StartsWith("Consolas", StringComparison.OrdinalIgnoreCase);

    private void AddInlines(InlineCollection target, IReadOnlyList<InlineRun> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].Link is { } link)
            {
                var hyperlink = NewHyperlink(link);
                for (; i < runs.Count && runs[i].Link == link; i++)
                {
                    hyperlink.Inlines.Add(CreateInline(runs[i] with { Link = null }));
                }
                i--;
                target.Add(hyperlink);
                continue;
            }
            target.Add(CreateInline(runs[i]));
        }
    }

    private static Hyperlink NewHyperlink(string link)
    {
        var hyperlink = new Hyperlink { Tag = link, ToolTip = $"{link}\nCtrl+클릭으로 열기" };
        hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        return hyperlink;
    }

    private Inline CreateInline(InlineRun run)
    {
        if (run.Image is { } image)
        {
            return CreateImage(image, run.Text, run.ImageWidth);
        }
        Inline inline;
        if (run.Text.Contains('\n'))
        {
            var span = new Span();
            var parts = run.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    span.Inlines.Add(new LineBreak());
                }
                if (parts[i].Length > 0)
                {
                    span.Inlines.Add(new Run(parts[i]));
                }
            }
            inline = span;
        }
        else
        {
            inline = new Run(run.Text);
        }
        ApplyFormat(inline, run.Style, run.Color);
        return inline;
    }

    private static void ApplyFormat(Inline inline, InlineStyle style, string? color)
    {
        if (style.HasFlag(InlineStyle.Bold))
        {
            inline.FontWeight = FontWeights.Bold;
        }
        if (style.HasFlag(InlineStyle.Italic))
        {
            inline.FontStyle = FontStyles.Italic;
        }
        if (style.HasFlag(InlineStyle.Underline) || style.HasFlag(InlineStyle.Strike))
        {
            var decorations = new TextDecorationCollection();
            if (style.HasFlag(InlineStyle.Underline))
            {
                decorations.Add(TextDecorations.Underline);
            }
            if (style.HasFlag(InlineStyle.Strike))
            {
                decorations.Add(TextDecorations.Strikethrough);
            }
            inline.TextDecorations = decorations;
        }
        if (style.HasFlag(InlineStyle.Code))
        {
            inline.FontFamily = CodeFont;
            inline.SetResourceReference(TextElement.BackgroundProperty, "SubtleBrush");
        }
        else if (style.HasFlag(InlineStyle.Highlight))
        {
            inline.Background = HighlightBrush();
        }
        if (color is not null && ColorConverter.ConvertFromString(color) is Color parsed)
        {
            inline.Foreground = Frozen(parsed);
        }
    }

    private static SolidColorBrush HighlightBrush() =>
        Frozen(ThemeService.IsDark ? Color.FromRgb(0x5C, 0x53, 0x20) : Color.FromRgb(0xFF, 0xF3, 0xA3));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <param name="width">사용자가 정한 너비 (null이면 원래 크기, 너무 크면 줄여서).</param>
    private InlineUIContainer CreateImage(string source, string alt, int? width = null)
    {
        FrameworkElement child;
        if (_resolveImage(source) is { } path && System.IO.File.Exists(path) && MarkdownRenderer.LoadImage(path) is { } bitmap)
        {
            child = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                // 너비를 정했으면 작은 그림도 그 너비까지 키운다. 높이는 Uniform이라 비율대로 따라간다.
                StretchDirection = width is null ? StretchDirection.DownOnly : StretchDirection.Both,
                Width = width ?? double.NaN,
                MaxWidth = MarkdownRenderer.MaxImageWidth,
                MaxHeight = MarkdownRenderer.MaxImageHeight,
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = Cursors.Arrow,
                ToolTip = (alt.Length > 0 ? alt + "\n" : "") + "누르면 모서리를 끌어 크기 조절",
            };
        }
        else
        {
            var placeholder = new TextBlock { Text = $"[그림{(alt.Length > 0 ? ": " + alt : "")}]" };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            child = placeholder;
        }
        System.Windows.Automation.AutomationProperties.SetName(child, alt.Length > 0 ? alt : "그림");
        child.Tag = ImageTag(source, width, alt);
        return new InlineUIContainer(child) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    /// <summary>그림 요소의 Tag: "image\n경로\n너비\n설명" (너비는 비어 있을 수 있다).</summary>
    private static string ImageTag(string source, int? width, string alt) =>
        ImagePrefix + source + "\n" + (width?.ToString(CultureInfo.InvariantCulture) ?? "") + "\n" + alt;

    private static string PlainText(Paragraph paragraph)
    {
        var text = new System.Text.StringBuilder();
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        text.Append(run.Text);
                        break;
                    case LineBreak:
                        text.Append(' ');
                        break;
                    case Span span:
                        Walk(span.Inlines);
                        break;
                }
            }
        }
        Walk(paragraph.Inlines);
        return text.ToString();
    }

    private static bool IsEmptyContent(Paragraph paragraph) =>
        PlainText(paragraph).Length == 0 && !paragraph.Inlines.OfType<InlineUIContainer>().Any(c => c.Child is FrameworkElement { Tag: string tag } && tag.StartsWith(ImagePrefix, StringComparison.Ordinal));

    /// <summary>
    /// 커서가 있는 문단. 커서가 문단 경계 바깥(문서 수준)에 놓여 <see cref="TextPointer.Paragraph"/>가 null일 때가 있으므로,
    /// 커서가 붙은 쪽(LogicalDirection)의 글자 위치, 반대쪽, 커서보다 앞에서 시작하는 마지막 문단 순으로 찾는다.
    /// </summary>
    private Paragraph? CurrentParagraph()
    {
        var caret = CaretPosition;
        var otherSide = caret.LogicalDirection == LogicalDirection.Forward ? LogicalDirection.Backward : LogicalDirection.Forward;
        return caret.Paragraph
               ?? caret.GetInsertionPosition(caret.LogicalDirection)?.Paragraph
               ?? caret.GetInsertionPosition(otherSide)?.Paragraph
               ?? AllParagraphs().LastOrDefault(p => p.ContentStart.CompareTo(caret) <= 0);
    }

    /// <summary>커서를 옮긴다 (글자를 넣을 수 있는 위치로 맞춘다).</summary>
    private void MoveCaret(TextPointer position) =>
        CaretPosition = position.IsAtInsertionPosition ? position : position.GetInsertionPosition(LogicalDirection.Forward);

    /// <summary>문단 글자의 시작 (목록이면 기호 바로 뒤).</summary>
    private static TextPointer ContentStart(Paragraph paragraph) =>
        MarkerOf(paragraph) is { } marker ? marker.ElementEnd : paragraph.ContentStart;
}
