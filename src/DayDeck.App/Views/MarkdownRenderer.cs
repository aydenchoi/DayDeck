using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DayDeck.App.Services;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 메모 마크다운을 서식이 적용된 문서로 그린다 (메모 화면의 "나란히"/"보기").
/// 링크는 웹·메일 주소만 연다. 체크 목록의 상자를 누르면 <paramref name="toggleTask"/>로 원문 줄을 바꾼다.
/// 그림은 메모 폴더 안의 파일만 보여 준다 (인터넷 주소는 읽지 않는다).
/// </summary>
internal static class MarkdownRenderer
{
    /// <summary>미리보기 그림의 최대 크기. 큰 사진도 이 크기로 줄여서 읽어 메모리를 아낀다.</summary>
    internal const double MaxImageWidth = 720;
    internal const double MaxImageHeight = 720;

    /// <param name="compact">작은 칸(할 일 메모)용: 여백과 글자를 줄인다.</param>
    /// <param name="resolveImage">그림 경로를 실제 파일 경로로. null이거나 null을 돌려주면 그림 대신 설명 글자를 보인다.</param>
    public static FlowDocument Render(string markdown, Action<int, bool>? toggleTask, bool compact = false, Func<string, string?>? resolveImage = null)
    {
        var document = new FlowDocument
        {
            PagePadding = compact ? new Thickness(8, 6, 8, 8) : new Thickness(24, 16, 24, 24),
            FontSize = compact ? 13 : 14,
            LineHeight = compact ? 20 : 22,
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        document.SetResourceReference(FlowDocument.BackgroundProperty, "CardBrush");

        foreach (var block in MarkdownDoc.Parse(markdown))
        {
            document.Blocks.Add(RenderBlock(block, toggleTask, resolveImage));
        }
        return document;
    }

    private static Block RenderBlock(MarkdownBlock block, Action<int, bool>? toggleTask, Func<string, string?>? resolveImage)
    {
        Paragraph Paragraph(IEnumerable<InlineRun> runs) => MarkdownRenderer.Paragraph(runs, resolveImage);

        switch (block.Kind)
        {
            case BlockKind.Table when block.Table is { } table:
                return new BlockUIContainer(MarkdownTableView.Create(table));

            case BlockKind.Rule:
                return new BlockUIContainer(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6), Background = ThemeService.Brush("LineBrush") });

            case BlockKind.Code:
            {
                var code = new Paragraph(new Run(block.PlainText))
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, D2Coding, Malgun Gothic"),
                    FontSize = 12.5,
                    Padding = new Thickness(10, 6, 10, 6),
                    Background = ThemeService.Brush("SubtleBrush"),
                    LineHeight = 18,
                };
                return code;
            }

            case BlockKind.Heading:
            {
                var heading = Paragraph(block.Inlines);
                heading.FontSize = block.Level switch { 1 => 22, 2 => 18, 3 => 15.5, _ => 14 };
                heading.FontWeight = FontWeights.SemiBold;
                heading.Margin = new Thickness(0, block.Level <= 2 ? 14 : 10, 0, 4);
                return heading;
            }

            case BlockKind.Quote:
            {
                var quote = Paragraph(block.Inlines);
                quote.BorderBrush = ThemeService.Brush("BorderStrongBrush");
                quote.BorderThickness = new Thickness(3, 0, 0, 0);
                quote.Padding = new Thickness(10, 0, 0, 0);
                quote.Foreground = ThemeService.Brush("SubTextBrush");
                quote.Margin = new Thickness(0, 4, 0, 4);
                return quote;
            }

            case BlockKind.Bullet or BlockKind.Numbered or BlockKind.Task:
            {
                var item = Paragraph(block.Inlines);
                item.Margin = new Thickness(18 + block.Level * 20, 1, 0, 1);
                item.TextIndent = -18;
                Inline marker;
                if (block.Kind == BlockKind.Task)
                {
                    var box = new CheckBox
                    {
                        IsChecked = block.Checked,
                        Margin = new Thickness(0, 0, 4, -3),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        IsEnabled = toggleTask is not null,
                    };
                    System.Windows.Automation.AutomationProperties.SetName(box, block.PlainText);
                    box.Click += (_, _) => toggleTask?.Invoke(block.Line, box.IsChecked == true);
                    marker = new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
                    if (block.Checked)
                    {
                        foreach (var inline in item.Inlines)
                        {
                            inline.Foreground = ThemeService.Brush("MutedBrush");
                            inline.TextDecorations = TextDecorations.Strikethrough;
                        }
                    }
                }
                else
                {
                    marker = new Run(block.Kind == BlockKind.Numbered ? $"{block.Number}. " : (block.Level % 2 == 0 ? "•  " : "◦  "))
                    {
                        Foreground = ThemeService.Brush("SubTextBrush"),
                    };
                }
                item.Inlines.InsertBefore(item.Inlines.FirstInline, marker);
                return item;
            }

