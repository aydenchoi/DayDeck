using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.App.ViewModels;
using DayDeck.App.Views.Calendar;
using DayDeck.Core.Calendar;
using DayDeck.Core.QuickAdd;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

public enum TaskView
{
    Today,
    Upcoming,
    Overdue,
    Completed,
    List,
    Tag,
}

/// <summary>
/// 할 일 목록 화면. 스마트 목록(오늘/예정/기한 지남/완료됨)과 사용자 목록을 같은 화면으로 보여 준다.
/// 행은 미완료 → 완료 순의 구역으로 나누고, 완료/미완료/기한 지남을 서식으로 구분한다 (설계 §5.2).
/// </summary>
public partial class TaskListPage : UserControl
{
    // 구역 접힘 상태는 화면을 다시 열어도 유지한다 (앱 실행 중에만).
    private static readonly HashSet<string> CollapsedSections = [];

    private readonly App _app = App.Current;
    private readonly TaskView _view;
    private readonly TodoList? _list;
    private readonly string? _tag;

    // 예정·기한 지남·완료됨 화면의 기간 (7일 / 14일, 기본 7일). 화면마다 따로 데이터 폴더(app_state)에 저장한다.
    private static readonly int[] RangeOptions = [7, 14];
    private const int DefaultRangeDays = 7;
    private int _rangeDays = DefaultRangeDays;
    private int _hiddenOverdue;

    // 드래그: 행을 끌어 목록/오늘/태그로 옮기거나, 직접 정렬일 때 순서를 바꾼다.
    public const string DragFormat = "DayDeck.Task";
    private Point? _dragStart;
    private TaskRowViewModel? _dragRow;
    private readonly ObservableCollection<TaskListRow> _rows = [];
    private List<TaskItem> _items = [];
    private List<EventOccurrence> _events = [];
    private string _signature = "";

