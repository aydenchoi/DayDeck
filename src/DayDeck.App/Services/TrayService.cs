using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using DayDeck.App.Views;

namespace DayDeck.App.Services;

/// <summary>시스템 트레이 아이콘과 메뉴. 앱이 살아 있는 동안 항상 존재한다.</summary>
internal sealed class TrayService : IDisposable
{
    private static readonly ImageSource NormalIcon = LoadIcon("app.ico");
    private static readonly ImageSource AlertIcon = LoadIcon("app-alert.ico");

    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseMenu;
    private readonly MenuItem _resumeItem;
    private MenuItem _widgetItem = null!;
    private bool _showAlert;

    public TrayService(App app)
    {
        _pauseMenu = new MenuItem { Header = "🔕 알림 일시 중지" };
        _resumeItem = Item("🔔 알림 다시 시작", () => app.PauseReminders(null));
        _icon = new TaskbarIcon
        {
            ToolTipText = "DayDeck",
            IconSource = NormalIcon,
            NoLeftClickDelay = true,
            ContextMenu = BuildMenu(app),
        };
        _icon.TrayLeftMouseUp += (_, _) => app.OnTrayClick();
        _icon.TrayMouseDoubleClick += (_, _) => app.ShowMainWindow();

        // 효율 모드(EcoQoS)는 알림 타이밍을 늦출 수 있어 쓰지 않는다.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public string ToolTipText
    {
        get => _icon.ToolTipText;
        set => _icon.ToolTipText = value;
    }

    /// <summary>기한 지난 할 일이나 확인하지 않은 알림이 있으면 빨간 점 아이콘.</summary>
    public bool ShowAlert
    {
        get => _showAlert;
        set
        {
            if (_showAlert != value)
            {
                _showAlert = value;
                _icon.IconSource = value ? AlertIcon : NormalIcon;
            }
        }
    }

    public bool IsPaused
    {
        set
        {
            _pauseMenu.Visibility = value ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            _resumeItem.Visibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
    }

    private ContextMenu BuildMenu(App app)
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("＋ 빠른 추가", app.ShowQuickAdd, app.Settings.Current.Hotkeys.QuickAdd));
        menu.Items.Add(Item("📝 새 메모", app.ShowNewNote, app.Settings.Current.Hotkeys.NewNote));
        menu.Items.Add(Item("📌 새 스티커 메모", app.NewSticky));
        _widgetItem = Item("🖥 바탕화면 위젯", app.ToggleWidget);
        _widgetItem.IsCheckable = false;
        menu.Items.Add(_widgetItem);
        // 오늘 할 일 (메뉴를 열 때마다 다시 채운다). 누르면 완료.
        var todayAnchor = new Separator();
        menu.Items.Add(todayAnchor);
        menu.Opened += (_, _) =>
        {
            RebuildToday(app, menu, todayAnchor);
            // 메뉴를 열 때 현재 상태를 반영한다.
            _widgetItem.IsChecked = app.Settings.Current.Widget.Enabled;
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("☀ 오늘의 할 일 보기", app.ShowBriefingNow));

        foreach (var (label, until) in new (string, Func<DateTime>)[]
                 {
                     ("30분", () => DateTime.Now.AddMinutes(30)),
                     ("1시간", () => DateTime.Now.AddHours(1)),
                     ("3시간", () => DateTime.Now.AddHours(3)),
                     ("내일 아침까지", () => DateOnly.FromDateTime(DateTime.Now).AddDays(1)
                         .ToDateTime(app.Settings.Current.Reminders.GetDailyBriefingTime())),
                 })
        {
            _pauseMenu.Items.Add(Item(label, () => app.PauseReminders(until())));
        }
        menu.Items.Add(_pauseMenu);
        _resumeItem.Visibility = System.Windows.Visibility.Collapsed;
        menu.Items.Add(_resumeItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("열기", () => app.ShowMainWindow(), app.Settings.Current.Hotkeys.OpenMain));
        menu.Items.Add(Item("설정", () => app.ShowMainWindow(MainPage.Settings)));
        if (!UpdateService.ManagedByStore)
        {
            menu.Items.Add(Item("업데이트 확인", app.CheckForUpdatesFromTray));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("종료", app.ExitApplication));
        return menu;
    }

    private readonly List<object> _todayItems = [];

    private void RebuildToday(App app, ContextMenu menu, Separator anchor)
    {
        foreach (var item in _todayItems)
        {
            menu.Items.Remove(item);
        }
        _todayItems.Clear();
        if (app.Tasks is null)
        {
            return;
        }

        var now = DateTime.Now;
        var open = Core.Todo.TaskRules.OrderOpen(app.Tasks.GetToday().Where(t => t.Status == Core.Todo.ItemStatus.Open), now).ToList();
        var index = menu.Items.IndexOf(anchor) + 1;
        void Add(object item)
        {
            menu.Items.Insert(index++, item);
            _todayItems.Add(item);
        }

        Add(new MenuItem { Header = open.Count == 0 ? "오늘 할 일을 모두 끝냈습니다 ✓" : $"오늘 (미완료 {open.Count})", IsEnabled = false });
        foreach (var task in open.Take(8))
        {
            var overdue = Core.Todo.TaskRules.IsOverdue(task, now);
            var label = "☐ " + (task.DueTime is { } t ? Core.Todo.TaskFormatting.FormatTime(t) + " " : "") + task.Title + (overdue ? "  ⚠" : "");
            var id = task.Id;
            var item = Item(label.Length > 48 ? label[..47] + "…" : label, () => app.Tasks.SetCompleted(id, true));
            item.ToolTip = "누르면 완료";
            Add(item);
        }
        if (open.Count > 8)
        {
            Add(Item($"… {open.Count - 8}개 더 보기", () => app.ShowMainWindow(MainPage.Today)));
        }
    }

    private static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        return item;
    }

    private static ImageSource LoadIcon(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/{name}"));
        image.Freeze();
        return image;
    }

    /// <summary>트레이 풍선 알림 (업데이트 결과 등).</summary>
    public void Notify(string title, string message) => _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();
}
