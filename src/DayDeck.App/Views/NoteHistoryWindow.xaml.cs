using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>메모의 예전 버전을 보고 되돌린다. 마크다운 메모는 서식이 적용된 모양으로 보인다.</summary>
public partial class NoteHistoryWindow : Window
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    private readonly NoteStore _store;
    private readonly string _path;
    private readonly bool _markdown;

    private NoteHistoryWindow(NoteStore store, string path)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        _store = store;
        _path = path;
        _markdown = string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
        HeaderText.Text = $"{Path.GetFileName(path)} — 예전 버전";
        Preview.Visibility = _markdown ? Visibility.Visible : Visibility.Collapsed;
        PlainPreview.Visibility = _markdown ? Visibility.Collapsed : Visibility.Visible;
        PlainPreview.FontFamily = Services.FontService.EditorFont(App.Current.Settings.Current.Notes);

        var versions = store.GetHistory(path).Select(v => new VersionItem(v)).ToList();
        Versions.ItemsSource = versions;
        EmptyText.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (versions.Count > 0)
        {
            Versions.SelectedIndex = 0;
        }
    }

    /// <summary>되돌렸으면 true (열린 탭을 다시 읽어야 한다).</summary>
    public static bool Show(Window? owner, NoteStore store, string path) =>
        new NoteHistoryWindow(store, path) { Owner = owner }.ShowDialog() == true;

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        RestoreButton.IsEnabled = Versions.SelectedItem is VersionItem;
        if (Versions.SelectedItem is not VersionItem item)
        {
            return;
        }
        string content;
        try
        {
            content = _store.ReadVersion(_path, item.Version.Id);
        }
        catch (IOException ex)
        {
            Log.Warn($"메모 기록을 읽을 수 없음: {_path} {item.Version.Id}", ex);
            StatusText.Text = "이 버전을 읽을 수 없습니다.";
            return;
        }
        StatusText.Text = "";
        if (_markdown)
        {
            Preview.Document = MarkdownRenderer.Render(content, toggleTask: null, resolveImage: link => _store.ResolveRelative(_path, link));
        }
        else
        {
            PlainPreview.Text = content;
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Versions.SelectedItem is not VersionItem item)
        {
            return;
        }
        try
        {
            _store.RestoreVersion(_path, item.Version.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"메모 되돌리기 실패: {_path}", ex);
            MessageBox.Show(this, $"되돌리지 못했습니다.\n{ex.Message}", "메모 기록", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private sealed record VersionItem(NoteVersion Version)
    {
        public string Title => Version.SavedAt.ToString("M월 d일 (ddd) HH:mm", Korean);

        public string Detail
        {
            get
            {
                var age = DateTime.Now - Version.SavedAt;
                var ago = age.TotalMinutes < 1 ? "방금"
                    : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}분 전"
                    : age.TotalDays < 1 ? $"{(int)age.TotalHours}시간 전"
                    : $"{(int)age.TotalDays}일 전";
                var size = Version.Size < 1024 ? $"{Version.Size}바이트" : $"{Version.Size / 1024.0:0.#}KB";
                return $"{ago} · {size}";
            }
        }

        public override string ToString() => Title;
    }
}
