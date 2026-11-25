using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;
using DayDeck.Core.Settings;

namespace DayDeck.App.Views;

/// <summary>
/// 주간업무보고: 주차(목요일 기준)마다 양식의 항목(기본: 금주실적·차주계획·이슈사항·문의사항)을 칸마다 써서
/// 메모 폴더의 <c>주간업무보고\연도\</c>에 .txt로 저장한다. 양식은 설정 → 주간업무보고에서 바꾼다.
/// 제목과 날짜는 주차에서 만들어 보여 주기만 하고 저장할 때마다 다시 쓰므로 잘못 적을 수 없다.
/// 새 주를 열면 지난주의 차주 항목을 금주 항목에 미리 넣는다. "복사"는 보고 전체를 그대로 클립보드에.
/// </summary>
public partial class WeeklyReportPage : UserControl
{
    private readonly App _app = App.Current;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly List<SectionEditor> _sections = [];

    /// <summary>이 화면을 여는 동안 쓰는 양식 (설정을 바꾸면 화면을 다시 열 때 반영된다).</summary>
    private readonly WeeklyReportSettings _template;

    /// <summary>꺼 둔 항목에 이미 쓴 내용. 화면에는 없지만 저장할 때 그대로 남긴다.</summary>
    private Dictionary<string, string> _hidden = [];

    private ReportWeek _week = ReportWeek.Of(Today);
    private bool _loading;
    private bool _dirty;

    private sealed record SectionEditor(ReportSection Section, TextBlock Header, TextBox Box);

    public WeeklyReportPage()
    {
        InitializeComponent();
        _template = Snapshot(_app.Settings.Current.WeeklyReport);
        BuildSections();
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

    private static WeeklyReportSettings Snapshot(WeeklyReportSettings settings) => new()
    {
        Title = settings.Title,
        Sections = [.. settings.Sections.Select(s => new ReportSection(s.Name, s.Dates) { Enabled = s.Enabled, Aliases = [.. s.Aliases] })],
    };

    /// <summary>양식의 켜 둔 항목마다 머리글과 입력 칸을 만든다. 칸은 남은 높이를 나눠 갖는다.</summary>
    private void BuildSections()
    {
        var notes = _app.Settings.Current.Notes;
        var first = true;
        foreach (var section in _template.ActiveSections())
        {
            var header = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 14, 0, 4) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(6, 4, 6, 4),
                FontFamily = Services.FontService.EditorFont(notes),
                FontSize = notes.EditorFontSize,
            };
            System.Windows.Automation.AutomationProperties.SetName(box, section.Name);
            box.TextChanged += OnEdited;
            box.PreviewKeyDown += OnBulletKey;
            box.LostKeyboardFocus += (_, _) => Save();

            SectionsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SectionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 64 });
            Grid.SetRow(header, SectionsGrid.RowDefinitions.Count - 2);
            Grid.SetRow(box, SectionsGrid.RowDefinitions.Count - 1);
            SectionsGrid.Children.Add(header);
            SectionsGrid.Children.Add(box);
            _sections.Add(new SectionEditor(section, header, box));
            first = false;
        }
    }

    /// <summary>지금 화면의 항목별 내용 (꺼 둔 항목의 예전 내용 포함).</summary>
    private Dictionary<string, string> CurrentBodies()
    {
        var bodies = new Dictionary<string, string>(_hidden, StringComparer.Ordinal);
        foreach (var editor in _sections)
        {
            bodies[editor.Section.Name] = editor.Box.Text;
        }
        return bodies;
    }

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

    /// <summary>그 주의 보고를 연다. 아직 없으면 빈 보고 (지난주 차주 항목을 금주 항목으로).</summary>
    private void Show(ReportWeek week)
    {
        Save();
        _week = week;
        _loading = true;
        try
        {
            var path = WeeklyReport.PathOf(week);
            var saved = _app.Notes.Exists(path);
            Dictionary<string, string> bodies;
            string status;
            if (saved)
            {
                bodies = WeeklyReport.Parse(_app.Notes.Read(path), _template);
                status = "저장된 보고입니다. 고치면 자동 저장됩니다.";
            }
            else
            {
                var previous = WeeklyReport.PathOf(week.Previous);
                bodies = _app.Notes.Exists(previous) ? WeeklyReport.CarryOver(_app.Notes.Read(previous), _template) : [];
                status = bodies.Count > 0
                    ? "새 보고 — 지난주 차주 항목을 이번 주 항목에 넣었습니다. 고쳐서 쓰면 저장됩니다."
                    : "새 보고 — 내용을 쓰면 저장됩니다.";
            }
            _hidden = bodies.Where(kv => _sections.All(e => e.Section.Name != kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            WeekTitle.Text = WeeklyReport.Title(week, _template);
            WeekLabel.Text = $"{week.Label} · {week.Monday:yyyy-MM-dd}(월) ~ {week.Friday:yyyy-MM-dd}(금)";
            CurrentChip.Visibility = week == ReportWeek.Of(Today) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var editor in _sections)
            {
                editor.Header.Text = WeeklyReport.Header(week, editor.Section);
                var body = bodies.GetValueOrDefault(editor.Section.Name);
                editor.Box.Text = body is null || WeeklyReport.IsBlank(body) ? WeeklyReport.EmptyItem : body;
            }
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
        if (_sections.FirstOrDefault()?.Box is { } firstBox)
        {
            firstBox.Focus();
            firstBox.CaretIndex = firstBox.Text.Length;
        }
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
        var bodies = CurrentBodies();
        var text = WeeklyReport.Write(_week, _template, bodies);
        try
        {
            if (_app.Notes.Exists(path))
            {
                _app.Notes.Save(path, text);
            }
            else if (bodies.Values.All(WeeklyReport.IsBlank))
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
            Clipboard.SetText(WeeklyReport.Write(_week, _template, CurrentBodies()).Replace("\n", "\r\n"));
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
