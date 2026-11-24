using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DayDeck.App.Services;
using DayDeck.App.ViewModels;
using DayDeck.App.Views.Calendar;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

public enum MainPage
{
    Today,
    Upcoming,
    Overdue,
    Completed,
    Calendar,
    Notes,
    Trash,
    Settings,
    WeeklyReport,
    Workflow,
}

public partial class MainWindow : Window
{
    private const string ListKeyPrefix = "list:";
    private const string NewListKey = "action:new-list";
    private const string SearchKey = "action:search";
    private const string TagKeyPrefix = "tag:";
    private const string TagsHeaderKey = "header:tags";

    private readonly App _app = App.Current;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly ObservableCollection<NavItem> _nav = [];
    private readonly NavItem _today = new(nameof(MainPage.Today), NavItemKind.Page, "☀", "오늘");
    private readonly NavItem _upcoming = new(nameof(MainPage.Upcoming), NavItemKind.Page, "▦", "예정");
    private readonly NavItem _overdue = new(nameof(MainPage.Overdue), NavItemKind.Page, "⚠", "기한 지남") { IsAlert = true };
    private readonly NavItem _completed = new(nameof(MainPage.Completed), NavItemKind.Page, "✓", "완료됨");
    private NavItem? _current;
    private Action? _undo;
    private bool _syncingNav;

