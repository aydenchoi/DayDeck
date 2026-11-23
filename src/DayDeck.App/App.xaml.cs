using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using DayDeck.App.Services;
using DayDeck.App.Views;
using DayDeck.Core;
using DayDeck.Core.AutoSave;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Drawings;
using DayDeck.Core.Notes;
using DayDeck.Core.QuickAdd;
using DayDeck.Core.Search;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.App;

/// <summary>
/// 상주 코어(트레이, 설정, DB)를 소유한다. 메인 창은 필요할 때만 만들고 닫으면 해제한다.
/// </summary>
public partial class App : Application
{
    private StartupOptions _options;
    private readonly SingleInstance _instance;
    private TrayService? _tray;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _minuteTimer;
    private ReminderPresenter? _reminderPresenter;
    private HotkeyService? _hotkeys;
    private QuickAddWindow? _quickAddWindow;
    private string _registeredHotkeys = "";
    private bool _ready;
    private bool _showRequested;
    private MainPage? _requestedPage;
    private bool _exiting;

    internal App(StartupOptions options, SingleInstance instance)
    {
        _options = options;
        _instance = instance;
    }

    public static new App Current => (App)Application.Current;

    public AppPaths Paths { get; private set; } = null!;
    public SettingsStore Settings { get; private set; } = null!;
    public Database Database { get; private set; } = null!;
    public TaskRepository Tasks { get; private set; } = null!;
    public EventRepository Events { get; private set; } = null!;
    public AppStateStore AppState { get; private set; } = null!;

    /// <summary>끌어서 바꾼 구역 너비, 메인 창 위치·크기.</summary>
    internal LayoutService Layout { get; private set; } = null!;
    public NoteStore Notes { get; private set; } = null!;

    /// <summary>워크플로우 그림 (.excalidraw).</summary>
    public DrawingStore Drawings { get; private set; } = null!;
    public QuickAddCommand QuickAdd { get; private set; } = null!;
    public SearchService Search { get; private set; } = null!;
    public BackupService Backup { get; private set; } = null!;

    /// <summary>새 버전 확인과 설치 (설치는 항상 사용자 확인 후).</summary>
    internal UpdateService Updates { get; private set; } = null!;

    /// <summary>업데이트를 설치했으면 끝난 뒤 이 exe를 다시 시작한다 (Program.Main).</summary>
    internal string? RestartPath { get; private set; }

    /// <summary>다시 시작할 때 메인 창을 열지 (업데이트 전에 열려 있었으면).</summary>
    internal bool RestartShowWindow { get; private set; }
    public ItemTrash Trash { get; private set; } = null!;

    /// <summary>메모 화면이 열리면 이 메모를 연다 (검색 결과).</summary>
    public string? PendingOpenNote { get; set; }

    /// <summary>열려 있는 메인 창 (대화상자의 소유자로 쓴다).</summary>
    internal Window? MainShell => _mainWindow;

    /// <summary>등록하지 못한 전역 단축키 (설정 화면 표시용).</summary>
    internal IReadOnlyList<string> HotkeyFailures => _hotkeys?.Failures ?? [];

    /// <summary>메모 화면이 열리면 새 메모를 만든다 (트레이 "새 메모").</summary>
    public bool PendingNewNote { get; set; }
    public AutoSaveService AutoSave { get; private set; } = null!;
    public ReminderRepository Reminders { get; private set; } = null!;
    public ReminderEngine ReminderEngine { get; private set; } = null!;
    internal ReminderPresenter ReminderPresenter => _reminderPresenter!;

    /// <summary>매 분 0초 무렵 발생한다. 날짜/기한 지남처럼 시간에 따라 바뀌는 표시를 갱신할 때 쓴다.</summary>
    public event EventHandler? MinuteTick;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var stopwatch = Stopwatch.StartNew();

        // 창을 모두 닫아도 트레이로 계속 실행한다. 종료는 ExitApplication으로만.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 날짜 선택기 등 WPF 기본 컨트롤을 한국어 형식으로 표시한다.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.GetCultureInfo("ko-KR").IetfLanguageTag)));