            default:
            {
                var paragraph = Paragraph(block.Inlines);
                paragraph.Margin = new Thickness(0, 0, 0, 10);
                return paragraph;
            }
        }
    }

    private static Paragraph Paragraph(IEnumerable<InlineRun> runs, Func<string, string?>? resolveImage)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        foreach (var run in runs)
        {
            paragraph.Inlines.Add(run.Image is { } image ? RenderImage(run, image, resolveImage) : RenderInline(run));
        }
        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(""));
        }
        return paragraph;
    }

    internal static Inline RenderInline(InlineRun run)
    {
        // 한 줄 바꿈은 줄바꿈으로 (메모장처럼)
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

        if (run.Style.HasFlag(InlineStyle.Bold))
        {
            span.FontWeight = FontWeights.Bold;
        }
        if (run.Style.HasFlag(InlineStyle.Italic))
        {
            span.FontStyle = FontStyles.Italic;
        }
        var decorations = new TextDecorationCollection();
        if (run.Style.HasFlag(InlineStyle.Underline) || run.Link is not null)
        {
            decorations.Add(TextDecorations.Underline);
        }
        if (run.Style.HasFlag(InlineStyle.Strike))
        {
            decorations.Add(TextDecorations.Strikethrough);
        }
        if (decorations.Count > 0)
        {
            span.TextDecorations = decorations;
        }
        if (run.Style.HasFlag(InlineStyle.Code))
        {
            span.FontFamily = new FontFamily("Cascadia Mono, Consolas, D2Coding, Malgun Gothic");
            span.Background = ThemeService.Brush("SubtleBrush");
        }
        if (run.Style.HasFlag(InlineStyle.Highlight))
        {
            span.Background = Frozen(ThemeService.IsDark ? Color.FromRgb(0x5C, 0x53, 0x20) : Color.FromRgb(0xFF, 0xF3, 0xA3));
        }
        if (run.Color is { } hex && ColorConverter.ConvertFromString(hex) is Color color)
        {
            span.Foreground = Frozen(color);
        }

        if (run.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && MarkdownEdit.IsSafeScheme(uri))
        {
            var hyperlink = new Hyperlink(span) { NavigateUri = uri, ToolTip = link, Cursor = System.Windows.Input.Cursors.Hand };
            if (run.Color is null)
            {
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            }
            hyperlink.RequestNavigate += (_, e) => Open(e.Uri);
            return hyperlink;
        }
        return span;
    }

    private static Inline RenderImage(InlineRun run, string source, Func<string, string?>? resolveImage)
    {
        if (resolveImage?.Invoke(source) is { } path && File.Exists(path) && LoadImage(path) is { } bitmap)
        {
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                // 편집기에서 정한 너비 (높이는 비율대로)
                StretchDirection = run.ImageWidth is null ? StretchDirection.DownOnly : StretchDirection.Both,
                Width = run.ImageWidth ?? double.NaN,
                MaxWidth = MaxImageWidth,
                MaxHeight = MaxImageHeight,
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = (run.Text.Length > 0 ? run.Text + "\n" : "") + "누르면 크게 보기",
            };
            System.Windows.Automation.AutomationProperties.SetName(image, run.Text.Length > 0 ? run.Text : "그림");
            image.MouseLeftButtonUp += (_, _) => OpenFile(path);
            return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        // 파일이 없거나 인터넷 그림: 설명만 보인다.
        return new Run($"[그림{(run.Text.Length > 0 ? ": " + run.Text : "")}]") { Foreground = ThemeService.Brush("MutedBrush") };
    }

    internal static System.Windows.Media.Imaging.BitmapImage? LoadImage(string path)
    {
        try
        {
            // 큰 사진만 화면에 필요한 크기로 줄여 읽는다 (작은 그림을 키우지 않도록 먼저 크기만 본다).
            int pixelWidth;
            using (var stream = File.OpenRead(path))
            {
                pixelWidth = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0].PixelWidth;
            }
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            // 파일을 잠그지 않고(OnLoad), 바뀐 그림은 다시 읽는다(IgnoreImageCache).
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            if (pixelWidth > MaxImageWidth * 2)
            {
                bitmap.DecodePixelWidth = (int)(MaxImageWidth * 2);
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warn($"그림을 읽을 수 없음: {path}", ex);
            return null;
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"그림을 열 수 없음: {path}", ex);
        }
    }

    private static void Open(Uri uri)
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

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
