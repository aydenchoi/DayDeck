using System.IO;
using System.Windows;
using System.Windows.Controls;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

/// <summary>휴지통: 지운 할 일, 일정, 메모를 복원하거나 영구 삭제한다.</summary>
public partial class TrashPage : UserControl
{
    private readonly App _app = App.Current;

    public TrashPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var rows = _app.Trash.GetItems().Select(i => new TrashRow(
                i.Kind == ItemKind.Event ? "📅" : "☐",
                i.Title,
                Describe(i.DeletedAt, i.Kind == ItemKind.Event ? "일정" : "할 일", i.ListName, i.SubtaskCount),
                i.DeletedAt, i.Id, null))
            .Concat(_app.Notes.GetTrash().Select(n => new TrashRow("📝", Path.GetFileNameWithoutExtension(n.OriginalPath),
                Describe(n.DeletedAt, "메모", n.OriginalPath, 0), n.DeletedAt, null, n.TrashPath)))
            .OrderByDescending(r => r.DeletedAt)
            .ToList();
        Items.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Describe(DateTime deletedAt, string kind, string? where, int subtasks)
    {
        var left = Math.Max(0, (deletedAt.AddDays(30) - DateTime.Now).Days);
        var parts = new List<string> { kind, $"{deletedAt:yyyy-MM-dd HH:mm} 삭제", $"{left}일 후 영구 삭제" };
        if (!string.IsNullOrEmpty(where))
        {
            parts.Insert(1, where);
        }
        if (subtasks > 0)
        {
            parts.Add($"하위 할 일 {subtasks}개 포함");
        }
        return string.Join(" · ", parts);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TrashRow row })
        {
            return;
        }
        if (row.ItemId is { } id)
        {
            _app.Trash.Restore(id);
            _app.NotifyDataRestored();
        }
        else
        {
            _app.Notes.Restore(row.NotePath!);
        }
        Reload();
    }

    private void OnDeleteForever(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TrashRow row })
        {
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this), $"'{row.Title}'을(를) 영구 삭제할까요? 되돌릴 수 없습니다.", "영구 삭제",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        if (row.ItemId is { } id)
        {
            _app.Trash.DeleteForever(id);
        }
        else
        {
            File.Delete(row.NotePath!);
        }
        Reload();
    }

    private void OnEmpty(object sender, RoutedEventArgs e)
    {
        if (Items.Items.Count == 0 || MessageBox.Show(Window.GetWindow(this), "휴지통의 모든 항목을 영구 삭제할까요? 되돌릴 수 없습니다.", "휴지통 비우기",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }
        _app.Trash.EmptyAll();
        if (Directory.Exists(_app.Notes.TrashRoot))
        {
            Directory.Delete(_app.Notes.TrashRoot, recursive: true);
        }
        Reload();
    }

    internal sealed record TrashRow(string Icon, string Title, string Detail, DateTime DeletedAt, long? ItemId, string? NotePath);
}