        // Microsoft Store(MSIX) 판은 설치 폴더가 읽기 전용이라 패키지 전용 폴더를 쓴다.
        var home = PackageInfo.FamilyName is { } packageFamily
            ? AppPaths.ForPackage(packageFamily)
            : AppPaths.Resolve(AppContext.BaseDirectory);
        if (ResolveDataLocation(home) is not { } paths)
        {
            Shutdown(0);
            return;
        }
        Paths = paths;
        Paths.EnsureCreated();
        Log.Initialize(Paths.LogsDirectory);
        RegisterExceptionHandlers();

        Settings = new SettingsStore(Paths.SettingsPath);
        Settings.Load();
        if (!File.Exists(Paths.SettingsPath))
        {
            // 사용자가 직접 열어볼 수 있도록 기본값을 파일로 남긴다.
            Settings.Save();
        }
        if (_options.LoginStartup && !Settings.Current.Startup.StartMinimized)
        {
            // Store 판의 StartupTask는 --minimized를 넘기지 못해 일단 트레이로 시작했다고 보고, 여기서 설정대로 고친다.
            _options = _options with { Minimized = false };
        }
        ThemeService.RegisterWindowHook();
        ThemeService.Apply(this, Settings.Current.Ui.Theme);
        FontService.Apply(this, Settings.Current.Ui);
        Settings.Saved += (_, _) =>
        {
            ThemeService.Apply(this, Settings.Current.Ui.Theme);
            FontService.Apply(this, Settings.Current.Ui);
            FontService.RescaleOpenWindows(this);
            ApplyStartupRegistration();
            UpdateTrayToolTip();
            AutoSave?.ApplySettings();
            // 날짜만 있는 마감의 알림 시각, 브리핑 시각이 바뀌었을 수 있다.
            Reminders?.RecomputeAll();
            ReminderEngine?.Reschedule();
            RegisterHotkeys();
        };