    public TaskListPage(TaskView view, TodoList? list = null, string? tag = null)
    {
        InitializeComponent();
        _view = view;
        _list = list;
        _tag = tag;
        Rows.ItemsSource = _rows;

        var canAdd = view is TaskView.Today or TaskView.Upcoming or TaskView.List or TaskView.Tag;
        if (view == TaskView.List)
        {
            SortButton.Visibility = Visibility.Visible;
            UpdateSortButton();
        }
        if (HasRange(view))
        {
            _rangeDays = RangeDays(view);
            foreach (var days in RangeOptions)
            {
                RangeChoice.Items.Add(new ComboBoxItem { Content = $"{days}일", Tag = days });
            }
            RangeChoice.SelectedIndex = Math.Max(0, Array.IndexOf(RangeOptions, _rangeDays));
            RangeChoice.Visibility = Visibility.Visible;
        }
        AddBox.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
        AddInput.TextChanged += (_, _) =>
            AddPlaceholder.Visibility = AddInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>행을 선택하고 상세 패널을 연다. 접힌 구역 등으로 행이 없으면 상세만 연다.</summary>
    public void SelectTask(long itemId)
    {
        var row = _rows.OfType<TaskRowViewModel>().FirstOrDefault(r => r.Id == itemId);
        if (row is not null)
        {
            Rows.SelectedItem = row;
            Rows.ScrollIntoView(row);
        }
        else
        {
            Detail.Show(itemId);
            SetDetailVisible(true);
        }
    }

    private string SectionKey(string section) => $"{_view}:{_list?.Id}:{section}";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.Tasks.Changed += OnDataChanged;
        _app.Events.Changed += OnDataChanged;
        _app.Reminders.Changed += OnDataChanged;
        _app.MinuteTick += OnDataChanged;
        Reload();
        if (AddBox.Visibility == Visibility.Visible)
        {
            AddInput.Focus();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _app.Tasks.Changed -= OnDataChanged;
        _app.Events.Changed -= OnDataChanged;
        _app.Reminders.Changed -= OnDataChanged;
        _app.MinuteTick -= OnDataChanged;
        Detail.Close();
    }

    private void OnDataChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        var repo = _app.Tasks;

        _items = _view switch
        {
            TaskView.Today => [.. repo.GetToday()],
            TaskView.Upcoming => [.. repo.GetUpcoming(_rangeDays)],
            TaskView.Overdue => [.. repo.GetOverdue(_rangeDays)],
            TaskView.Completed => [.. repo.GetCompleted(since: today.AddDays(-(_rangeDays - 1)))],
            TaskView.Tag => [.. repo.GetByTag(_tag!)],
            _ => [.. repo.GetByList(_list!.Id)],
        };

        // 기간보다 오래된 기한 지남은 숨기되, 몇 개인지는 알린다 (잊지 않도록).
        _hiddenOverdue = _view == TaskView.Overdue ? repo.GetOverdue().Count - _items.Count : 0;

        var rows = _view switch
        {
            TaskView.Upcoming => BuildByDate(_items, i => i.DueDate!.Value, today, now, SectionTone.Normal),
            TaskView.Completed => BuildByDate(_items, i => DateOnly.FromDateTime(i.CompletedAt ?? now), today, now, SectionTone.Done),
            _ => BuildByState(_items, now),
        };
        // 달력 일정도 함께 보여 준다 (맨 위).
        //  - 오늘: 오늘 일정
        //  - 기한 지남: 기간 안에 끝났는데 완료하지 않은 일정
        //  - 완료됨: 기간 안의 완료한 일정 (최근 것부터)
        _events = _view switch
        {
            TaskView.Today => [.. _app.Events.GetOccurrences(today, today)],
            TaskView.Overdue => [.. _app.Events.GetOccurrences(today.AddDays(-(_rangeDays - 1)), today).Where(e => e.IsPastAndOpen(now))],
            TaskView.Completed => [.. _app.Events.GetOccurrences(today.AddDays(-(_rangeDays - 1)), today)
                .Where(e => e.IsCompleted).OrderByDescending(e => e.Start)],
            _ => [],
        };
        if (_events.Count > 0)
        {
            var tone = _view switch
            {
                TaskView.Overdue => SectionTone.Alert,
                TaskView.Completed => SectionTone.Done,
                _ => SectionTone.Normal,
            };
            var showDate = _view != TaskView.Today;
            rows.InsertRange(0, [new SectionRow("events", "일정", _events.Count, tone, false, false),
                .. _events.Select(e => new EventRowViewModel(e, now, showDate))]);
        }

        UpdateHeader(now);

        var signature = string.Join("\n", rows.Select(r => r.Signature));
        if (signature == _signature)
        {
            return;
        }
        _signature = signature;

        var selectedId = (Rows.SelectedItem as TaskRowViewModel)?.Id;
        _rows.Clear();
        foreach (var row in rows)
        {
            _rows.Add(row);
        }
        if (selectedId is { } id)
        {
            Rows.SelectedItem = _rows.OfType<TaskRowViewModel>().FirstOrDefault(r => r.Id == id);
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _view switch
        {
            TaskView.Today => "오늘 할 일이 없습니다.\n아래 입력창에 할 일을 적어 보세요.",
            TaskView.Overdue when _hiddenOverdue > 0 =>
                $"최근 {_rangeDays}일 안에 기한이 지난 할 일·일정이 없습니다.\n그보다 오래된 {_hiddenOverdue}개가 있습니다 (기간을 넓히거나 목록에서 확인).",
            TaskView.Overdue => "기한이 지난 할 일·일정이 없습니다. 👍",
            TaskView.Completed => $"최근 {_rangeDays}일 동안 완료한 할 일·일정이 없습니다.",
            TaskView.Upcoming => $"앞으로 {_rangeDays}일 안에 마감인 할 일이 없습니다.",
            _ => "할 일이 없습니다.",
        };
    }

    /// <summary>기한 지남 → 해야 할 일 → 완료됨 구역.</summary>
    private List<TaskListRow> BuildByState(List<TaskItem> items, DateTime now)
    {
        var rows = new List<TaskListRow>();
        var showMyDay = _view != TaskView.Today;
        List<TaskItem> overdue, open;
        if (IsManualSort)
        {
            // 직접 정렬: 기한 지남도 따로 떼지 않고 끌어 놓은 순서대로 (기한 지남 표시는 그대로).
            overdue = [];
            open = [.. TaskRules.OrderManual(items.Where(i => i.Status == ItemStatus.Open))];
        }
        else
        {
            overdue = TaskRules.OrderOpen(items.Where(i => TaskRules.IsOverdue(i, now)), now).ToList();
            open = TaskRules.OrderOpen(items.Where(i => i.Status == ItemStatus.Open && !TaskRules.IsOverdue(i, now)), now).ToList();
        }
        var done = TaskRules.OrderDone(items.Where(i => i.Status != ItemStatus.Open)).ToList();

        AddSection(rows, "overdue", "기한 지남", overdue, SectionTone.Alert, collapsible: false, now, showMyDay);
        AddSection(rows, "open", "해야 할 일", open, SectionTone.Normal, collapsible: false, now, showMyDay);
        // 설정: 완료 항목 숨기기 / 흐리게 / 그대로
        var display = _app.Settings.Current.Ui.CompletedTasks;
        if (display != CompletedDisplay.Hide)
        {
            AddSection(rows, "done", "완료됨", done, SectionTone.Done, collapsible: true, now, showMyDay, dim: display == CompletedDisplay.Dim);
        }
        return rows;
    }

    /// <summary>날짜별 구역 (예정: 마감일, 완료됨: 완료일).</summary>
    private List<TaskListRow> BuildByDate(List<TaskItem> items, Func<TaskItem, DateOnly> dateOf, DateOnly today, DateTime now, SectionTone tone)
    {
        var rows = new List<TaskListRow>();
        foreach (var group in items.GroupBy(dateOf))
        {
            var title = TaskFormatting.FormatDate(group.Key, today);
            if (title is "오늘" or "내일" or "어제" or "모레")
            {
                title += $" · {group.Key.Month}/{group.Key.Day}";
            }
            AddSection(rows, group.Key.ToString("yyyy-MM-dd"), title, [.. group], tone, collapsible: false, now, showMyDay: true);
        }
        return rows;
    }

    private void AddSection(List<TaskListRow> rows, string key, string title, List<TaskItem> items, SectionTone tone,
        bool collapsible, DateTime now, bool showMyDay, bool dim = false)
    {
        if (items.Count == 0)
        {
            return;
        }

        var collapsed = collapsible && CollapsedSections.Contains(SectionKey(key));
        rows.Add(new SectionRow(key, title, items.Count, tone, collapsible, collapsed));
        if (!collapsed)
        {
            rows.AddRange(items.Select(i => new TaskRowViewModel(i, now, showMyDay) { IsDimmed = dim }));
        }
    }

    private void UpdateHeader(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        TitleText.Text = _view switch
        {
            TaskView.Today => "오늘",
            TaskView.Upcoming => "예정",
            TaskView.Overdue => "기한 지남",
            TaskView.Completed => "완료됨",
            TaskView.Tag => "@" + _tag,
            _ => _list!.Name,
        };

        var open = _items.Count(i => i.Status == ItemStatus.Open);
        var done = _items.Count - open;
        SubtitleText.Text = _view switch
        {
            TaskView.Today => TaskFormatting.FormatDayHeader(today),
            TaskView.Upcoming => $"{TaskFormatting.FormatDate(today, today)}부터 {_rangeDays}일 동안 · {open}개",
            TaskView.Overdue => $"최근 {_rangeDays}일 안에 마감 · 미완료 {open}개"
                                + (_events.Count > 0 ? $" · 일정 {_events.Count}개" : "")
                                + (_hiddenOverdue > 0 ? $" · 그보다 오래된 {_hiddenOverdue}개 숨김" : ""),
            TaskView.Completed => $"최근 {_rangeDays}일 완료 {done}개"
                                  + (_events.Count > 0 ? $" · 일정 {_events.Count}개" : ""),
            _ => $"미완료 {open}개 · 완료 {done}개",
        };

        var showProgress = _view is TaskView.Today or TaskView.List or TaskView.Tag && _items.Count > 0;
        ProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        if (showProgress)
        {
            var ratio = (double)done / _items.Count;
            ProgressText.Text = _view == TaskView.Today ? $"오늘 {_items.Count}개 중 {done}개 완료" : $"{_items.Count}개 중 {done}개 완료";
            ProgressBar.Value = ratio;
            ProgressPercent.Text = $"{ratio:P0}";
        }
    }

    // ---------- 정렬, 드래그 ----------

    private string? SortKey => _list is { } list ? $"list.sort.{list.Id}" : null;

    /// <summary>목록별 정렬 방식 (app_state에 저장 → 다른 PC로 옮겨도 유지).</summary>
    private bool IsManualSort => SortKey is { } key && _app.AppState.Get(key) == "manual";

    private void UpdateSortButton()
    {
        SortButton.Content = IsManualSort ? "↕ 직접 정렬" : "⏱ 마감순";
        SortButton.ToolTip = IsManualSort
            ? "끌어서 순서를 바꿀 수 있습니다. 누르면 마감순으로"
            : "기한 지남 → 마감 빠른 순 → 우선순위. 누르면 직접 정렬로 (끌어서 순서 바꾸기)";
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (SortKey is not { } key)
        {
            return;
        }
        _app.AppState.Set(key, IsManualSort ? null : "manual");
        UpdateSortButton();
        _signature = "";
        Reload();
    }

    private void OnRowsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragRow = null;
        // 체크 원(버튼)을 누른 것은 드래그가 아니다.
        for (var element = e.OriginalSource as DependencyObject; element is not null and not ListBoxItem; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return;
            }
        }
        if (ItemsControl.ContainerFromElement(Rows, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: TaskRowViewModel row })
        {
            _dragStart = e.GetPosition(Rows);
            _dragRow = row;
        }
    }

    private void OnRowsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || _dragRow is not { } row)
        {
            return;
        }
        var delta = e.GetPosition(Rows) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragStart = null;
        DragDrop.DoDragDrop(Rows, new DataObject(DragFormat, row.Item), DragDropEffects.Move);
    }

    private TaskRowViewModel? DropTarget(DragEventArgs e) =>
        ItemsControl.ContainerFromElement(Rows, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: TaskRowViewModel row } ? row : null;

    private void OnRowsDragOver(object sender, DragEventArgs e)
    {
        var canReorder = IsManualSort && e.Data.GetData(DragFormat) is TaskItem { Status: ItemStatus.Open } dragged
                         && dragged.ListId == _list?.Id && DropTarget(e) is not { Item.Status: not ItemStatus.Open };
        e.Effects = canReorder ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRowsDrop(object sender, DragEventArgs e)
    {
        if (!IsManualSort || e.Data.GetData(DragFormat) is not TaskItem { Status: ItemStatus.Open } dragged || dragged.ListId != _list?.Id)
        {
            return;
        }
        var target = DropTarget(e);
        if (target?.Id == dragged.Id)
        {
            return;
        }
        // 행의 아래쪽 절반에 놓으면 그 다음으로.
        long? beforeId = target?.Id;
        if (target is not null && ItemsControl.ContainerFromElement(Rows, e.OriginalSource as DependencyObject) is ListBoxItem container
            && e.GetPosition(container).Y > container.ActualHeight / 2)
        {
            var open = _rows.OfType<TaskRowViewModel>().Where(r => r.Item.Status == ItemStatus.Open).ToList();
            var index = open.FindIndex(r => r.Id == target.Id);
            beforeId = index + 1 < open.Count ? open[index + 1].Id : null;
            if (beforeId == dragged.Id)
            {
                beforeId = index + 2 < open.Count ? open[index + 2].Id : null;
            }
        }
        _app.Tasks.MoveBefore(dragged.Id, beforeId);
        e.Handled = true;
    }

    private static bool HasRange(TaskView view) => view is TaskView.Upcoming or TaskView.Overdue or TaskView.Completed;

    private static string RangeKey(TaskView view) => $"tasks.range.{view.ToString().ToLowerInvariant()}";

    /// <summary>저장한 기간 (7 또는 14일, 기본 7일). 탐색 메뉴의 "예정" 개수도 이 기간으로 센다.</summary>
    internal static int RangeDays(TaskView view) =>
        App.Current.AppState.Get(RangeKey(view)) is { } saved && int.TryParse(saved, out var days) && RangeOptions.Contains(days) ? days : DefaultRangeDays;

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangeChoice.SelectedItem is not ComboBoxItem { Tag: int days } || days == _rangeDays)
        {
            return;
        }
        _rangeDays = days;
        try
        {
            _app.AppState.Set(RangeKey(_view), days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            DayDeck.Core.Diagnostics.Log.Warn("기간 저장 실패", ex);
        }
        if (IsLoaded)
        {
            Reload();
            (Window.GetWindow(this) as MainWindow)?.RefreshCounts();
        }
    }

    // ---------- 입력 ----------

    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AddInput.Clear();
            return;
        }
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(AddInput.Text))
        {
            return;
        }

        // 빠른 입력과 같은 해석기를 쓴다: "내일 3시 치과 #개인"처럼 쓰면 날짜/일정/목록이 정해진다.
        var context = _view switch
        {
            TaskView.Upcoming => new QuickAddContext(DefaultDueDate: DateOnly.FromDateTime(DateTime.Now)),
            TaskView.List => new QuickAddContext(ListId: _list!.Id, MyDayWhenNoDate: false),
            _ => new QuickAddContext(),
        };
        // 태그 화면에서 넣으면 그 태그를 붙인다.
        var input = _view == TaskView.Tag ? $"{AddInput.Text} @{_tag}" : AddInput.Text;
        var created = _app.QuickAdd.Create(new QuickAddParser(DateTime.Now).Parse(input), context);
        if (created.Kind == QuickAddKind.Event)
        {
            (Window.GetWindow(this) as MainWindow)?.ShowUndo($"일정으로 추가됨: {created.Summary}", () => _app.Events.Delete(created.Id));
        }
        AddInput.Clear();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control && AddBox.Visibility == Visibility.Visible)
        {
            AddInput.Focus();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnRowsKeyDown(object sender, KeyEventArgs e)
    {
        if (Rows.SelectedItem is not TaskRowViewModel row || e.OriginalSource is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                ToggleCompleted(row);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteTask(row);
                e.Handled = true;
                break;
            case Key.Enter:
                Detail.FocusTitle();
                e.Handled = true;
                break;
        }
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TaskRowViewModel row })
        {
            ToggleCompleted(row);
        }
    }

    private void OnSectionClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SectionRow { IsCollapsible: true } section })
        {
            var key = SectionKey(section.Key);
            if (!CollapsedSections.Remove(key))
            {
                CollapsedSections.Add(key);
            }
            Reload();
        }
        e.Handled = true;
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is EventRowViewModel eventRow)
        {
            Rows.SelectedItem = null;
            EventEditorWindow.Edit(Window.GetWindow(this), eventRow.Occurrence);
            return;
        }
        if (Rows.SelectedItem is TaskRowViewModel row)
        {
            Detail.Show(row.Id);
            SetDetailVisible(true);
        }
    }

    private const string ListWidthKey = "tasks.list";
    private const double DefaultListWidth = 400;

    /// <summary>
    /// 상세를 열면 목록은 저장한 너비(기본 400)로 왼쪽에, 상세는 나머지 전부를 차지한다 (메모 화면처럼).
    /// 닫으면 목록이 다시 전체 너비를 쓴다.
    /// </summary>
    private void SetDetailVisible(bool visible)
    {
        Detail.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            ListColumn.Width = new GridLength(Math.Clamp(_app.Layout.GetWidth(ListWidthKey) ?? DefaultListWidth, 300, 900));
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.MinWidth = 380;
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = GridLength.Auto;
            DetailColumn.MinWidth = 0;
        }
    }

    private void OnDetailCloseRequested(object? sender, EventArgs e)
    {
        Detail.Close();
        SetDetailVisible(false);
        Rows.SelectedItem = null;
    }

    private void OnRowsContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Rows.SelectedItem is not TaskRowViewModel row || Rows.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        var item = row.Item;
        var isMyDay = item.MyDay == DateOnly.FromDateTime(DateTime.Now);
        menu.Items.Add(MenuItem(item.Status == ItemStatus.Open ? "완료" : "완료 취소", () => ToggleCompleted(row), "Space"));
        menu.Items.Add(MenuItem(isMyDay ? "오늘 할 일에서 빼기" : "☀ 오늘 할 일에 추가", () => _app.Tasks.SetMyDay(item.Id, !isMyDay)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("마감: 오늘", () => SetDue(item, DateOnly.FromDateTime(DateTime.Now))));
        menu.Items.Add(MenuItem("마감: 내일", () => SetDue(item, DateOnly.FromDateTime(DateTime.Now).AddDays(1))));
        menu.Items.Add(MenuItem("마감 지우기", () => SetDue(item, null)));
        menu.Items.Add(new Separator());
        var priority = new MenuItem { Header = "우선순위" };
        foreach (var (label, value) in new[] { ("높음", Priority.High), ("보통", Priority.Medium), ("낮음", Priority.Low), ("없음", Priority.None) })
        {
            var option = MenuItem(label, () => { item.Priority = value; _app.Tasks.Update(item); });
            option.IsChecked = item.Priority == value;
            priority.Items.Add(option);
        }
        menu.Items.Add(priority);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("삭제", () => DeleteTask(row), "Del"));

        static MenuItem MenuItem(string header, Action action, string? gesture = null)
        {
            var menuItem = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            menuItem.Click += (_, _) => action();
            return menuItem;
        }
    }

    // ---------- 동작 ----------

    private void ToggleCompleted(TaskRowViewModel row)
    {
        var complete = row.Item.Status == ItemStatus.Open;
        var id = row.Id;
        _app.Tasks.SetCompleted(id, complete);
        Detail.Refresh();
        (Window.GetWindow(this) as MainWindow)?.ShowUndo(
            complete ? $"'{row.Title}' 완료" + (row.Item.IsRecurring ? " · 다음 회차를 만들었습니다" : "") : $"'{row.Title}' 완료 취소",
            () => _app.Tasks.SetCompleted(id, !complete));
    }

    private void SetDue(TaskItem item, DateOnly? date)
    {
        item.DueDate = date;
        if (date is null)
        {
            item.DueTime = null;
        }
        _app.Tasks.Update(item);
        if (date is not null)
        {
            _app.Reminders.AddDefaultsIfNone(item.Id);
        }
        Detail.Refresh();
    }

    private void DeleteTask(TaskRowViewModel row)
    {
        var index = _rows.IndexOf(row);
        if (Detail.CurrentId == row.Id)
        {
            OnDetailCloseRequested(this, EventArgs.Empty);
        }

        _app.Tasks.Delete(row.Id);
        (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{row.Title}' 삭제됨", () => _app.Tasks.Restore(row.Id));

        // 다음 행을 선택해서 키보드로 연속 삭제할 수 있게 한다.
        var next = _rows.Skip(Math.Max(0, index)).OfType<TaskRowViewModel>().FirstOrDefault()
                   ?? _rows.OfType<TaskRowViewModel>().LastOrDefault();
        if (next is not null)
        {
            Rows.SelectedItem = next;
            (Rows.ItemContainerGenerator.ContainerFromItem(next) as ListBoxItem)?.Focus();
        }
    }
}
