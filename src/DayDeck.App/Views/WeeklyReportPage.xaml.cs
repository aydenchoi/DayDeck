using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 주간업무보고: 주차(목요일 기준)마다 금주실적·차주계획을 써서 메모 폴더의 <c>주간업무보고\연도\</c>에 .txt로 저장한다.
/// 제목과 날짜는 주차에서 만들어 보여 주기만 하고 저장할 때마다 다시 쓰므로 잘못 적을 수 없다.
/// 새 주를 열면 지난주의 차주계획을 금주실적에 미리 넣는다. "복사"는 보고 전체를 그대로 클립보드에.
/// </summary>
public partial class WeeklyReportPage : UserControl
{
    private readonly App _app = App.Current;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private ReportWeek _week = ReportWeek.Of(Today);
    private bool _loading;
    private bool _dirty;

    public WeeklyReportPage()
    {
        InitializeComponent();
        var notes = _app.Settings.Current.Notes;
        foreach (var box in new[] { DoneBox, PlanBox })
        {
            box.FontFamily = Services.FontService.EditorFont(notes);
            box.FontSize = notes.EditorFontSize;
            box.TextChanged += OnEdited;
            box.PreviewKeyDown += OnBulletKey;
            box.LostKeyboardFocus += (_, _) => Save();
        }
        _saveTimer.Tick += (_, _) => Save();
        Loaded += (_, _) =>
        {
            _app.Notes.Changed += OnStoreChanged;
            Show(_week);
        };
        Unloaded += (_, _) =>
        {
            Save();
            _saveTimer.Stop();
            _app.Notes.Changed -= OnStoreChanged;
        };
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // ---------- 주 고르기 ----------

    private void OnPreviousWeek(object sender, RoutedEventArgs e) => Show(_week.Previous);

    private void OnNextWeek(object sender, RoutedEventArgs e) => Show(_week.Next);

    private void OnThisWeek(object sender, RoutedEventArgs e) => Show(ReportWeek.Of(Today));

    private void OnJumpDate(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && JumpDate.SelectedDate is { } date)
        {
            Show(ReportWeek.Of(DateOnly.FromDateTime(date)));
        }
    }