        // 트레이 아이콘을 먼저 띄운다. DB와 알림 초기화는 바로 다음 순서로 이어서 한다 (사용자가 기다리는 시간 단축).
        _tray = new TrayService(this);
        _tray.ToolTipText = "DayDeck · 시작하는 중…";
        _instance.StartListening(command => Dispatcher.BeginInvoke(() => HandleCommand(command)));
        Log.Info($"트레이 표시 {stopwatch.ElapsedMilliseconds}ms (portable={Paths.IsPortable}, packaged={Paths.IsPackaged}, relocated={Paths.IsRelocated}, minimized={_options.Minimized})");

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => InitializeServices(stopwatch));
    }

    /// <summary>
    /// 데이터 폴더를 옮겼으면 그 위치를 쓴다. 그 폴더가 없으면(외장 디스크를 뺐을 때 등) 다시 시도할지,
    /// 이번에는 기본 위치를 쓸지, 끝낼지 묻는다. 끝내면 null.
    /// </summary>
    private static AppPaths? ResolveDataLocation(AppPaths home)
    {
        while (DataLocation.ReadPointer(home.HomeDirectory) is { } moved)
        {
            if (Directory.Exists(moved))
            {
                return home.WithDataDirectory(moved);
            }
            var answer = MessageBox.Show(
                $"데이터 폴더를 찾을 수 없습니다.\n\n{moved}\n\n"
                + "외장 디스크나 네트워크 드라이브라면 연결한 뒤 [예]를 눌러 다시 시도하세요.\n"
                + $"[아니요]를 누르면 이번에는 기본 위치({home.HomeDirectory})를 씁니다. 옮긴 위치는 그대로 기억합니다.\n"
                + "[취소]를 누르면 DayDeck을 끝냅니다.",
                "DayDeck", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.No)
            {
                return home;
            }
            if (answer != MessageBoxResult.Yes)
            {
                return null;
            }
        }
        return home;
    }

    private void InitializeServices(Stopwatch stopwatch)
    {
        var phase = Stopwatch.StartNew();
        Database = new Database(Paths.DatabasePath);
        try
        {
            Database.Migrate();
        }
        catch (Exception ex)
        {
            Log.Error("데이터베이스를 열 수 없습니다", ex);
            MessageBox.Show($"데이터베이스를 열 수 없습니다.\n\n{Paths.DatabasePath}\n\n{ex.Message}",
                "DayDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var dbMs = phase.ElapsedMilliseconds;

        ApplyStartupRegistration();

        Tasks = new TaskRepository(Database, TimeProvider.System);
        Events = new EventRepository(Database, TimeProvider.System);
        AppState = new AppStateStore(Database);
        Layout = new LayoutService(AppState);
        Notes = new NoteStore(Paths.ResolveNotesRoot(Settings.Current.Notes.RootPath), AppState, TimeProvider.System);
        Drawings = new DrawingStore(Paths.DrawingsDirectory);
        var uiContext = new DispatcherSynchronizationContext(Dispatcher);
        AutoSave = new AutoSaveService(() => Settings.Current.AutoSave, TimeProvider.System, uiContext);
        Reminders = new ReminderRepository(Database, TimeProvider.System, () => Settings.Current.Reminders);
        ReminderEngine = new ReminderEngine(Reminders, Tasks, AppState, () => Settings.Current.Reminders,
            TimeProvider.System, uiContext);
        _reminderPresenter = new ReminderPresenter(this);
        QuickAdd = new QuickAddCommand(Tasks, Events, Reminders, TimeProvider.System);
        Search = new SearchService(Database, Notes);
        Trash = new ItemTrash(Database, TimeProvider.System);
        Backup = new BackupService(Paths, Database, AppState, TimeProvider.System, () => Notes.Root,
            () => Paths.ResolveBackupsDirectory(Settings.Current.Backup.Directory));
        InitializeExtras();
        Trash.Changed += (_, _) =>
        {
            Reminders.RecomputeAll();
            UpdateTrayToolTip();
        };
        _hotkeys = new HotkeyService();
        RegisterHotkeys();

        Tasks.Changed += (_, _) =>
        {
            Reminders.RecomputeAll();
            _reminderPresenter.PruneResolved();
            UpdateTrayToolTip();
        };
        Events.Changed += (_, _) =>
        {
            Reminders.RecomputeAll();
            _reminderPresenter.PruneResolved();
        };
        Reminders.Changed += (_, _) => ReminderEngine.Reschedule();
        ReminderEngine.RemindersDue += (_, due) => _reminderPresenter.ShowReminders(due);
        ReminderEngine.BriefingDue += (_, briefing) => _reminderPresenter.ShowBriefing(briefing);
        ReminderEngine.PauseChanged += (_, _) => UpdateTrayToolTip();
        _reminderPresenter.PendingChanged += (_, _) => UpdateTrayToolTip();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _ready = true;
        StartMinuteTimer();
        UpdateTrayToolTip();
        var servicesMs = phase.ElapsedMilliseconds;

        if (!_options.Minimized || _showRequested)
        {
            ShowMainWindow(_requestedPage);
        }
        if (_options.QuickAdd)
        {
            ShowQuickAdd();
        }
        if (_options.Today)
        {
            ShowTodayPopup();
        }
        var windowMs = phase.ElapsedMilliseconds;

        Reminders.RecomputeAll();
        ReminderEngine.Start();
        RestoreDesktopWindows();
        PurgeNotesTrash();
        RunDailyMaintenance();
        Updates = new UpdateService(this);
        Updates.Start();
        if (_options.Updated)
        {
            Log.Info($"업데이트 후 시작: {UpdateService.CurrentVersion.ToString(3)}");
            _tray?.Notify("DayDeck 업데이트 완료", $"{UpdateService.CurrentVersion.ToString(3)} 버전으로 바뀌었습니다. 데이터는 그대로입니다.");
        }
        UpdateService.CleanupAfterUpdate();

        Log.Info($"시작 완료 {stopwatch.ElapsedMilliseconds}ms (DB {dbMs}ms, 서비스 {servicesMs - dbMs}ms, " +
                 $"창 {windowMs - servicesMs}ms, 알림 {phase.ElapsedMilliseconds - windowMs}ms)");

        if (_options.Minimized && _mainWindow is null)
        {
            ScheduleMemoryTrim();
        }
    }

    public void ShowMainWindow(MainPage? page = null)
    {
        if (_exiting)
        {
            return;
        }
        if (!_ready)
        {
            // 초기화가 끝나면 연다.
            _showRequested = true;
            _requestedPage = page ?? _requestedPage;
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += OnMainWindowClosed;
        }

        if (page is { } target)
        {
            _mainWindow.Navigate(target);
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        // 다른 창 뒤에 숨어 있으면 앞으로 가져온다.
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    /// <summary>새 버전 파일을 넣었다: 저장하고 끝낸 뒤 새 버전으로 다시 시작한다.</summary>
    internal void RestartAfterUpdate(string exe)
    {
        RestartPath = exe;
        RestartShowWindow = _mainWindow?.IsVisible == true;
        ExitApplication();
    }

    /// <summary>앱을 끝낸 뒤 실행할 데이터·메모 폴더 옮기기 (Program이 처리하고 다시 시작한다).</summary>
    internal Relocation? PendingRelocation { get; private set; }

    /// <summary>데이터·메모 폴더를 옮긴다: 저장하고 끝낸 뒤(DB를 닫은 뒤) 옮기고 다시 시작한다.</summary>
    internal void RestartToRelocate(Relocation relocation)
    {
        PendingRelocation = relocation;
        RestartShowWindow = _mainWindow?.IsVisible == true;
        ExitApplication();
    }

    /// <summary>트레이 "업데이트 확인": 새 버전이 없으면 결과를 알림으로 보인다.</summary>
    public async void CheckForUpdatesFromTray()
    {
        if (!_ready)
        {
            return;
        }
        var result = await Updates.CheckAsync(manual: true);
        if (!result.StartsWith("새 버전", StringComparison.Ordinal))
        {
            _tray?.Notify("DayDeck 업데이트", result);
        }
    }

    public void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;

        SaveDesktopWindows();
        _mainWindow?.Close();
        Shutdown();
    }

    /// <summary>할 일 또는 일정을 메인 창에서 연다 (알림의 "열기").</summary>
    public void ShowTask(long itemId, DateTime? occurrenceStart = null)
    {
        ShowMainWindow();
        if (Events.Get(itemId) is { } calendarEvent)
        {
            _mainWindow?.OpenEvent(calendarEvent, occurrenceStart);
        }
        else
        {
            _mainWindow?.OpenTask(itemId);
        }
    }

    /// <summary>알림을 잠시 멈춘다. null이면 다시 시작.</summary>
    public void PauseReminders(DateTime? until)
    {
        if (_ready)
        {
            ReminderEngine.Pause(until);
        }
    }

    /// <summary>빠른 추가 창을 연다 (전역 단축키, 트레이, --quick-add).</summary>
    public void ShowQuickAdd()
    {
        if (!_ready)
        {
            return;
        }
        if (_quickAddWindow is null)
        {
            _quickAddWindow = new QuickAddWindow();
            _quickAddWindow.Closed += (_, _) =>
            {
                _quickAddWindow = null;
                if (_mainWindow is null)
                {
                    ScheduleMemoryTrim();
                }
            };
            _quickAddWindow.Show();
        }
        _quickAddWindow.Activate();
    }

    /// <summary>메인 창이 앞에 있으면 트레이로 숨기고, 아니면 연다 (Ctrl+Alt+T).</summary>
    public void ToggleMainWindow()
    {
        if (_mainWindow is { IsActive: true })
        {
            _mainWindow.Close();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }
        var hotkeys = Settings.Current.Hotkeys;
        var quickAdd = hotkeys.QuickAddEnabled ? hotkeys.QuickAdd : "";
        var openMain = hotkeys.OpenMainEnabled ? hotkeys.OpenMain : "";
        var newNote = hotkeys.NewNoteEnabled ? hotkeys.NewNote : "";
        var signature = $"{quickAdd}|{openMain}|{newNote}";
        if (signature == _registeredHotkeys)
        {
            return;
        }
        _registeredHotkeys = signature;
        _hotkeys.Register([(quickAdd, ShowQuickAdd), (openMain, ToggleMainWindow), (newNote, ShowNewNote)]);
    }

    /// <summary>메모 화면을 열고 새 메모를 만든다 (트레이 "새 메모").</summary>
    public void ShowNewNote()
    {
        PendingNewNote = true;
        ShowMainWindow(MainPage.Notes);
        _mainWindow?.ConsumePendingNewNote();
    }

    /// <summary>하루 한 번 자동 백업, 30일 지난 휴지통 비우기. 시작을 늦추지 않도록 백그라운드에서 한다.</summary>
    private void RunDailyMaintenance()
    {
        Task.Run(() =>
        {
            try
            {
                if (Trash.Purge() is > 0 and var removed)
                {
                    Log.Info($"휴지통에서 {removed}개 영구 삭제 (30일 지남)");
                }
                Backup.BackupIfDue();
            }
            catch (Exception ex)
            {
                Log.Warn("자동 백업/정리 실패", ex);
            }
        });
    }

    /// <summary>휴지통에서 할 일/일정을 되돌렸다 → 목록, 알림, 트레이를 다시 그리게 한다.</summary>
    public void NotifyDataRestored()
    {
        Reminders.RecomputeAll();
        MinuteTick?.Invoke(this, EventArgs.Empty);
        UpdateTrayToolTip();
    }

    /// <summary>메모 화면에서 메모를 연다 (전체 검색).</summary>
    public void ShowNote(string path)
    {
        PendingOpenNote = path;
        ShowMainWindow(MainPage.Notes);
        _mainWindow?.ConsumePendingNewNote();
    }

    /// <summary>전체 검색 창 (Ctrl+K).</summary>
    public void ShowSearch()
    {
        // 메인 창이 이미 앞에 있으면 다시 활성화하지 않는다 (늦게 도착한 활성화가 검색 창을 닫아 버림).
        if (_mainWindow is not { IsVisible: true, IsActive: true })
        {
            ShowMainWindow();
        }
        if (_mainWindow is not null)
        {
            var owner = _mainWindow;
            // 메인 창 활성화가 끝난 뒤에 띄운다.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => new SearchWindow { Owner = owner }.Show());
        }
    }

    private void PurgeNotesTrash()
    {
        try
        {
            if (Notes.PurgeTrash() is > 0 and var removed)
            {
                Log.Info($"메모 휴지통에서 {removed}개 영구 삭제 (30일 지남)");
            }
        }
        catch (IOException ex)
        {
            Log.Warn("메모 휴지통 정리 실패", ex);
        }
    }

    public void ShowBriefingNow()
    {
        if (_ready)
        {
            _reminderPresenter?.ShowBriefing(ReminderEngine.CreateBriefing());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ReminderEngine?.Dispose();
        Updates?.Dispose();
        _hotkeys?.Dispose();
        AutoSave?.FlushAll();
        AutoSave?.Dispose();
        _minuteTimer?.Stop();
        _tray?.Dispose();
        Log.Info($"종료 (code={e.ApplicationExitCode})");
        base.OnExit(e);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }

        if (_exiting)
        {
            return;
        }

        if (Settings.Current.Tray.CloseToTray)
        {
            ScheduleMemoryTrim();
        }
        else
        {
            ExitApplication();
        }
    }

    private void HandleCommand(string command)
    {
        if (command == SingleInstance.ShowCommand)
        {
            ShowMainWindow();
        }
        else if (command == SingleInstance.QuickAddCommand)
        {
            ShowQuickAdd();
        }
        else if (command == SingleInstance.TodayCommand)
        {
            ShowTodayPopup();
        }
    }

    private void StartMinuteTimer()
    {
        _minuteTimer = new DispatcherTimer(DispatcherPriority.Background);
        _minuteTimer.Tick += (_, _) =>
        {
            // 절전 복귀 등으로 어긋나도 다음 분 경계에 다시 맞춘다.
            _minuteTimer.Interval = UntilNextMinute();
            UpdateTrayToolTip();
            MinuteTick?.Invoke(this, EventArgs.Empty);
        };
        _minuteTimer.Interval = UntilNextMinute();
        _minuteTimer.Start();

        static TimeSpan UntilNextMinute()
        {
            var now = DateTime.Now;
            return TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(200);
        }
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is null || !_ready)
        {
            return;
        }

        var counts = Tasks.GetCounts();
        var text = counts.TodayOpen == 0 && counts.Overdue == 0
            ? "DayDeck · 오늘 할 일을 모두 끝냈습니다"
            : $"DayDeck · 오늘 남은 할 일 {counts.TodayOpen}개";
        if (counts.Overdue > 0)
        {
            text += $" · 기한 지남 {counts.Overdue}개";
        }

        if (NextEventToday() is { } nextEvent)
        {
            var minutes = (int)Math.Ceiling((nextEvent.Start - DateTime.Now).TotalMinutes);
            text += $"\n📅 다음 일정: {nextEvent.Start:HH:mm} {nextEvent.Title} ({(minutes >= 60 ? $"{minutes / 60}시간 {minutes % 60}분" : $"{minutes}분")} 후)";
        }
        if (ReminderEngine.PausedUntil is { } paused)
        {
            text += $"\n🔕 알림 일시 중지 ({paused:M/d HH:mm}까지)";
        }
        else if (Reminders.GetNextFireTime() is { } next && next > DateTime.Now)
        {
            text += $"\n🔔 다음 알림 {next:HH:mm}";
        }

        // 알림 영역 도구 설명은 128자까지만 표시된다.
        _tray.ToolTipText = text.Length > 127 ? text[..126] + "…" : text;
        // 기한 지난 할 일이 있거나 확인하지 않은 알림이 있으면 아이콘에 빨간 점을 표시한다.
        _tray.ShowAlert = Settings.Current.Tray.ShowBadge && (counts.Overdue > 0 || (_reminderPresenter?.PendingCount ?? 0) > 0);
        _tray.IsPaused = ReminderEngine.PausedUntil is not null;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // 절전 중에는 타이머가 늦게 깨므로 바로 확인한다. 지난 알림은 "놓친 알림"으로 보인다.
            Dispatcher.BeginInvoke(() =>
            {
                Log.Info("절전 복귀");
                ReminderEngine.Reschedule();
                MinuteTick?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            Reminders.RecomputeAll();
            ReminderEngine.Reschedule();
            MinuteTick?.Invoke(this, EventArgs.Empty);
        });

    /// <summary>Windows의 밝게/어둡게 설정이 바뀌면 "시스템 설정 따름"일 때 바로 따라간다.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && Settings.Current.Ui.Theme == AppTheme.System)
        {
            Dispatcher.BeginInvoke(() => ThemeService.Apply(this, AppTheme.System));
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            Dispatcher.BeginInvoke(ReminderEngine.Reschedule);
        }
    }

    private void ApplyStartupRegistration()
    {
        var startup = Settings.Current.Startup;
        StartupRegistration.Apply(startup.RunAtLogin, startup.StartMinimized);
    }

    /// <summary>창이 해제되고 렌더링 리소스가 정리될 시간을 준 뒤 메모리를 반환한다.</summary>
    private void ScheduleMemoryTrim()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_mainWindow is null)
            {
                MemoryTrimmer.Trim();
            }
        };
        timer.Start();
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("처리되지 않은 UI 예외", args.Exception);
            MessageBox.Show($"오류가 발생했습니다.\n\n{args.Exception.Message}\n\n자세한 내용: {Paths.LogsDirectory}",
                "DayDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("처리되지 않은 예외", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("관찰되지 않은 작업 예외", args.Exception);
            args.SetObserved();
        };
    }
}
