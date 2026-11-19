using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DayDeck.App.ViewModels;
using DayDeck.Core.Data;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Notes;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

/// <summary>
/// 메모 화면 (설계 §5.5): 폴더 트리 | 메모 목록 | 탭 편집기.
/// 탭마다 TextBox를 따로 둬서 실행 취소 기록과 커서 위치가 섞이지 않는다.
/// 입력은 자동 저장되고(디바운스/주기/포커스 이동/닫기), 다시 열면 탭과 커서 위치를 복원한다.
/// </summary>
public partial class NotesPage : UserControl
{

    private readonly App _app = App.Current;
    private readonly NoteStore _store;
    private readonly AppStateStore _state;
    private readonly ObservableCollection<NoteTabViewModel> _tabs = [];
    private readonly Dictionary<NoteTabViewModel, TextBox> _editors = [];

    /// <summary>"보기" 편집기 (마크다운 탭, 처음 보기로 열 때 만든다).</summary>
    private readonly Dictionary<NoteTabViewModel, MarkdownRichEditor> _rich = [];

    /// <summary>"옵시디언" 편집기 (마크다운 탭, 처음 옵시디언 모드로 열 때 만든다).</summary>
    private readonly Dictionary<NoteTabViewModel, LivePreviewEditor> _live = [];
    private readonly HashSet<string> _expanded = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _searchTimer;
    private FileSystemWatcher? _watcher;
    private NoteTabViewModel? _active;
    private string _folder = "";
    private bool _showingTrash;
    private bool _wordWrap = true;
    private bool _suppressFolderEvent;
    private bool _syncingSelection;
    private Point _dragStart;
    private Point _folderDragStart;
    private NoteFolderViewModel? _folderDragSource;

    // "보기"(기본: 서식이 적용된 모양 그대로 고침) / "옵시디언"(원문에 서식을 입혀 보이고, 커서 줄만 기호가 보임)
    // / "나란히"(원문 + 미리보기) / "편집"(마크다운 원문).
    // 메모 화면을 열 때마다 설정의 시작 방식("보기" 또는 "옵시디언")으로 시작한다.
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private string _viewMode = "view";