    private void OnReportSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && ReportList.SelectedItem is ListBoxItem { Tag: ReportWeek week } && week != _week)
        {
            Show(week);
        }
    }

    /// <summary>그 주의 보고를 연다. 아직 없으면 빈 보고 (지난주 차주계획을 금주실적으로).</summary>
    private void Show(ReportWeek week)
    {
        Save();
        _week = week;
        _loading = true;
        try
        {
            var path = WeeklyReport.PathOf(week);
            var saved = _app.Notes.Exists(path);
            string done, plan, status;
            if (saved)
            {
                (done, plan) = WeeklyReport.Parse(_app.Notes.Read(path));
                status = "저장된 보고입니다. 고치면 자동 저장됩니다.";
            }
            else
            {
                var previous = WeeklyReport.PathOf(week.Previous);
                var carried = _app.Notes.Exists(previous) ? WeeklyReport.Parse(_app.Notes.Read(previous)).Plan : "";
                var hasCarried = !WeeklyReport.IsBlank(carried);
                done = hasCarried ? carried : WeeklyReport.EmptyItem;
                plan = WeeklyReport.EmptyItem;
                status = hasCarried
                    ? "새 보고 — 지난주 차주계획을 금주실적에 넣었습니다. 고쳐서 쓰면 저장됩니다."
                    : "새 보고 — 내용을 쓰면 저장됩니다.";
            }
            WeekTitle.Text = WeeklyReport.Title(week);
            WeekLabel.Text = $"{week.Label} · {week.Monday:yyyy-MM-dd}(월) ~ {week.Friday:yyyy-MM-dd}(금)";
            DoneHeader.Text = WeeklyReport.DoneHeader(week);
            PlanHeader.Text = WeeklyReport.PlanHeader(week);
            CurrentChip.Visibility = week == ReportWeek.Of(Today) ? Visibility.Visible : Visibility.Collapsed;
            DoneBox.Text = done;
            PlanBox.Text = plan;
            DeleteButton.IsEnabled = saved;
            StatusText.Text = status;
            JumpDate.SelectedDate = week.Monday.ToDateTime(TimeOnly.MinValue);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"주간업무보고를 읽을 수 없음: {week.Label}", ex);
            StatusText.Text = $"⚠ 보고를 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
        RefreshList();
        DoneBox.Focus();
        DoneBox.CaretIndex = DoneBox.Text.Length;
    }

    // ---------- 저장 ----------

    private void OnEdited(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (!_dirty)
        {
            return;
        }
        var path = WeeklyReport.PathOf(_week);
        var text = WeeklyReport.Write(_week, DoneBox.Text, PlanBox.Text);
        try
        {
            if (_app.Notes.Exists(path))
            {
                _app.Notes.Save(path, text);
            }
            else if (WeeklyReport.IsBlank(DoneBox.Text) && WeeklyReport.IsBlank(PlanBox.Text))
            {
                _dirty = false; // 아무것도 쓰지 않은 주는 파일을 만들지 않는다.
                return;
            }
            else
            {
                _app.Notes.Create(WeeklyReport.FolderOf(_week), WeeklyReport.NameOf(_week), ".txt", text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"주간업무보고 저장 실패: {path}", ex);
            StatusText.Text = "⚠ 저장 실패 — 잠시 후 다시 저장합니다";
            _saveTimer.Start();
            return;
        }
        _dirty = false;
        DeleteButton.IsEnabled = true;
        StatusText.Text = $"✓ 저장됨 {DateTime.Now:HH:mm:ss}";
    }

    private void OnStoreChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshList);

    /// <summary>저장된 보고 목록 (최근 주가 위).</summary>
    private void RefreshList()
    {
        var weeks = new List<ReportWeek>();
        try
        {
            var root = _app.Notes.FullPath(WeeklyReport.FolderName);
            if (Directory.Exists(root))
            {
                weeks = [.. Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
                    .Select(WeeklyReport.FromFileName).OfType<ReportWeek>().Distinct().OrderByDescending(w => w.Monday)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("주간업무보고 목록을 읽을 수 없음", ex);
        }

        var wasLoading = _loading;
        _loading = true;
        ReportList.Items.Clear();
        var thisWeek = ReportWeek.Of(Today);
        foreach (var week in weeks)
        {
            var panel = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            // 이번 주는 글자 대신 붉은색으로 표시한다.
            var current = week == thisWeek;
            var label = new TextBlock { Text = week.Label, FontSize = 13.5 };
            var range = new TextBlock { Text = week.Range, FontSize = 11.5 };
            if (current)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                range.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            }
            else
            {
                range.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            }
            panel.Children.Add(label);
            panel.Children.Add(range);
            var item = new ListBoxItem { Content = panel, Tag = week };
            System.Windows.Automation.AutomationProperties.SetName(item, week.Label);
            ReportList.Items.Add(item);
            if (week == _week)
            {
                ReportList.SelectedItem = item;
            }
        }
        ListEmpty.Visibility = weeks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = wasLoading;
    }

    // ---------- 복사 / 삭제 ----------

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Save();
        try
        {
            Clipboard.SetText(WeeklyReport.Write(_week, DoneBox.Text, PlanBox.Text).Replace("\n", "\r\n"));
            StatusText.Text = "📋 복사했습니다 — 메일이나 메신저에 붙여 넣으세요.";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("주간업무보고 복사 실패", ex);
            StatusText.Text = "⚠ 클립보드를 다른 프로그램이 쓰고 있습니다. 다시 눌러 주세요.";
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var path = WeeklyReport.PathOf(_week);
        if (!_app.Notes.Exists(path))
        {
            return;
        }
        var answer = MessageBox.Show(Window.GetWindow(this), $"{_week.Label} 보고를 휴지통으로 옮길까요? (30일 동안 복원할 수 있습니다)",
            "주간업무보고 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        _dirty = false;
        var trashPath = _app.Notes.Delete(path);
        var label = _week.Label;
        (Window.GetWindow(this) as MainWindow)?.ShowUndo($"{label} 보고를 휴지통으로 옮김", () => _app.Notes.Restore(trashPath));
        Show(_week);
    }

    // ---------- 항목 줄 ----------

    /// <summary>
    /// " - 항목" 줄에서 Enter: 다음 줄에도 " - "를 붙인다. 빈 항목(" - "만 있음)에서 Enter는 기호를 지운다 (목록 끝).
    /// </summary>
    private void OnBulletKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || sender is not TextBox box || box.SelectionLength > 0)
        {
            return;
        }
        var text = box.Text;
        var caret = box.CaretIndex;
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        if (BulletLine().Match(text[lineStart..caret]) is not { Success: true } bullet)
        {
            return;
        }
        e.Handled = true;
        if (bullet.Groups["rest"].Value.Trim().Length == 0)
        {
            box.Select(lineStart, caret - lineStart);
            box.SelectedText = "";
            box.CaretIndex = lineStart;
            return;
        }
        var insert = "\n" + bullet.Groups["indent"].Value + "- ";
        box.SelectedText = insert;
        box.CaretIndex = caret + insert.Length;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)-(?<rest>.*)$")]
    private static partial Regex BulletLine();
}
