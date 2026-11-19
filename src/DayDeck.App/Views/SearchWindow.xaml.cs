using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DayDeck.Core.Search;

namespace DayDeck.App.Views;

/// <summary>전체 검색 (Ctrl+K): 할 일, 일정, 메모를 한 번에 찾아 연다.</summary>
public partial class SearchWindow : Window
{
    private readonly App _app = App.Current;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _closing;
    private bool _armed;

    public SearchWindow()
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Search();
        };
        Loaded += (_, _) => QueryBox.Focus();
        // 다른 곳을 누르면 닫힌다. 단, 뜨자마자 오는 활성화 변경(메인 창 활성화 등)은 무시한다.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _armed = true);
        Deactivated += (_, _) =>
        {
            if (_armed && !_closing)
            {
                Close();
            }
        };
        EmptyText.Text = "찾을 말을 입력하세요.";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Search()
    {
        var query = QueryBox.Text.Trim();
        var rows = new List<SearchRow>();
        if (query.Length > 0)
        {
            var hits = _app.Search.Search(query);
            foreach (var (kind, header, icon) in new[] { (SearchHitKind.Task, "할 일", "☐"), (SearchHitKind.Event, "일정", "📅"), (SearchHitKind.Note, "메모", "📝") })
            {
                var group = hits.Where(h => h.Kind == kind).ToList();
                if (group.Count == 0)
                {
                    continue;
                }
                rows.Add(new SearchRow(null, $"{header} {group.Count}", "", "", true, false));
                rows.AddRange(group.Select(h => new SearchRow(h, h.Title, h.Detail, h.IsDone ? "☑" : icon, false, h.IsDone)));
            }
        }

        Results.ItemsSource = rows;
        Results.SelectedItem = rows.FirstOrDefault(r => !r.IsHeader);
        EmptyText.Text = query.Length == 0 ? "찾을 말을 입력하세요." : rows.Count == 0 ? $"'{query}'에 해당하는 항목이 없습니다." : "";
        FooterText.Text = rows.Count == 0 ? "↑↓ 이동 · Enter 열기 · Esc 닫기" : $"{rows.Count(r => !r.IsHeader)}개 · ↑↓ 이동 · Enter 열기 · Esc 닫기";
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_debounce.IsEnabled)
                {
                    _debounce.Stop();
                    Search();
                }
                Activate(Results.SelectedItem as SearchRow);
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Activate(Results.SelectedItem as SearchRow);
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Move(int direction)
    {
        if (Results.ItemsSource is not List<SearchRow> rows || rows.Count == 0)
        {
            return;
        }
        var index = Results.SelectedItem is SearchRow current ? rows.IndexOf(current) : -1;
        for (var i = index + direction; i >= 0 && i < rows.Count; i += direction)
        {
            if (!rows[i].IsHeader)
            {
                Results.SelectedItem = rows[i];
                Results.ScrollIntoView(rows[i]);
                return;
            }
        }
    }

    private void OnResultActivated(object sender, MouseButtonEventArgs e) => Activate(Results.SelectedItem as SearchRow);

    private void Activate(SearchRow? row)
    {
        if (row?.Hit is not { } hit)
        {
            return;
        }
        _closing = true;
        Close();
        switch (hit.Kind)
        {
            case SearchHitKind.Note:
                _app.ShowNote(hit.NotePath!);
                break;
            default:
                _app.ShowTask(hit.ItemId!.Value);
                break;
        }
    }

    internal sealed record SearchRow(SearchHit? Hit, string Title, string Detail, string Icon, bool IsHeader, bool IsDone);
}