    public NotesPage()
    {
        InitializeComponent();
        _store = _app.Notes;
        _state = _app.AppState;
        TabStrip.ItemsSource = _tabs;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshAll();
            foreach (var tab in _tabs)
            {
                tab.CheckExternalChange();
            }
            UpdateEditorChrome();
        };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshList();
        };

        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---------- 수명 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.Settings.Saved += OnSettingsSaved;
        _viewMode = _app.Settings.Current.Notes.StartInObsidianMode ? "live" : "view";
        (_viewMode == "live" ? LiveModeButton : PreviewModeButton).IsChecked = true;
        var session = NoteSession.Load(_state);
        _folder = Directory.Exists(_store.FullPath(session.Folder)) ? session.Folder : "";
        _wordWrap = session.WordWrap;
        WrapToggle.IsChecked = _wordWrap;
        foreach (var part in Ancestors(_folder))
        {
            _expanded.Add(part);
        }

        RefreshAll();
        foreach (var tab in session.Tabs.Where(t => _store.Exists(t.Path)))
        {
            OpenTab(tab.Path, activate: false, caret: tab.CaretIndex);
        }
        var active = _tabs.FirstOrDefault(t => t.Path == session.ActivePath) ?? _tabs.FirstOrDefault();
        if (active is not null)
        {
            Activate(active);
        }
        UpdateEditorChrome();

        _store.Changed += OnStoreChanged;
        StartWatcher();

        if (_app.PendingNewNote)
        {
            _app.PendingNewNote = false;
            NewNote();
        }
        if (_app.PendingOpenNote is { } pending)
        {
            _app.PendingOpenNote = null;
            OpenNote(pending);
        }
    }

    /// <summary>설정에서 편집기 글꼴/크기를 바꾸면 열린 탭에도 바로 적용한다.</summary>
    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        var notes = _app.Settings.Current.Notes;
        var font = Services.FontService.EditorFont(notes);
        foreach (var editor in _editors.Values)
        {
            editor.FontFamily = font;
            editor.FontSize = notes.EditorFontSize;
        }
        foreach (var live in _live.Values)
        {
            live.FontFamily = font;
            live.FontSize = notes.EditorFontSize;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _app.Settings.Saved -= OnSettingsSaved;
        _store.Changed -= OnStoreChanged;
        _watcher?.Dispose();
        _watcher = null;
        foreach (var rich in _rich.Values)
        {
            rich.Flush();
        }
        foreach (var tab in _tabs)
        {
            _app.AutoSave.Unregister(tab);
        }
        SaveSession();
    }

    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_store.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler handler = (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.Deleted += handler;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Log.Warn("메모 폴더 감시를 시작하지 못했습니다", ex);
        }
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnStoreChanged(object? sender, EventArgs e) => ScheduleRefresh();

    private void SaveSession()
    {
        foreach (var (tab, editor) in _editors)
        {
            tab.CaretIndex = CaretOf(tab, editor);
        }
        new NoteSession([.. _tabs.Select(t => new NoteTabState(t.Path, t.CaretIndex))], _active?.Path, _folder, _wordWrap).Save(_state);
    }

    // ---------- 폴더 ----------

    private void RefreshAll()
    {
        RefreshTree();
        RefreshList();
        var trashCount = _store.GetTrash().Count;
        TrashText.Text = trashCount > 0 ? $"🗑 휴지통 ({trashCount})" : "🗑 휴지통";
    }

    private void RefreshTree()
    {
        if (FolderTree.ItemsSource is IEnumerable<NoteFolderViewModel> old)
        {
            foreach (var node in old.SelectMany(n => n.SelfAndDescendants()).Where(n => n.IsExpanded))
            {
                _expanded.Add(node.RelativePath);
            }
        }

        var root = new NoteFolderViewModel(_store.GetFolderTree(), _expanded);
        _suppressFolderEvent = true;
        FolderTree.ItemsSource = new[] { root };
        var selected = root.SelfAndDescendants().FirstOrDefault(n => n.RelativePath == _folder);
        if (selected is null)
        {
            _folder = "";
            selected = root;
        }
        if (!_showingTrash)
        {
            selected.IsSelected = true;
        }
        _suppressFolderEvent = false;
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressFolderEvent || e.NewValue is not NoteFolderViewModel folder)
        {
            return;
        }
        _folder = folder.RelativePath;
        _showingTrash = false;
        SearchBox.Clear();
        RefreshList();
        SaveSession();
    }

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (InputDialog.Ask(Window.GetWindow(this), "새 폴더", $"'{FolderName(_folder)}' 안에 만들 폴더 이름") is not { } name)
        {
            return;
        }
        try
        {
            _expanded.Add(_folder);
            _folder = _store.CreateFolder(_folder, name);
            _showingTrash = false;
            RefreshAll();
        }
        catch (IOException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "새 폴더", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnFolderMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var folder = (e.OriginalSource as FrameworkElement)?.DataContext as NoteFolderViewModel;
        if (folder is null || FolderTree.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        menu.Items.Add(MenuItem("＋ 새 하위 폴더", () =>
        {
            _folder = folder.RelativePath;
            OnNewFolder(this, new RoutedEventArgs());
        }));
        if (!folder.IsRoot)
        {
            menu.Items.Add(MenuItem("이름 바꾸기", () =>
            {
                if (InputDialog.Ask(Window.GetWindow(this), "폴더 이름 바꾸기", "새 이름", folder.Name) is { } name)
                {
                    FlushTabsUnder(folder.RelativePath);
                    var renamed = _store.RenameFolder(folder.RelativePath, name);
                    RetargetTabs(folder.RelativePath, renamed, isFolder: true);
                    _folder = renamed;
                    RefreshAll();
                }
            }));
            menu.Items.Add(MenuItem("삭제 (휴지통으로)", () =>
            {
                var answer = MessageBox.Show(Window.GetWindow(this),
                    $"'{folder.Name}' 폴더와 안의 메모를 휴지통으로 옮길까요? (30일 동안 복원할 수 있습니다)",
                    "폴더 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.OK)
                {
                    FlushTabsUnder(folder.RelativePath);
                    CloseTabsUnder(folder.RelativePath);
                    _store.DeleteFolder(folder.RelativePath);
                    _folder = "";
                    RefreshAll();
                }
            }));
        }
    }

    private void OnShowTrash(object sender, RoutedEventArgs e)
    {
        _showingTrash = true;
        SearchBox.Clear();
        if (FolderTree.SelectedItem is NoteFolderViewModel selected)
        {
            _suppressFolderEvent = true;
            selected.IsSelected = false;
            _suppressFolderEvent = false;
        }
        RefreshList();
    }

    // ---------- 목록 ----------

    private void RefreshList()
    {
        var query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<NoteListItemViewModel> items;
        if (_showingTrash)
        {
            ListTitle.Text = "🗑 휴지통";
            items = [.. _store.GetTrash().Select(t => new NoteListItemViewModel(t))];
            ListEmpty.Text = "휴지통이 비어 있습니다.\n지운 메모는 30일 동안 여기 있습니다.";
        }
        else if (query.Length > 0)
        {
            ListTitle.Text = $"'{query}' 검색";
            items = [.. _store.Search(query, _folder).Select(r => new NoteListItemViewModel(r.Note, r.Snippet))];
            ListEmpty.Text = "찾는 메모가 없습니다.";
        }
        else
        {
            ListTitle.Text = FolderName(_folder);
            // 폴더를 고르면 하위 폴더의 메모도 함께 보인다 (각 메모 제목 위에 폴더 위치).
            items = [.. _store.GetNotes(_folder, includeSubfolders: true).Select(n => new NoteListItemViewModel(n))];
            ListEmpty.Text = "메모가 없습니다.\n＋ 로 새 메모를 만드세요.";
        }

        var selectedPath = _active?.Path;
        _syncingSelection = true;
        NoteList.ItemsSource = items;
        NoteList.SelectedItem = items.FirstOrDefault(i => i.RelativePath == selectedPath);
        _syncingSelection = false;
        ListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnNoteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && NoteList.SelectedItem is NoteListItemViewModel { IsTrashed: false } item)
        {
            OpenTab(item.RelativePath, activate: true);
        }
    }

    private void OnNoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NoteList.SelectedItem is NoteListItemViewModel { IsTrashed: true } trashed)
        {
            RestoreFromTrash(trashed);
        }
    }

    private void OnNoteMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (NoteList.SelectedItem is not NoteListItemViewModel item || NoteList.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        if (item.IsTrashed)
        {
            menu.Items.Add(MenuItem("↩ 복원", () => RestoreFromTrash(item)));
            return;
        }

        menu.Items.Add(MenuItem(item.IsPinned ? "고정 해제" : "📌 맨 위에 고정", () => _store.SetPinned(item.RelativePath, !item.IsPinned)));
        menu.Items.Add(MenuItem("이름 바꾸기", () => RenameNote(item.RelativePath)));
        var move = new MenuItem { Header = "폴더로 이동" };
        foreach (var folder in ((IEnumerable<NoteFolderViewModel>)FolderTree.ItemsSource).SelectMany(f => f.SelfAndDescendants()))
        {
            var target = folder.RelativePath;
            move.Items.Add(MenuItem((target.Length == 0 ? "메모 (최상위)" : target), () => MoveNote(item.RelativePath, target)));
        }
        menu.Items.Add(move);
        if (item.Extension == ".txt")
        {
            menu.Items.Add(MenuItem("마크다운(.md)으로 바꾸기 — 서식 쓰기", () => ConvertToMarkdown(item.RelativePath)));
        }
        menu.Items.Add(MenuItem("파일 위치 열기", () =>
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_store.FullPath(item.RelativePath)}\"")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("삭제 (휴지통으로)", () => DeleteNote(item.RelativePath)));
    }

    // 메모를 끌어서 폴더에 놓으면 이동
    private void OnNoteListMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(NoteList);

    private void OnNoteListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || NoteList.SelectedItem is not NoteListItemViewModel { IsTrashed: false } item)
        {
            return;
        }
        var delta = e.GetPosition(NoteList) - _dragStart;
        if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(NoteList, new DataObject("DayDeck.NotePath", item.RelativePath), DragDropEffects.Move);
        }
    }

    // 폴더를 끌어서 다른 폴더에 놓으면 그 안으로 이동
    private const string FolderDragFormat = "DayDeck.FolderPath";

    private void OnFolderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _folderDragStart = e.GetPosition(FolderTree);
        _folderDragSource = (e.OriginalSource as FrameworkElement)?.DataContext as NoteFolderViewModel;
    }

    private void OnFolderMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _folderDragSource is not { IsRoot: false } folder)
        {
            return;
        }
        var delta = e.GetPosition(FolderTree) - _folderDragStart;
        if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _folderDragSource = null;
            DragDrop.DoDragDrop(FolderTree, new DataObject(FolderDragFormat, folder.RelativePath), DragDropEffects.Move);
        }
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as NoteFolderViewModel;
        e.Effects = e.Data.GetDataPresent("DayDeck.NotePath")
                    || (e.Data.GetData(FolderDragFormat) is string folder && target is not null && CanMoveFolder(folder, target.RelativePath))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not NoteFolderViewModel target)
        {
            return;
        }
        if (e.Data.GetData("DayDeck.NotePath") is string path)
        {
            MoveNote(path, target.RelativePath);
        }
        else if (e.Data.GetData(FolderDragFormat) is string folder && CanMoveFolder(folder, target.RelativePath))
        {
            MoveFolder(folder, target.RelativePath);
        }
    }

    /// <summary>자기 자신·하위 폴더나 지금 있는 폴더에는 놓을 수 없다.</summary>
    private static bool CanMoveFolder(string folder, string parent) =>
        folder.Length > 0
        && !string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase)
        && !parent.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(parent, folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "", StringComparison.OrdinalIgnoreCase);

    private void MoveFolder(string folder, string parent)
    {
        try
        {
            FlushTabsUnder(folder);
            var moved = _store.MoveFolder(folder, parent);
            RetargetTabs(folder, moved, isFolder: true);
            foreach (var path in _expanded.Where(p => p == folder || p.StartsWith(folder + "/", StringComparison.Ordinal)).ToList())
            {
                _expanded.Remove(path);
                _expanded.Add(moved + path[folder.Length..]);
            }
            _expanded.Add(parent); // 옮긴 폴더가 보이도록
            if (_folder == folder || _folder.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                _folder = moved + _folder[folder.Length..];
            }
            RefreshAll();
            SaveSession();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "폴더 이동", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---------- 메모 동작 ----------

    private void OnNewNote(object sender, RoutedEventArgs e) => NewNote();

    /// <summary>메모가 있는 폴더로 가서 연다 (전체 검색 결과).</summary>
    public void OpenNote(string path)
    {
        if (!_store.Exists(path))
        {
            return;
        }
        var slash = path.LastIndexOf('/');
        _folder = slash < 0 ? "" : path[..slash];
        _showingTrash = false;
        foreach (var part in Ancestors(_folder))
        {
            _expanded.Add(part);
        }
        SearchBox.Clear();
        RefreshAll();
        OpenTab(path, activate: true);
    }

    /// <summary>현재 폴더에 새 메모를 만들고 연다. 첫 저장 때 첫 줄로 이름이 바뀐다.</summary>
    public void NewNote()
    {
        if (_showingTrash)
        {
            _showingTrash = false;
        }
        var path = _store.Create(_folder, NoteTabViewModel.NewNoteTitle, _app.Settings.Current.Notes.DefaultExtension);
        RefreshList();
        OpenTab(path, activate: true, isNew: true);
    }

    private void RenameNote(string path)
    {
        if (InputDialog.Ask(Window.GetWindow(this), "메모 이름 바꾸기", "새 이름", Path.GetFileNameWithoutExtension(path)) is not { } name)
        {
            return;
        }
        FlushTab(path);
        var renamed = _store.Rename(path, name);
        RetargetTabs(path, renamed, isFolder: false);
        RefreshList();
    }

    private void MoveNote(string path, string folder)
    {
        FlushTab(path);
        var moved = _store.Move(path, folder);
        RetargetTabs(path, moved, isFolder: false);
        RefreshAll();
    }

    private void DeleteNote(string path)
    {
        FlushTab(path);
        CloseTabsUnder(path, exact: true);
        var trashPath = _store.Delete(path);
        (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{Path.GetFileNameWithoutExtension(path)}' 메모를 휴지통으로 옮김", () => _store.Restore(trashPath));
        RefreshAll();
    }

    private void RestoreFromTrash(NoteListItemViewModel item)
    {
        var restored = _store.Restore(item.TrashPath!);
        RefreshAll();
        (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{Path.GetFileNameWithoutExtension(restored)}' 복원됨 ({restored})", () => { });
    }

    // ---------- 탭 ----------

    private void OpenTab(string path, bool activate, int caret = 0, bool isNew = false)
    {
        var tab = _tabs.FirstOrDefault(t => t.Path == path);
        if (tab is null)
        {
            try
            {
                tab = new NoteTabViewModel(_store, _app.AutoSave, path, isNew) { CaretIndex = caret };
            }
            catch (IOException ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"메모를 열 수 없습니다.\n{ex.Message}", "메모", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            tab.PathChanged += (_, _) =>
            {
                RefreshList();
                SaveSession();
            };
            tab.PropertyChanged += (s, args) =>
            {
                if (s == _active && args.PropertyName is nameof(NoteTabViewModel.SaveStatus) or nameof(NoteTabViewModel.HasConflict))
                {
                    UpdateEditorChrome();
                }
                if (s == _active && args.PropertyName == nameof(NoteTabViewModel.Content) && PreviewHost.Visibility == Visibility.Visible)
                {
                    _previewTimer.Stop();
                    _previewTimer.Start();
                }
                // 원문이 바뀌었으면(원문 편집, 다시 읽기, 기록 되돌리기) 보기 편집기도 다시 읽는다. 보기에서 고친 것이면 같으므로 그대로.
                // 숨어 있으면 보일 때 다시 읽는다 (ShowActiveEditor).
                if (s is NoteTabViewModel changed && args.PropertyName == nameof(NoteTabViewModel.Content)
                    && _rich.TryGetValue(changed, out var rich) && rich.IsVisible && rich.Markdown != changed.Content)
                {
                    rich.Load(changed.Content);
                }
                if (s is NoteTabViewModel changedLive && args.PropertyName == nameof(NoteTabViewModel.Content)
                    && _live.TryGetValue(changedLive, out var live) && live.IsVisible && live.Markdown != changedLive.Content)
                {
                    ReloadLive(live, changedLive.Content);
                }
            };
            _tabs.Add(tab);
            _app.AutoSave.Register(tab);
            _editors[tab] = CreateEditor(tab);
        }
        if (activate)
        {
            Activate(tab);
        }
        SaveSession();
    }

    private TextBox CreateEditor(NoteTabViewModel tab)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = _wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = _wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            FontFamily = Services.FontService.EditorFont(_app.Settings.Current.Notes),
            FontSize = _app.Settings.Current.Notes.EditorFontSize,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(24, 16, 24, 16),
            Visibility = Visibility.Collapsed,
            DataContext = tab,
            UndoLimit = 200,
            // 마크다운 메모만 서식 단축키(Ctrl+B 등)를 받는다.
            Tag = IsMarkdown(tab) ? MarkdownTag : null,
        };
        editor.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding(nameof(NoteTabViewModel.Content))
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });
        System.Windows.Automation.AutomationProperties.SetName(editor, "메모 편집기");
        editor.AutomationId("NoteEditor");
        editor.SelectionChanged += (_, _) =>
        {
            if (editor.IsVisible)
            {
                UpdateCaretStatus(editor.AsTarget());
            }
        };
        editor.LostKeyboardFocus += (_, _) => tab.Save();
        // 그림: Ctrl+V(클립보드 그림), 그림 파일 끌어다 놓기. 표: 엑셀 등에서 복사한 표는 마크다운 표로. 마크다운 메모만.
        editor.PreviewKeyDown += (_, e) =>
        {
            var paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                        || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
            if (paste && editor.Tag is MarkdownTag && (TryPasteTable(editor.AsTarget()) || TryPasteImage(tab, editor.AsTarget())))
            {
                e.Handled = true;
            }
        };
        editor.PreviewDragOver += (_, e) =>
        {
            if (editor.Tag is MarkdownTag && DroppedImages(e.Data).Count > 0)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        editor.PreviewDrop += (_, e) =>
        {
            if (editor.Tag is MarkdownTag && DroppedImages(e.Data) is { Count: > 0 } files)
            {
                var index = editor.GetCharacterIndexFromPoint(e.GetPosition(editor), snapToText: true);
                editor.Select(index >= 0 ? index : editor.Text.Length, 0);
                InsertImageFiles(tab, editor.AsTarget(), files);
                e.Handled = true;
            }
        };
        editor.Loaded += (_, _) => editor.CaretIndex = Math.Min(tab.CaretIndex, editor.Text.Length);
        EditorHost.Children.Add(editor);
        return editor;
    }

    /// <summary>탭의 커서 위치: 옵시디언 편집기가 보이면 그 커서, 아니면 원문 입력란의 커서.</summary>
    private int CaretOf(NoteTabViewModel tab, TextBox box) =>
        _live.TryGetValue(tab, out var live) && live.Visibility == Visibility.Visible ? live.CaretOffset : box.CaretIndex;

    private void Activate(NoteTabViewModel tab)
    {
        if (_active is not null && _editors.TryGetValue(_active, out var previous))
        {
            _active.CaretIndex = CaretOf(_active, previous);
            previous.Visibility = Visibility.Collapsed;
        }
        _active = tab;
        var editor = _editors[tab];
        editor.CaretIndex = Math.Min(tab.CaretIndex, editor.Text.Length);
        UpdateEditorChrome(); // 보기 방식에 맞는 편집기를 보인다.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_active != tab)
            {
                return;
            }
            if (ActiveRich is { } rich)
            {
                rich.Focus();
            }
            else if (ActiveLive is { } live)
            {
                ((IMarkdownTarget)live).Focus();
                live.ScrollToOffset(live.CaretOffset);
            }
            else
            {
                editor.Focus();
                editor.ScrollToLine(editor.GetLineIndexFromCharacterIndex(editor.CaretIndex) is var line and >= 0 ? line : 0);
            }
        });
        _syncingSelection = true;
        NoteList.SelectedItem = (NoteList.ItemsSource as IEnumerable<NoteListItemViewModel>)?.FirstOrDefault(i => i.RelativePath == tab.Path);
        _syncingSelection = false;
        SaveSession();
    }

    private void CloseTab(NoteTabViewModel tab)
    {
        if (_rich.Remove(tab, out var rich))
        {
            rich.Flush();
            EditorHost.Children.Remove(rich);
        }
        if (_live.Remove(tab, out var live))
        {
            EditorHost.Children.Remove(live);
        }
        _app.AutoSave.Unregister(tab); // 저장 후 해제
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_editors.Remove(tab, out var editor))
        {
            EditorHost.Children.Remove(editor);
        }
        if (_active == tab)
        {
            _active = null;
            if (_tabs.Count > 0)
            {
                Activate(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
            }
        }
        UpdateEditorChrome();
        SaveSession();
    }

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NoteTabViewModel tab })
        {
            Activate(tab);
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NoteTabViewModel tab })
        {
            CloseTab(tab);
        }
        e.Handled = true;
    }

    private void FlushTab(string path) => _tabs.FirstOrDefault(t => t.Path == path)?.Save();

    private void FlushTabsUnder(string folder)
    {
        foreach (var tab in _tabs.Where(t => t.Path.StartsWith(folder + "/", StringComparison.Ordinal)))
        {
            tab.Save();
        }
    }

    private void CloseTabsUnder(string path, bool exact = false)
    {
        foreach (var tab in _tabs.Where(t => exact ? t.Path == path : t.Path.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            CloseTab(tab);
        }
    }

    private void RetargetTabs(string from, string to, bool isFolder)
    {
        foreach (var tab in _tabs)
        {
            if (!isFolder && tab.Path == from)
            {
                tab.Retarget(to);
            }
            else if (isFolder && tab.Path.StartsWith(from + "/", StringComparison.Ordinal))
            {
                tab.Retarget(to + tab.Path[from.Length..]);
            }
        }
        UpdateEditorChrome();
        SaveSession();
    }

    private void UpdateEditorChrome()
    {
        var hasTab = _active is not null;
        Toolbar.Visibility = EditorStatus.Visibility = hasTab ? Visibility.Visible : Visibility.Collapsed;
        EditorEmpty.Visibility = hasTab ? Visibility.Collapsed : Visibility.Visible;
        if (!hasTab)
        {
            FindBar.Visibility = ConflictBar.Visibility = FormatBar.Visibility = PlainTextHint.Visibility = Visibility.Collapsed;
            ApplyViewMode();
            return;
        }

        var markdown = IsMarkdown(_active!);
        FormatBar.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        PlainTextHint.Visibility = markdown ? Visibility.Collapsed : Visibility.Visible;
        ApplyViewMode();
        BreadcrumbText.Text = _active!.Breadcrumb;
        SaveStatusText.Text = _active.SaveStatus;
        ConflictBar.Visibility = _active.HasConflict ? Visibility.Visible : Visibility.Collapsed;
        foreach (var container in TabStrip.Items.Cast<NoteTabViewModel>()
                     .Select(t => (Tab: t, Element: TabStrip.ItemContainerGenerator.ContainerFromItem(t) as ContentPresenter)))
        {
            if (container.Element is not null && VisualTreeHelper.GetChildrenCount(container.Element) > 0
                && VisualTreeHelper.GetChild(container.Element, 0) is Border border)
            {
                border.Background = container.Tab == _active ? Services.ThemeService.Brush("CardBrush") : Brushes.Transparent;
            }
        }
        if (ActiveRich is { } rich)
        {
            EditorStatusText.Text = $"{rich.TextLength:N0}자 · 보기에서 바로 고침 (원문은 \"편집\") · {_store.FullPath(_active.Path)}";
        }
        else
        {
            UpdateCaretStatus(ActiveRaw!);
        }
    }

    private void UpdateCaretStatus(IMarkdownTarget editor)
    {
        var text = editor.Text;
        var caret = Math.Clamp(editor.CaretOffset, 0, text.Length);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var line = text.AsSpan(0, lineStart).Count('\n');
        var mode = editor is LivePreviewEditor ? "옵시디언 모드 (커서 줄은 원문, 링크는 Ctrl+클릭) · " : "";
        EditorStatusText.Text = $"{line + 1}줄 {caret - lineStart + 1}칸 · {text.Length:N0}자 · {mode}UTF-8 · {_store.FullPath(_active!.Path)}";
    }

    // ---------- 편집 도구 ----------

    private void OnWrapToggled(object sender, RoutedEventArgs e)
    {
        _wordWrap = WrapToggle.IsChecked == true;
        foreach (var editor in _editors.Values)
        {
            editor.TextWrapping = _wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            editor.HorizontalScrollBarVisibility = _wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }
        SaveSession();
    }

    private void OnShowFind(object sender, RoutedEventArgs e) => ShowFind(replace: false);

    private void OnShowReplace(object sender, RoutedEventArgs e) => ShowFind(replace: true);

    private void ShowFind(bool replace)
    {
        if (_active is null)
        {
            return;
        }
        FindBar.Visibility = Visibility.Visible;
        ReplacePanel.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        var selected = ActiveRich is { } rich ? rich.Selection.Text : ActiveRaw!.SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n') && !selected.Contains('\r'))
        {
            FindBox.Text = selected;
        }
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        if (ActiveRich is { } rich)
        {
            rich.Focus();
        }
        else if (_active is not null)
        {
            ActiveRaw!.Focus();
        }
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnCloseFind(sender, e);
            e.Handled = true;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext();

    private bool FindNext()
    {
        if (_active is null || FindBox.Text.Length == 0)
        {
            return false;
        }
        if (ActiveRich is { } rich)
        {
            var found = rich.FindNext(FindBox.Text);
            FindStatus.Text = found ? $"{rich.CountMatches(FindBox.Text)}개 중" : "찾지 못함";
            return found;
        }
        var editor = ActiveRaw!;
        var start = editor.SelectionStart + editor.SelectionLength;
        var index = editor.Text.IndexOf(FindBox.Text, start, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            index = editor.Text.IndexOf(FindBox.Text, 0, StringComparison.CurrentCultureIgnoreCase);
        }
        if (index < 0)
        {
            FindStatus.Text = "찾지 못함";
            return false;
        }
        editor.Select(index, FindBox.Text.Length);
        editor.ScrollToOffset(index);
        FindStatus.Text = $"{CountOccurrences(editor.Text, FindBox.Text)}개 중";
        return true;
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (_active is null)
        {
            return;
        }
        if (ActiveRich is { } rich)
        {
            rich.ReplaceSelection(FindBox.Text, ReplaceBox.Text);
            return;
        }
        var editor = ActiveRaw!;
        if (string.Equals(editor.SelectedText, FindBox.Text, StringComparison.CurrentCultureIgnoreCase) && editor.SelectionLength > 0)
        {
            editor.SelectedText = ReplaceBox.Text;
        }
        FindNext();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (_active is null || FindBox.Text.Length == 0)
        {
            return;
        }
        if (ActiveRich is { } rich)
        {
            var replaced = rich.ReplaceAll(FindBox.Text, ReplaceBox.Text);
            FindStatus.Text = replaced == 0 ? "찾지 못함" : $"{replaced}개 바꿈";
            return;
        }
        var editor = ActiveRaw!;
        var count = CountOccurrences(editor.Text, FindBox.Text);
        if (count == 0)
        {
            FindStatus.Text = "찾지 못함";
            return;
        }
        // 한 번에 바꿔서 실행 취소 한 번으로 되돌릴 수 있게 한다.
        editor.Select(0, editor.Text.Length);
        editor.SelectedText = ReplaceIgnoreCase(editor.Text, FindBox.Text, ReplaceBox.Text);
        FindStatus.Text = $"{count}개 바꿈";
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.CurrentCultureIgnoreCase))
        {
            count++;
        }
        return count;
    }

    private static string ReplaceIgnoreCase(string text, string value, string replacement) =>
        text.Replace(value, replacement, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>선택한 줄(없으면 커서가 있는 줄)을 오늘 할 일로 추가한다. 이미 체크된 줄은 건너뛴다.</summary>
    // ---------- 그림 ----------

    private void OnInsertImage(object sender, RoutedEventArgs e)
    {
        if (_active is null || !IsMarkdown(_active))
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "그림 넣기",
            Multiselect = true,
            Filter = "그림 (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            if (ActiveRich is { } rich)
            {
                rich.InsertImages(ImportImages(_active, dialog.FileNames));
            }
            else
            {
                InsertImageFiles(_active, ActiveRaw!, dialog.FileNames);
            }
        }
    }

    private static bool IsImageFile(string path) => NoteStore.ImageExtensions.Contains(Path.GetExtension(path));

    private static List<string> DroppedImages(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? [.. files.Where(f => IsImageFile(f) && File.Exists(f))]
            : [];

    /// <summary>그림 파일들을 메모 폴더의 .images로 복사하고 커서 자리에 넣는다.</summary>
    private void InsertImageFiles(NoteTabViewModel tab, IMarkdownTarget editor, IEnumerable<string> files) =>
        InsertImageLinks(editor, ImportImages(tab, files));

    /// <summary>그림 파일들을 메모 폴더의 .images로 복사하고, 메모에서 쓸 경로들을 돌려준다.</summary>
    private List<string> ImportImages(NoteTabViewModel tab, IEnumerable<string> files)
    {
        var links = new List<string>();
        foreach (var file in files.Where(IsImageFile))
        {
            try
            {
                links.Add(_store.ImportImage(tab.Path, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"그림을 복사할 수 없음: {file}", ex);
                MessageBox.Show(Window.GetWindow(this), $"그림을 넣지 못했습니다.\n{file}\n{ex.Message}", "메모", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        return links;
    }

    /// <summary>클립보드 그림을 PNG로 메모 폴더의 .images에 저장한다.</summary>
    private string? SaveClipboardImage(NoteTabViewModel tab, System.Windows.Media.Imaging.BitmapSource image)
    {
        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var png = new MemoryStream();
            encoder.Save(png);
            return _store.SaveImage(tab.Path, png.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn("그림 붙여 넣기 실패", ex);
            return null;
        }
    }

    private void InsertImageLinks(IMarkdownTarget editor, IReadOnlyList<string> links)
    {
        if (links.Count == 0)
        {
            return;
        }
        MarkdownCommands.Apply(editor, (t, s, l) => MarkdownEdit.InsertImages(t, s, l, links));
        if (PreviewHost.Visibility != Visibility.Visible && editor is not LivePreviewEditor)
        {
            SaveStatusText.Text = "🖼 그림을 넣었습니다 — \"나란히\"나 \"보기\"에서 보입니다";
        }
    }

    /// <summary>Ctrl+V: 엑셀·구글 시트·워드 표를 복사했으면 마크다운 표로 넣는다 (앞뒤 글과 빈 줄로 뗀다).</summary>
    private static bool TryPasteTable(IMarkdownTarget editor)
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } data || MarkdownTableView.FromClipboard(data) is not { } table)
            {
                return false;
            }
            MarkdownCommands.Apply(editor, (t, s, l) => MarkdownEdit.InsertBlock(t, s, l, string.Join("\n", table.Write())));
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("표 붙여 넣기 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// Ctrl+V: 클립보드에 글자 없이 그림만 있으면(화면 캡처 등) PNG로 저장해 넣는다. 그림 파일을 복사한 경우도 받는다.
    /// 글자가 함께 있으면(엑셀·워드에서 복사) 평소처럼 글자를 붙여 넣는다. 설정에서 끌 수 있다.
    /// </summary>
    private bool TryPasteImage(NoteTabViewModel tab, IMarkdownTarget editor)
    {
        if (!_app.Settings.Current.Notes.PasteImages)
        {
            return false;
        }
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(IsImageFile).ToList();
                if (files.Count == 0)
                {
                    return false;
                }
                InsertImageFiles(tab, editor, files);
                return true;
            }
            if (Clipboard.ContainsText() || !Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
            {
                return false;
            }
            if (SaveClipboardImage(tab, image) is { } link)
            {
                InsertImageLinks(editor, [link]);
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException)
        {
            // 클립보드를 다른 프로그램이 잡고 있거나 저장 실패: 평소 붙여 넣기로 넘긴다.
            Log.Warn("그림 붙여 넣기 실패", ex);
            return false;
        }
    }

    // ---------- 버전 기록 ----------

    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        if (_active is not { } tab)
        {
            return;
        }
        FlushRich(tab);
        tab.Save(); // 지금 고친 내용도 기록 비교 대상이 되도록 먼저 저장
        if (NoteHistoryWindow.Show(Window.GetWindow(this), _store, tab.Path))
        {
            tab.Reload();
            SaveStatusText.Text = "↺ 예전 버전으로 되돌림";
            RenderPreview();
        }
    }

    private void OnMakeSticky(object sender, RoutedEventArgs e)
    {
        if (_active is null)
        {
            return;
        }
        FlushRich(_active);
        // 같은 파일을 스티커에서 고치므로 탭의 변경을 먼저 저장한다.
        _active.Save();
        _app.OpenSticky(_active.Path);
    }

    private void OnMakeTasks(object sender, RoutedEventArgs e)
    {
        if (_active is null)
        {
            return;
        }
        var created = new List<long>();
        foreach (var text in SelectedLines())
        {
            if (NoteText.IsChecked(text) || NoteText.ExtractTaskTitle(text) is not { } title)
            {
                continue;
            }
            created.Add(_app.Tasks.Add(new TaskItem
            {
                Title = title,
                MyDay = DateOnly.FromDateTime(DateTime.Now),
                Notes = $"메모에서: {_active.Path}",
            }));
        }

        var window = Window.GetWindow(this) as MainWindow;
        if (created.Count == 0)
        {
            window?.ShowUndo("할 일로 만들 줄이 없습니다 (빈 줄이거나 이미 체크됨)", () => { });
            return;
        }
        window?.ShowUndo($"할 일 {created.Count}개를 오늘 할 일에 추가했습니다", () =>
        {
            foreach (var id in created)
            {
                _app.Tasks.Delete(id);
            }
        });
    }

    /// <summary>선택한 줄(없으면 커서가 있는 줄)의 마크다운 원문.</summary>
    private IEnumerable<string> SelectedLines()
    {
        if (ActiveRich is { } rich)
        {
            return rich.SelectedMarkdownLines().ToList();
        }
        var editor = ActiveRaw!;
        var text = editor.Text;
        var start = Math.Clamp(editor.SelectionStart, 0, text.Length);
        var end = Math.Clamp(editor.SelectionStart + Math.Max(0, editor.SelectionLength - 1), start, text.Length);
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', end);
        return text[first..(last < 0 ? text.Length : last)].Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }

    private void OnConflictReload(object sender, RoutedEventArgs e)
    {
        _active?.Reload();
        UpdateEditorChrome();
    }

    private void OnConflictKeep(object sender, RoutedEventArgs e)
    {
        _active?.KeepMine();
        UpdateEditorChrome();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox { Tag: MarkdownTag } focused && MarkdownCommands.HandleShortcut(focused, e))
        {
            return;
        }
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.N)
        {
            NewNote();
        }
        else if (ctrl && e.Key == Key.S && _active is not null)
        {
            FlushRich(_active);
            _active.Save();
            UpdateEditorChrome();
        }
        else if (ctrl && e.Key == Key.W && _active is not null)
        {
            CloseTab(_active);
        }
        else if (ctrl && e.Key == Key.F)
        {
            ShowFind(replace: false);
        }
        else if (ctrl && e.Key == Key.H)
        {
            ShowFind(replace: true);
        }
        else if (e.Key == Key.F3)
        {
            FindNext();
        }
        else if (ctrl && e.Key == Key.Tab && _tabs.Count > 1 && _active is not null)
        {
            Activate(_tabs[(_tabs.IndexOf(_active) + 1) % _tabs.Count]);
        }
        else
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
    }

    // ---------- 서식 (마크다운) ----------

    private const string MarkdownTag = MarkdownCommands.Tag;

    private static bool IsMarkdown(NoteTabViewModel tab) =>
        Path.GetExtension(tab.Path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>서식 도구: "보기"면 보기 편집기에, 아니면 원문 편집기에 넣는다 (마크다운 탭만).</summary>
    private void Format(Action<IMarkdownTarget> raw, Action<MarkdownRichEditor> rich)
    {
        if (_active is null || !IsMarkdown(_active))
        {
            return;
        }
        if (ActiveRich is { } editor)
        {
            rich(editor);
        }
        else if (ActiveRaw is { } target)
        {
            raw(target);
        }
    }

    private void OnFormatBold(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Bold, r => r.ToggleBold());

    private void OnFormatItalic(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Italic, r => r.ToggleItalic());

    private void OnFormatUnderline(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Underline, r => r.ToggleUnderline());

    private void OnFormatStrike(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Strike, r => r.ToggleStrike());

    private void OnFormatHighlight(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Highlight, r => r.ToggleHighlight());

    private void OnFormatCode(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Code, r => r.ToggleCode());

    private void OnFormatBullet(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.Line(editor, LineStyle.Bullet), r => r.ToggleLineKind(LineKind.Bullet));

    private void OnFormatNumbered(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.Line(editor, LineStyle.Numbered), r => r.ToggleLineKind(LineKind.Numbered));

    private void OnFormatTask(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.Line(editor, LineStyle.Task), r => r.ToggleLineKind(LineKind.Task));

    private void OnFormatQuote(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.Line(editor, LineStyle.Quote), r => r.ToggleLineKind(LineKind.Quote));

    private void OnFormatHeading(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.ShowHeadingMenu(editor, (UIElement)sender),
            r => MarkdownCommands.ShowHeadingMenu((UIElement)sender, style => r.SetHeading(style switch
            {
                LineStyle.Heading1 => 1,
                LineStyle.Heading2 => 2,
                LineStyle.Heading3 => 3,
                _ => 0,
            })));

    private void OnFormatColor(object sender, RoutedEventArgs e) =>
        Format(editor => MarkdownCommands.ShowColorMenu(editor, ColorButton, PickedColor),
            r => MarkdownCommands.ShowColorMenu(ColorButton, r.SetColor, PickedColor));

    private void PickedColor(string hex) => ColorSwatch.Background = TaskRowViewModel.TryParseBrush(hex);

    private void OnFormatLink(object sender, RoutedEventArgs e) => Format(MarkdownCommands.Link, MarkdownCommands.RichLink);

    // ---------- 보기 편집기 ----------

    /// <summary>지금 보이는 보기 편집기 ("보기" + 마크다운 탭일 때).</summary>
    private MarkdownRichEditor? ActiveRich =>
        _active is not null && _viewMode == "view" && IsMarkdown(_active) && _rich.TryGetValue(_active, out var rich) ? rich : null;

    /// <summary>지금 보이는 옵시디언 편집기 ("옵시디언" + 마크다운 탭일 때).</summary>
    private LivePreviewEditor? ActiveLive =>
        _active is not null && _viewMode == "live" && IsMarkdown(_active) && _live.TryGetValue(_active, out var live) ? live : null;

    /// <summary>원문을 고치는 지금 편집기: 옵시디언 편집기, 아니면 원문 입력란. 보기 편집기만 보일 때도 원문 입력란.</summary>
    private IMarkdownTarget? ActiveRaw =>
        ActiveLive is { } live ? live : _active is not null && _editors.TryGetValue(_active, out var box) ? box.AsTarget() : null;

    private LivePreviewEditor GetOrCreateLive(NoteTabViewModel tab)
    {
        if (_live.TryGetValue(tab, out var existing))
        {
            return existing;
        }
        var notes = _app.Settings.Current.Notes;
        var live = new LivePreviewEditor(link => _store.ResolveRelative(tab.Path, link))
        {
            Visibility = Visibility.Collapsed,
            FontFamily = Services.FontService.EditorFont(notes),
            FontSize = notes.EditorFontSize,
            OpenWikiLink = OpenWikiLink,
        };
        live.LoadMarkdown(tab.Content);
        live.MarkdownChanged += (_, _) => tab.Content = live.Markdown;
        live.LostKeyboardFocus += (_, _) =>
        {
            if (!live.IsKeyboardFocusWithin)
            {
                tab.Save();
            }
        };
        live.TextArea.Caret.PositionChanged += (_, _) =>
        {
            tab.CaretIndex = live.CaretOffset;
            if (live.IsVisible && tab == _active)
            {
                UpdateCaretStatus(live);
            }
        };
        // 그림: Ctrl+V(클립보드 그림), 그림 파일 끌어다 놓기. 표: 엑셀 등에서 복사한 표는 마크다운 표로.
        live.TextArea.PreviewKeyDown += (_, e) =>
        {
            var paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                        || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
            if (paste && (TryPasteTable(live) || TryPasteImage(tab, live)))
            {
                e.Handled = true;
            }
        };
        live.PreviewDragOver += (_, e) =>
        {
            if (DroppedImages(e.Data).Count > 0)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        live.PreviewDrop += (_, e) =>
        {
            if (DroppedImages(e.Data) is { Count: > 0 } files)
            {
                if (live.GetPositionFromPoint(e.GetPosition(live)) is { } position)
                {
                    live.Select(live.Document.GetOffset(position.Location), 0);
                }
                InsertImageFiles(tab, live, files);
                e.Handled = true;
            }
        };
        _live[tab] = live;
        EditorHost.Children.Add(live);
        return live;
    }

    /// <summary>원문이 밖에서 바뀌었을 때(다시 읽기, 기록 되돌리기) 옵시디언 편집기를 다시 읽는다. 커서 위치는 되도록 유지.</summary>
    private static void ReloadLive(LivePreviewEditor live, string content)
    {
        var caret = live.CaretOffset;
        live.LoadMarkdown(content);
        live.CaretOffset = Math.Min(caret, live.Document.TextLength);
        live.RefreshImages();
    }

    /// <summary>[[메모 이름]]을 Ctrl+클릭: 같은 이름의 메모를 연다.</summary>
    private bool OpenWikiLink(string target)
    {
        var name = target.Split('#')[0].Trim();
        var match = _store.GetNotes("", includeSubfolders: true)
            .FirstOrDefault(n => string.Equals(Path.GetFileNameWithoutExtension(n.RelativePath), name, StringComparison.CurrentCultureIgnoreCase)
                                 || string.Equals(n.RelativePath, name, StringComparison.CurrentCultureIgnoreCase));
        if (match is null)
        {
            SaveStatusText.Text = $"'{name}' 메모를 찾지 못했습니다";
            return false;
        }
        OpenNote(match.RelativePath);
        return true;
    }

    private void FlushRich(NoteTabViewModel tab)
    {
        if (_rich.TryGetValue(tab, out var rich))
        {
            rich.Flush();
        }
    }

    private MarkdownRichEditor GetOrCreateRich(NoteTabViewModel tab)
    {
        if (_rich.TryGetValue(tab, out var existing))
        {
            return existing;
        }
        var rich = new MarkdownRichEditor(link => _store.ResolveRelative(tab.Path, link))
        {
            Visibility = Visibility.Collapsed,
            ImportImageFiles = files => ImportImages(tab, files),
            SavePastedImage = image => SaveClipboardImage(tab, image),
            PasteImagesEnabled = () => _app.Settings.Current.Notes.PasteImages,
        };
        rich.Load(tab.Content);
        rich.MarkdownChanged += (_, _) => tab.Content = rich.Markdown;
        rich.LostKeyboardFocus += (_, _) =>
        {
            rich.Flush();
            tab.Save();
        };
        rich.SelectionChanged += (_, _) =>
        {
            if (rich.IsVisible && tab == _active)
            {
                EditorStatusText.Text = $"{rich.TextLength:N0}자 · 보기에서 바로 고침 (원문은 \"편집\") · {_store.FullPath(tab.Path)}";
            }
        };
        _rich[tab] = rich;
        EditorHost.Children.Add(rich);
        return rich;
    }

    /// <summary>지금 탭과 보기 방식에 맞는 편집기 하나만 보인다.</summary>
    private void ShowActiveEditor()
    {
        var useRich = _active is not null && _viewMode == "view" && IsMarkdown(_active);
        var useLive = _active is not null && _viewMode == "live" && IsMarkdown(_active);
        var rich = useRich ? GetOrCreateRich(_active!) : null;
        var live = useLive ? GetOrCreateLive(_active!) : null;
        foreach (var (tab, editor) in _editors)
        {
            editor.Visibility = tab == _active && !useRich && !useLive ? Visibility.Visible : Visibility.Collapsed;
        }
        var liveWasShown = live?.Visibility == Visibility.Visible;
        foreach (var other in _live.Values)
        {
            other.Visibility = other == live ? Visibility.Visible : Visibility.Collapsed;
        }
        if (live is not null && live.Markdown != _active!.Content)
        {
            ReloadLive(live, _active.Content);
        }
        // 탭·보기 방식을 바꿔 새로 보일 때만 커서를 맞춘다. 입력 중(메모 이름이 바뀌어 다시 불릴 때 등)에는 그대로 둔다.
        if (live is not null && !liveWasShown)
        {
            live.CaretOffset = Math.Min(_active!.CaretIndex, live.Document.TextLength);
        }
        foreach (var (tab, other) in _rich)
        {
            if (other != rich && other.Visibility == Visibility.Visible)
            {
                other.Flush();
            }
            other.Visibility = other == rich ? Visibility.Visible : Visibility.Collapsed;
        }
        if (rich is not null && rich.Markdown != _active!.Content)
        {
            rich.Load(_active.Content);
        }
    }

    private void ConvertToMarkdown(string path)
    {
        FlushTab(path);
        var reopen = _tabs.Any(t => t.Path == path);
        CloseTabsUnder(path, exact: true);
        var converted = _store.ChangeExtension(path, ".md");
        if (reopen)
        {
            OpenTab(converted, activate: true);
        }
    }

    // ---------- 미리보기 ----------

    private void OnViewModeChanged(object sender, RoutedEventArgs e)
    {
        var mode = sender == SplitModeButton ? "split" : sender == PreviewModeButton ? "view" : sender == LiveModeButton ? "live" : "edit";
        if (mode == _viewMode)
        {
            return;
        }
        // 보기에서 고친 것을 원문에 반영한 뒤 바꾼다.
        if (_active is not null)
        {
            FlushRich(_active);
        }
        _viewMode = mode;
        ApplyViewMode();
        if (_active is not null)
        {
            if (ActiveRich is { } rich)
            {
                rich.Focus();
            }
            else
            {
                ActiveRaw?.Focus();
            }
        }
    }

    private (string Mode, NoteTabViewModel? Tab, string? Path) _appliedView;

    private void ApplyViewMode()
    {
        var markdown = _active is not null && IsMarkdown(_active);
        // 보기: 보기 편집기 하나. 옵시디언: 옵시디언 편집기 하나. 나란히: 원문 + 미리보기. 편집: 원문만.
        var mode = markdown ? _viewMode : "edit";
        ShowActiveEditor();
        // 저장 상태가 바뀔 때마다 불리므로, 탭·보기 방식이 그대로면 다시 그리지 않는다 (내용 변경은 타이머가 그린다).
        if (_appliedView == (mode, _active, _active?.Path))
        {
            return;
        }
        _appliedView = (mode, _active, _active?.Path);
        EditorColumn.Width = new GridLength(1, GridUnitType.Star);
        // 나란히: 끌어서 정한 미리보기 너비가 있으면 그 너비, 없으면 반반.
        PreviewColumn.Width = mode == "split"
            ? _app.Layout.GetWidth("notes.preview") is { } previewWidth ? new GridLength(previewWidth) : new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        PreviewSplitter.Visibility = mode == "split" ? Visibility.Visible : Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Visible;
        PreviewHost.Visibility = mode == "split" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "split")
        {
            RenderPreview();
        }
        else
        {
            Preview.Document = null; // 안 보이는 문서는 메모리에 두지 않는다.
        }
    }

    private void RenderPreview()
    {
        if (_active is null || PreviewHost.Visibility != Visibility.Visible)
        {
            return;
        }
        // 다시 그려도 읽던 위치를 유지한다.
        var scroller = FindScrollViewer(Preview);
        var offset = scroller?.VerticalOffset ?? 0;
        var path = _active.Path;
        Preview.Document = MarkdownRenderer.Render(_active.Content, ToggleTaskLine, resolveImage: link => _store.ResolveRelative(path, link));
        if (scroller is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToVerticalOffset(offset));
        }
    }

    /// <summary>미리보기에서 체크 상자를 누르면 원문 "- [ ]"를 "- [x]"로 (실행 취소 가능).</summary>
    private void ToggleTaskLine(int line, bool done)
    {
        if (_active is not null && _editors.TryGetValue(_active, out var editor))
        {
            MarkdownCommands.ToggleTaskLine(editor, line, done);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    // ---------- 도움 ----------

    private static string FolderName(string folder) => folder.Length == 0 ? "메모" : folder[(folder.LastIndexOf('/') + 1)..];

    private static IEnumerable<string> Ancestors(string folder)
    {
        yield return "";
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            yield return string.Join('/', parts[..i]);
        }
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}

internal static class AutomationExtensions
{
    public static void AutomationId(this FrameworkElement element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
