using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// "보기"에서 표 칸 고치기. 칸을 누르면(표를 고른 채 Enter·F2면 첫 칸) 그 칸이 글 상자가 되어 칸의 마크다운을 그대로 고친다
/// (<c>\|</c>는 <c>|</c>, <c>&lt;br&gt;</c>는 줄바꿈으로 보인다). Tab/Shift+Tab은 다음/앞 칸, Enter는 아래 칸, Shift+Enter는 칸 안 줄바꿈,
/// Esc는 표 전체 선택. 표 밖으로 나가면 고친 표로 블록을 한 번에 바꾼다 (Ctrl+Z 한 번에 되돌린다).
/// 고치는 동안에도 저장(<see cref="ToLines"/>)은 고친 내용으로 한다.
/// </summary>
internal sealed partial class MarkdownRichEditor
{
    public const string TableCellAutomationId = "TableCellEditor";

    private const string TableToolTip = "표 — 칸을 누르면 바로 고칩니다 (Tab 다음 칸, Enter 아래 칸, Shift+Enter 칸 안 줄바꿈, Esc 표 전체 선택: Delete로 지우기, Ctrl+C로 복사)";

    private TableEdit? _tableEdit;

    /// <summary>고치고 있는 표. <see cref="Table"/>은 앞서 고친 칸까지 넣은 내용, <see cref="Box"/>는 지금 고치는 칸의 글 상자.</summary>
    private sealed class TableEdit(BlockUIContainer container, MarkdownTable table)
    {
        public BlockUIContainer Container { get; } = container;

        public MarkdownTable Original { get; } = table;

        public MarkdownTable Table { get; set; } = table;

        public int Row { get; set; }

        public int Column { get; set; }

        public TextBox? Box { get; set; }

        public string BoxStartText { get; set; } = "";

        /// <summary>지금 글 상자의 글자까지 넣은 표.</summary>
        public MarkdownTable Current => Box is { } box && box.Text != BoxStartText
            ? Table.WithCell(Row, Column, MarkdownTable.CellFromEditText(box.Text))
            : Table;
    }

    /// <summary>표 고치기를 끝낸 뒤 커서를 둘 곳.</summary>
    private enum TableExit
    {
        /// <summary>다른 곳을 눌렀거나 포커스가 나갔다: 커서는 건드리지 않는다.</summary>
        Stay,

        /// <summary>표 앞 줄 끝 (Shift+Tab으로 첫 칸에서 나감).</summary>
        Before,

        /// <summary>표 다음 줄 처음 (Tab·Enter로 마지막 칸에서 나감).</summary>
        After,

        /// <summary>표 전체 선택 (Esc): Delete로 지우기, Ctrl+C로 복사.</summary>
        SelectTable,
    }

    /// <summary>고치고 있는 표면 고친 내용 (저장할 때).</summary>
    private MarkdownTable? EditingTable(BlockUIContainer container) =>
        _tableEdit is { } edit && edit.Container == container ? edit.Current : null;

    private bool IsInTableCellBox(object? source) => _tableEdit is { Box: { } box } && IsInside(source as DependencyObject, box);

    /// <summary>선택이 표 하나뿐이면 그 표 (Esc로 고르거나 표 테두리를 눌렀을 때).</summary>
    private BlockUIContainer? SelectedTable()
    {
        if (Selection.IsEmpty || Selection.Text.Trim().Length > 0)
        {
            return null;
        }
        var tables = Document.Blocks.OfType<BlockUIContainer>().Where(b => b.Tag is MarkdownTable && IsTableSelected(b)).Take(2).ToList();
        return tables.Count == 1 ? tables[0] : null;
    }

