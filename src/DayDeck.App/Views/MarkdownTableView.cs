using System.Windows;
using System.Windows.Controls;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 메모의 표를 테두리 있는 칸으로 그린다 (미리보기와 "보기" 편집기). 보기 편집기는 칸을 눌러 그 자리에서 고친다
/// (<see cref="MarkdownRichEditor"/>). 엑셀 등에서 복사한 클립보드를 표로 바꾸는 판단도 여기서 한다.
/// </summary>
internal static class MarkdownTableView
{
    /// <summary>칸 하나의 최대 너비. 긴 글은 칸 안에서 줄을 바꾼다.</summary>
    public const double MaxCellWidth = 320;

    /// <summary>
    /// 칸 테두리(<see cref="Border"/>)의 Tag. 몇째 줄·칸인지는 Grid.Row/Grid.Column이다 (0부터, 머리글이 0줄).
    /// "보기" 편집기에서 표 블록을 지우면 WPF가 실행 취소용으로 블록을 XAML로 저장하므로 Tag는 XAML로 쓸 수 있는 글자로 둔다.
    /// </summary>
    private const string CellTag = "table-cell";

    /// <summary>
    /// 표. 바깥 테두리(<see cref="Border"/>)는 평소 투명하고, 보기 편집기에서 표를 선택하면 <see cref="SetSelected"/>로 강조한다.
    /// </summary>
    public static Border Create(MarkdownTable table, string? toolTip = null)
    {
        var grid = new Grid { SnapsToDevicePixels = true };
        var outline = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 6),
            Padding = new Thickness(1),
            BorderThickness = new Thickness(2),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            Child = grid,
            ToolTip = toolTip,
        };
        System.Windows.Automation.AutomationProperties.SetName(outline, $"표 ({table.Rows.Count}줄 × {table.ColumnCount}칸)");
        for (var c = 0; c < table.ColumnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var r = 0; r < table.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < table.ColumnCount; c++)
            {
                var cell = new Border
                {
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = CellText(table, r, c),
                    Tag = CellTag,
                };
                cell.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
                if (r == 0)
                {
                    cell.SetResourceReference(Border.BackgroundProperty, "SubtleBrush");
                }
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return outline;
    }

    /// <summary>칸 하나의 글자 (서식 적용, 머리글은 굵게).</summary>
    public static TextBlock CellText(MarkdownTable table, int row, int column)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MaxCellWidth,
            TextAlignment = Alignment(table, column),
            FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
        };
        foreach (var run in MarkdownDoc.ParseInlines(table.Cell(row, column)))
        {
            text.Inlines.Add(MarkdownRenderer.RenderInline(run));
        }
        return text;
    }

    public static TextAlignment Alignment(MarkdownTable table, int column) => table.Alignments[column] switch
    {
        TableAlign.Right => TextAlignment.Right,
        TableAlign.Center => TextAlignment.Center,
        _ => TextAlignment.Left,
    };

    /// <summary>모든 칸을 이 표의 글자로 다시 보인다.</summary>
    public static void ShowCells(Border outline, MarkdownTable table)
    {
        foreach (var cell in Cells(outline))
        {
            cell.Child = CellText(table, Grid.GetRow(cell), Grid.GetColumn(cell));
        }
    }

    /// <summary><see cref="Create"/>로 만든 표에서 이 칸의 테두리.</summary>
    public static Border? Cell(Border outline, int row, int column) =>
        Cells(outline).FirstOrDefault(b => Grid.GetRow(b) == row && Grid.GetColumn(b) == column);

    private static IEnumerable<Border> Cells(Border outline) =>
        (outline.Child as Grid)?.Children.OfType<Border>().Where(b => b.Tag is CellTag) ?? [];

    /// <summary>누른 곳이 들어 있는 칸의 테두리 (칸 글자처럼 Visual이 아닌 것은 논리 트리로 올라간다).</summary>
    public static Border? CellAt(DependencyObject? element)
    {
        for (var current = element; current is not null;
             current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: CellTag } cell)
            {
                return cell;
            }
        }
        return null;
    }

    public static void SetSelected(Border outline, bool selected)
    {
        if (selected)
        {
            outline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        }
        else
        {
            outline.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
    }

    /// <summary>
    /// 붙여 넣을 내용이 표 프로그램에서 복사한 것이면 표로. 엑셀(XML Spreadsheet·CSV 형식을 함께 둔다)은 칸 하나짜리 열도 표로,
    /// 그 밖에 HTML 표(구글 시트, 워드, 웹)는 칸이 둘 이상일 때만. 아니면 null (평소처럼 글자로 붙여 넣는다).
    /// </summary>
    public static MarkdownTable? FromClipboard(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.UnicodeText, true) || data.GetData(DataFormats.UnicodeText, true) is not string text
                || (!text.Contains('\t') && !text.TrimEnd().Contains('\n')))
            {
                return null;
            }
            var spreadsheet = data.GetDataPresent("XML Spreadsheet") || data.GetDataPresent(DataFormats.CommaSeparatedValue);
            var htmlTable = !spreadsheet && data.GetDataPresent(DataFormats.Html)
                            && data.GetData(DataFormats.Html) is string html && html.Contains("<table", StringComparison.OrdinalIgnoreCase);
            if (!spreadsheet && !htmlTable)
            {
                return null;
            }
            return MarkdownTable.FromTsv(text) is { } table && (spreadsheet || table.ColumnCount >= 2) ? table : null;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("클립보드 표를 읽을 수 없음", ex);
            return null;
        }
    }
}