    public MainWindow()
    {
        InitializeComponent();
        Nav.ItemsSource = _nav;
        BuildNav();

        _memoryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _memoryTimer.Tick += (_, _) => UpdateMemoryStatus();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _undoTimer.Tick += (_, _) => HideUndo();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateMemoryStatus();
                _memoryTimer.Start();
            }
            else
            {
                _memoryTimer.Stop();
            }
        };

        _app.Tasks.Changed += OnTasksChanged;
        _app.MinuteTick += OnMinuteTick;
        _app.AutoSave.Edited += OnAutoSaveEdited;
        _app.AutoSave.Saved += OnAutoSaveSaved;
        _app.Settings.Saved += OnSettingsSaved;
        Deactivated += OnDeactivated;
        UpdateAutoSaveInfo();
        Services.FontService.ScaleWindow(this);
        RestoreSavedBounds();

        Navigate(MainPage.Today);
    }

    public void Navigate(MainPage page) => SelectNav(page.ToString());

    /// <summary>메모 화면이 이미 열려 있으면 바로 새 메모를 만든다 (아니면 화면이 열릴 때 만든다).</summary>
    public void ConsumePendingNewNote()
    {
        if (PageHost.Content is not NotesPage { IsLoaded: true } notes)
        {
            return;
        }
        if (_app.PendingNewNote)
        {
            _app.PendingNewNote = false;
            notes.NewNote();
        }
        if (_app.PendingOpenNote is { } path)
        {
            _app.PendingOpenNote = null;
            notes.OpenNote(path);
        }
    }

    /// <summary>달력에서 일정이 있는 날을 연다.</summary>
    public void OpenEvent(CalendarEvent calendarEvent, DateTime? occurrenceStart)
    {
        Navigate(MainPage.Calendar);
        var date = DateOnly.FromDateTime(occurrenceStart ?? calendarEvent.Start);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (PageHost.Content as CalendarPage)?.Show(date, CalendarViewMode.Day));
    }

    /// <summary>할 일이 있는 화면(오늘 또는 그 목록)으로 가서 선택하고 상세를 연다.</summary>
    public void OpenTask(long itemId)
    {
        if (_app.Tasks.Get(itemId) is not { } item)
        {
            return;
        }

        var inToday = _app.Tasks.GetToday().Any(i => i.Id == itemId);
        SelectNav(inToday || item.ListId is null ? nameof(MainPage.Today) : ListKeyPrefix + item.ListId);
        // 화면이 로드되어 행이 만들어진 뒤에 선택한다.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (PageHost.Content as TaskListPage)?.SelectTask(itemId));
    }

    /// <summary>하단에 "삭제됨 · 실행 취소"를 잠시 보여 준다.</summary>
    public void ShowUndo(string message, Action undo)
    {
        _undo = undo;
        UndoText.Text = message;
        UndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            MainPage? page = e.Key switch
            {
                System.Windows.Input.Key.D1 => MainPage.Today,
                System.Windows.Input.Key.D2 => MainPage.Upcoming,
                System.Windows.Input.Key.D3 => MainPage.Calendar,
                System.Windows.Input.Key.D4 => MainPage.Notes,
                System.Windows.Input.Key.D5 => MainPage.Settings,
                _ => null,
            };
            if (page is { } target)
            {
                Navigate(target);
                e.Handled = true;
                return;
            }
            // 메모 편집기(마크다운)에서는 Ctrl+K가 "링크 넣기"다 (서식 단축키를 끄면 검색).
            if (e.Key == System.Windows.Input.Key.K
                && (System.Windows.Input.Keyboard.FocusedElement is not (TextBox { Tag: MarkdownCommands.Tag } or MarkdownRichEditor or ICSharpCode.AvalonEdit.Editing.TextArea) || !MarkdownCommands.ShortcutsEnabled))
            {
                _app.ShowSearch();
                e.Handled = true;
                return;
            }
            // 입력란 밖에서 Ctrl+Z: 방금 한 일(삭제, 완료 등)을 되돌린다. 입력란 안에서는 글자 되돌리기.
            if (e.Key == System.Windows.Input.Key.Z && _undo is not null
                && System.Windows.Input.Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase or ICSharpCode.AvalonEdit.Editing.TextArea or Drawing.DrawingCanvas))
            {
                OnUndoClick(this, e);
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _app.AutoSave.FlushAll();
        SaveBounds();
        base.OnClosing(e);
    }

    /// <summary>지난번 창 위치·크기(최대화 포함)로 연다. 다른 PC로 옮겨 화면이 다르면 화면 안으로 들인다.</summary>
    private void RestoreSavedBounds()
    {
        if (_app.Layout.GetMainWindow() is not { } saved)
        {
            return;
        }
        var bounds = saved.FitToScreen(MinWidth, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (bounds.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>최대화·최소화 상태면 복원했을 때의 위치·크기를 저장한다.</summary>
    private void SaveBounds()
    {
        var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (normal.IsEmpty || normal.Width < 100 || normal.Height < 100)
        {
            return;
        }
        _app.Layout.SetMainWindow(new Services.WindowBounds(normal.Left, normal.Top, normal.Width, normal.Height,
            WindowState == WindowState.Maximized));
    }

    protected override void OnClosed(EventArgs e)
    {
        _memoryTimer.Stop();
        _undoTimer.Stop();
        _app.Tasks.Changed -= OnTasksChanged;
        _app.MinuteTick -= OnMinuteTick;
        _app.AutoSave.Edited -= OnAutoSaveEdited;
        _app.AutoSave.Saved -= OnAutoSaveSaved;
        _app.Settings.Saved -= OnSettingsSaved;
        PageHost.Content = null;
        _process.Dispose();
        base.OnClosed(e);
    }

    // ---------- 탐색 ----------

    private void BuildNav()
    {
        _nav.Add(new NavItem(SearchKey, NavItemKind.Action, "⌕", "검색   Ctrl+K"));
        _nav.Add(new NavItem("sep:0", NavItemKind.Separator));
        _nav.Add(_today);
        _nav.Add(_upcoming);
        _nav.Add(_overdue);
        _nav.Add(_completed);
        _nav.Add(new NavItem("sep:1", NavItemKind.Separator));
        _nav.Add(new NavItem("header:lists", NavItemKind.Header, title: "목록"));
        // 목록 항목은 RefreshNav에서 이 자리에 넣는다.
        _nav.Add(new NavItem(NewListKey, NavItemKind.Action, "＋", "새 목록"));
        _nav.Add(new NavItem("sep:2", NavItemKind.Separator));
        _nav.Add(new NavItem(nameof(MainPage.Calendar), NavItemKind.Page, "📅", "달력"));
        _nav.Add(new NavItem(nameof(MainPage.Notes), NavItemKind.Page, "📝", "메모"));
        _nav.Add(new NavItem(nameof(MainPage.Workflow), NavItemKind.Page, "✏", "워크플로우"));
        _nav.Add(new NavItem(nameof(MainPage.WeeklyReport), NavItemKind.Page, "📋", "주간업무보고"));
        _nav.Add(new NavItem("sep:3", NavItemKind.Separator));
        _nav.Add(new NavItem(nameof(MainPage.Trash), NavItemKind.Page, "🗑", "휴지통"));
        _nav.Add(new NavItem(nameof(MainPage.Settings), NavItemKind.Page, "⚙", "설정"));
        RefreshNav();
    }

    /// <summary>탐색 메뉴의 개수를 다시 센다 (예정 기간을 바꿨을 때 등).</summary>
    internal void RefreshCounts() => RefreshNav();

    /// <summary>개수와 목록을 갱신한다. 선택은 유지한다.</summary>
    private void RefreshNav()
    {
        RefreshTags();
        var counts = _app.Tasks.GetCounts(TaskListPage.RangeDays(TaskView.Upcoming));
        _today.Count = counts.TodayOpen;
        _upcoming.Count = counts.Upcoming;
        _overdue.Count = counts.Overdue;

        var lists = _app.Tasks.GetLists();
        var existing = _nav.Where(n => n.Kind == NavItemKind.List).ToList();
        var sameLists = existing.Count == lists.Count
                        && existing.Zip(lists).All(p => p.First.ListId == p.Second.Id && p.First.Title == p.Second.Name
                                                        && p.First.ListColor == p.Second.Color);
        if (sameLists)
        {
            foreach (var (item, list) in existing.Zip(lists))
            {
                item.Count = list.OpenCount;
            }
            return;
        }

        _syncingNav = true;
        try
        {
            var selectedKey = (Nav.SelectedItem as NavItem)?.Key;
            foreach (var item in existing)
            {
                _nav.Remove(item);
            }

            var insertAt = _nav.IndexOf(_nav.First(n => n.Key == NewListKey));
            foreach (var list in lists)
            {
                _nav.Insert(insertAt++, new NavItem(ListKeyPrefix + list.Id, NavItemKind.List, title: list.Name,
                    dot: TaskRowViewModel.TryParseBrush(list.Color) ?? (System.Windows.Media.Brush)FindResource("MutedBrush"))
                {
                    ListId = list.Id,
                    ListColor = list.Color,
                    Count = list.OpenCount,
                });
            }

            if (selectedKey is not null)
            {
                Nav.SelectedItem = _nav.FirstOrDefault(n => n.Key == selectedKey);
            }
        }
        finally
        {
            _syncingNav = false;
        }

        if (_current?.Kind == NavItemKind.List)
        {
            var replacement = _nav.FirstOrDefault(n => n.Key == _current.Key);
            if (replacement is null)
            {
                // 보고 있던 목록이 지워졌으면 오늘 화면으로 간다.
                Navigate(MainPage.Today);
            }
            else if (replacement.Title != _current.Title)
            {
                // 이름이 바뀌었으면 제목을 다시 그리도록 다시 연다.
                OpenPage(replacement);
            }
            else
            {
                _current = replacement;
            }
        }
    }

    /// <summary>태그가 있으면 목록 아래에 "태그" 구역을 보여 준다.</summary>
    private void RefreshTags()
    {
        var tags = _app.Tasks.GetTags();
        var existing = _nav.Where(n => n.Kind == NavItemKind.Tag).ToList();
        if (existing.Select(n => n.Title).SequenceEqual(tags.Select(t => t.Name)))
        {
            foreach (var (item, tag) in existing.Zip(tags))
            {
                item.Count = tag.OpenCount;
            }
            return;
        }

        _syncingNav = true;
        try
        {
            var selectedKey = (Nav.SelectedItem as NavItem)?.Key;
            foreach (var item in existing)
            {
                _nav.Remove(item);
            }
            if (_nav.FirstOrDefault(n => n.Key == TagsHeaderKey) is { } header)
            {
                _nav.Remove(header);
            }
            if (tags.Count > 0)
            {
                var insertAt = _nav.IndexOf(_nav.First(n => n.Key == "sep:2"));
                _nav.Insert(insertAt++, new NavItem(TagsHeaderKey, NavItemKind.Header, title: "태그"));
                foreach (var (name, open) in tags)
                {
                    _nav.Insert(insertAt++, new NavItem(TagKeyPrefix + name, NavItemKind.Tag, "@", name) { Count = open });
                }
            }
            if (selectedKey is not null)
            {
                Nav.SelectedItem = _nav.FirstOrDefault(n => n.Key == selectedKey);
            }
        }
        finally
        {
            _syncingNav = false;
        }

        if (_current?.Kind == NavItemKind.Tag && _nav.All(n => n.Key != _current.Key))
        {
            // 보고 있던 태그가 없어졌다.
            Dispatcher.BeginInvoke(() => Navigate(MainPage.Today));
        }
    }

    // ---------- 할 일을 탐색 메뉴로 끌어다 놓기 ----------

    private static NavItem? NavItemAt(DragEventArgs e) => (e.OriginalSource as FrameworkElement)?.DataContext as NavItem;

    /// <summary>목록 → 그 목록으로 옮기기, 오늘 → 오늘 할 일에 추가, 태그 → 태그 붙이기, 완료됨 → 완료.</summary>
    private static bool CanDropOn(NavItem? item, TaskItem task) => item switch
    {
        { Kind: NavItemKind.List } => item.ListId != task.ListId,
        { Kind: NavItemKind.Tag } => !task.Tags.Contains(item.Title),
        { Key: nameof(MainPage.Today) } => task.Status == ItemStatus.Open,
        { Key: nameof(MainPage.Completed) } => task.Status == ItemStatus.Open,
        _ => false,
    };

    private void OnNavDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(TaskListPage.DragFormat) is TaskItem task && CanDropOn(NavItemAt(e), task)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnNavDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TaskListPage.DragFormat) is not TaskItem task || NavItemAt(e) is not { } item || !CanDropOn(item, task))
        {
            return;
        }
        e.Handled = true;
        var tasks = _app.Tasks;
        var id = task.Id;
        switch (item)
        {
            case { Kind: NavItemKind.List }:
                var previous = task.ListId;
                tasks.MoveToList(id, item.ListId);
                ShowUndo($"'{task.Title}' → {item.Title}", () => tasks.MoveToList(id, previous));
                break;
            case { Kind: NavItemKind.Tag }:
                var tags = task.Tags;
                tasks.AddTag(id, item.Title);
                ShowUndo($"'{task.Title}'에 @{item.Title}", () => tasks.SetTags(id, tags));
                break;
            case { Key: nameof(MainPage.Today) }:
                var wasMyDay = task.MyDay == DateOnly.FromDateTime(DateTime.Now);
                tasks.SetMyDay(id, true);
                ShowUndo($"'{task.Title}'을(를) 오늘 할 일에 추가", () => tasks.SetMyDay(id, wasMyDay));
                break;
            case { Key: nameof(MainPage.Completed) }:
                tasks.SetCompleted(id, true);
                ShowUndo($"'{task.Title}' 완료", () => tasks.SetCompleted(id, false));
                break;
        }
    }

    private void SelectNav(string key)
    {
        Nav.SelectedItem = _nav.FirstOrDefault(n => n.Key == key);
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav || Nav.SelectedItem is not NavItem item)
        {
            return;
        }

        if (item.Key == SearchKey)
        {
            Nav.SelectedItem = _current;
            _app.ShowSearch();
            return;
        }

        if (item.Key == NewListKey)
        {
            Nav.SelectedItem = _current;
            CreateList();
            return;
        }

        if (item.Key == _current?.Key && PageHost.Content is not null)
        {
            return;
        }

        OpenPage(item);
    }

    private void OpenPage(NavItem item)
    {
        _current = item;
        // 페이지는 캐시하지 않는다. 보이지 않는 화면이 메모리를 잡지 않도록.
        PageHost.Content = item.Kind == NavItemKind.List
            ? new TaskListPage(TaskView.List, _app.Tasks.GetLists().First(l => l.Id == item.ListId))
            : item.Kind == NavItemKind.Tag
            ? new TaskListPage(TaskView.Tag, tag: item.Title)
            : Enum.Parse<MainPage>(item.Key) switch
            {
                MainPage.Today => new TaskListPage(TaskView.Today),
                MainPage.Upcoming => new TaskListPage(TaskView.Upcoming),
                MainPage.Overdue => new TaskListPage(TaskView.Overdue),
                MainPage.Completed => new TaskListPage(TaskView.Completed),
                MainPage.Calendar => new CalendarPage(),
                MainPage.Notes => new NotesPage(),
                MainPage.WeeklyReport => new WeeklyReportPage(),
                MainPage.Workflow => new Drawing.WorkflowPage(),
                MainPage.Trash => new TrashPage(),
                _ => new SettingsPage(),
            };
    }

    private void OnNavContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        if (element?.DataContext is not NavItem { Kind: NavItemKind.List } item || Nav.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        var rename = new MenuItem { Header = "이름 바꾸기" };
        rename.Click += (_, _) =>
        {
            if (InputDialog.Ask(this, "목록 이름 바꾸기", "새 이름", item.Title) is { } name)
            {
                _app.Tasks.RenameList(item.ListId!.Value, name);
            }
        };
        var delete = new MenuItem { Header = "목록 삭제" };
        delete.Click += (_, _) =>
        {
            var answer = MessageBox.Show(this,
                $"'{item.Title}' 목록을 삭제할까요?\n목록 안의 할 일 {item.Count}개(미완료 기준)도 함께 삭제됩니다.",
                "목록 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK)
            {
                _app.Tasks.DeleteList(item.ListId!.Value);
            }
        };
        menu.Items.Add(rename);
        menu.Items.Add(ColorMenu(item));
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
    }

    /// <summary>목록 색: 미리 정한 색 중에서 고르거나 #RRGGBB를 직접 입력한다.</summary>
    private MenuItem ColorMenu(NavItem item)
    {
        var listId = item.ListId!.Value;
        var colors = new MenuItem { Header = "색 바꾸기" };
        foreach (var (name, hex) in ListPalette.Colors)
        {
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = TaskRowViewModel.TryParseBrush(hex),
            };
            var choice = new MenuItem
            {
                Header = name,
                Icon = swatch,
                IsChecked = string.Equals(item.ListColor, hex, StringComparison.OrdinalIgnoreCase),
            };
            choice.Click += (_, _) => _app.Tasks.SetListColor(listId, hex);
            colors.Items.Add(choice);
        }
        colors.Items.Add(new Separator());
        var custom = new MenuItem { Header = "직접 입력… (#RRGGBB)" };
        custom.Click += (_, _) =>
        {
            if (InputDialog.Ask(this, "목록 색", "색 코드 (예: #2B6CD4)", item.ListColor ?? "#") is not { } input)
            {
                return;
            }
            var hex = input.Trim();
            if (!hex.StartsWith('#'))
            {
                hex = "#" + hex;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
            {
                MessageBox.Show(this, "색 코드는 #과 16진수 6자리입니다 (예: #2B6CD4).", "목록 색", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _app.Tasks.SetListColor(listId, hex);
        };
        colors.Items.Add(custom);
        var none = new MenuItem { Header = "색 없음", IsChecked = item.ListColor is null };
        none.Click += (_, _) => _app.Tasks.SetListColor(listId, null);
        colors.Items.Add(none);
        return colors;
    }

    private void CreateList()
    {
        if (InputDialog.Ask(this, "새 목록", "목록 이름") is not { } name)
        {
            return;
        }

        var palette = ListPalette.Colors;
        var id = _app.Tasks.AddList(name, palette[_nav.Count(n => n.Kind == NavItemKind.List) % palette.Length].Hex);
        SelectNav(ListKeyPrefix + id);
    }

    // ---------- 상태 표시 ----------

    private void OnTasksChanged(object? sender, EventArgs e)
    {
        RefreshNav();
        SaveStatus.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        SaveStatus.Text = $"✓ 저장됨 {DateTime.Now:HH:mm:ss}";
    }

    private void OnMinuteTick(object? sender, EventArgs e) => RefreshNav();

    private void OnAutoSaveEdited(object? sender, EventArgs e)
    {
        SaveStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        SaveStatus.Text = "● 수정됨";
    }

    private void OnAutoSaveSaved(object? sender, DateTimeOffset time)
    {
        SaveStatus.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        SaveStatus.Text = $"✓ 저장됨 {time:HH:mm:ss}";
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        UpdateAutoSaveInfo();
        SaveStatus.Text = $"✓ 설정 저장됨 {DateTime.Now:HH:mm:ss}";
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_app.Settings.Current.AutoSave.SaveOnFocusLost)
        {
            _app.AutoSave.FlushAll();
        }
    }

    private void UpdateAutoSaveInfo()
    {
        var autoSave = _app.Settings.Current.AutoSave;
        AutoSaveInfo.Text = autoSave.SaveAfterEdit
            ? $"자동 저장: {autoSave.IntervalMinutes}분마다 + 편집 후 {autoSave.DebounceSeconds}초"
            : $"자동 저장: {autoSave.IntervalMinutes}분마다";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        var undo = _undo;
        HideUndo();
        undo?.Invoke();
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        _undo = null;
        UndoBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateMemoryStatus()
    {
        // 작업 관리자와 같은 값(개인 작업 집합)을 보여 준다. Working Set은 공유 DLL·런타임까지 세어 훨씬 크게 나온다.
        // 개인 작업 집합을 얻을 수 없는 Windows(10 1809 이전)에서만 Working Set을 보여 준다.
        var heap = $"관리 힙 {GC.GetTotalMemory(forceFullCollection: false) / 1024d / 1024d:0.0}MB";
        var privateWorkingSet = NativeMethods.GetPrivateWorkingSet();
        if (privateWorkingSet is { } bytes)
        {
            MemoryStatus.Text = $"메모리 {bytes / 1024d / 1024d:0.0}MB · {heap}";
            return;
        }

        _process.Refresh();
        MemoryStatus.Text = $"메모리 {_process.WorkingSet64 / 1024d / 1024d:0.0}MB (Working Set) · {heap}";
    }
}