    /// <summary>
    /// 칸을 고치기 시작한다. 같은 표의 다른 칸을 고치던 중이면 그 칸을 넣고 옮겨 가고, 다른 표였으면 그 표를 먼저 끝낸다.
    /// <paramref name="click"/>(칸 안에서 누른 곳)이 있으면 그 자리에 커서를, 없으면(키보드) 칸 글자를 모두 고른다.
    /// </summary>
    private void EditTableCell(BlockUIContainer container, int row, int column, Point? click = null)
    {
        if (_tableEdit is { } other && other.Container != container)
        {
            FinishTableEdit(TableExit.Stay);
        }
        if (container.Tag is not MarkdownTable table || container.Child is not Border outline)
        {
            return;
        }
        if (!Selection.IsEmpty)
        {
            Selection.Select(Selection.End, Selection.End); // 표 전체 선택 강조를 지운다
        }
        var edit = _tableEdit ??= new TableEdit(container, table);
        CloseCellBox(edit);
        if (MarkdownTableView.Cell(outline, row, column) is not { } cell)
        {
            _tableEdit = null;
            return;
        }
        var text = MarkdownTable.CellEditText(edit.Table.Cell(row, column));
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true, // Shift+Enter는 칸 안 줄바꿈 (Enter는 아래 칸으로 간다)
            TextWrapping = TextWrapping.Wrap,
            MinWidth = Math.Max((cell.Child as FrameworkElement)?.ActualWidth ?? 0, 24),
            MaxWidth = MarkdownTableView.MaxCellWidth,
            TextAlignment = MarkdownTableView.Alignment(edit.Table, column),
            FontWeight = row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetAutomationId(box, TableCellAutomationId);
        AutomationProperties.SetName(box, $"표 칸 ({row + 1}줄 {column + 1}칸)");
        box.TextChanged += (_, _) =>
        {
            // 고치는 동안에도 잠시 뒤 저장되도록 (ToLines가 이 글자를 읽는다)
            _changeTimer.Stop();
            _changeTimer.Start();
        };
        box.LostKeyboardFocus += OnTableCellLostFocus;
        edit.Row = row;
        edit.Column = column;
        edit.Box = box;
        edit.BoxStartText = text;
        cell.Child = box;
        cell.UpdateLayout();
        if (!box.Focus())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_tableEdit?.Box == box)
                {
                    box.Focus();
                }
            });
        }
        if (click is { } point && box.GetCharacterIndexFromPoint(cell.TranslatePoint(point, box), snapToText: true) is var index and >= 0)
        {
            box.CaretIndex = index;
        }
        else if (click is null)
        {
            box.SelectAll();
        }
        else
        {
            box.CaretIndex = text.Length;
        }
    }

    /// <summary>지금 고치던 칸의 글자를 표에 넣고, 그 칸을 다시 서식 있는 글자로 보인다.</summary>
    private static void CloseCellBox(TableEdit edit)
    {
        if (edit.Box is null)
        {
            return;
        }
        edit.Table = edit.Current;
        edit.Box = null; // 먼저 비운다: 글 상자가 빠지며 포커스를 잃어도 표 고치기를 끝내지 않도록
        if (edit.Container.Child is Border outline && MarkdownTableView.Cell(outline, edit.Row, edit.Column) is { } cell)
        {
            cell.Child = MarkdownTableView.CellText(edit.Table, edit.Row, edit.Column);
        }
    }

    /// <summary>표 고치기를 끝낸다. 고친 칸이 있으면 블록을 새 표로 바꾼다 (실행 취소 한 번).</summary>
    private void FinishTableEdit(TableExit exit)
    {
        if (_tableEdit is not { } edit)
        {
            return;
        }
        CloseCellBox(edit);
        _tableEdit = null;
        var container = edit.Container;
        if (container.SiblingBlocks is not { } blocks)
        {
            return; // 그 사이 문서가 바뀌었다 (다른 메모를 열었음)
        }
        if (!edit.Table.Write().SequenceEqual(edit.Original.Write()))
        {
            var replaced = (BlockUIContainer)CreateBlock(new MarkdownLine(LineKind.Table, [], Table: edit.Table));
            // 지우는 블록은 WPF가 실행 취소용으로 XAML로 저장해 두었다가 다시 만든다:
            // 칸을 고치며 바꿔 둔 화면을 먼저 원래 글자로 되돌려야 되돌린 표가 원래 내용으로 보인다.
            if (container.Child is Border oldOutline)
            {
                MarkdownTableView.ShowCells(oldOutline, edit.Original);
            }
            BeginChange();
            try
            {
                blocks.InsertAfter(container, replaced);
                blocks.Remove(container);
            }
            finally
            {
                EndChange();
            }
            container = replaced;
            Flush();
        }
        switch (exit)
        {
            case TableExit.Before when container.PreviousBlock is Paragraph previous:
                Focus();
                MoveCaret(previous.ContentEnd);
                break;
            case TableExit.After when container.NextBlock is Paragraph next:
                Focus();
                MoveCaret(ContentStart(next));
                break;
            case TableExit.Before or TableExit.After or TableExit.SelectTable:
                Focus();
                Selection.Select(container.ElementStart, container.ElementEnd);
                break;
        }
    }

    /// <summary>
    /// 칸 글 상자의 키: Tab/Shift+Tab은 다음/앞 칸(줄 끝에서는 다음/앞 줄로), Enter는 아래 칸, Esc는 표 전체 선택.
    /// 마지막 칸(첫 칸)에서 넘어가면 표 다음(앞) 줄로 나간다. 나머지 키는 글 상자가 처리한다.
    /// </summary>
    private bool HandleTableCellKey(KeyEventArgs e)
    {
        if (_tableEdit is not { } edit)
        {
            return false;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var (rows, columns) = (edit.Table.Rows.Count, edit.Table.ColumnCount);
        switch (key)
        {
            case Key.Tab when modifiers == ModifierKeys.None:
                if (edit.Column + 1 < columns)
                {
                    EditTableCell(edit.Container, edit.Row, edit.Column + 1);
                }
                else if (edit.Row + 1 < rows)
                {
                    EditTableCell(edit.Container, edit.Row + 1, 0);
                }
                else
                {
                    FinishTableEdit(TableExit.After);
                }
                return true;
            case Key.Tab when modifiers == ModifierKeys.Shift:
                if (edit.Column > 0)
                {
                    EditTableCell(edit.Container, edit.Row, edit.Column - 1);
                }
                else if (edit.Row > 0)
                {
                    EditTableCell(edit.Container, edit.Row - 1, columns - 1);
                }
                else
                {
                    FinishTableEdit(TableExit.Before);
                }
                return true;
            case Key.Enter when modifiers == ModifierKeys.None:
                if (edit.Row + 1 < rows)
                {
                    EditTableCell(edit.Container, edit.Row + 1, edit.Column);
                }
                else
                {
                    FinishTableEdit(TableExit.After);
                }
                return true;
            case Key.Escape:
                FinishTableEdit(TableExit.SelectTable);
                return true;
            default:
                return false;
        }
    }

    /// <summary>글 상자 밖으로 포커스가 나가면(편집기의 다른 곳·다른 창) 표 고치기를 끝낸다. 칸을 옮길 때는 끝내지 않는다.</summary>
    private void OnTableCellLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_tableEdit?.Box != sender)
        {
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_tableEdit is { Box: { } box } && box == sender && !box.IsKeyboardFocusWithin)
            {
                FinishTableEdit(TableExit.Stay);
            }
        });
    }
}
