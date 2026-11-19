using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DayDeck.Core;
using DayDeck.Core.Settings;

namespace DayDeck.App.Views;

public partial class SettingsPage : UserControl
{
    private static readonly int[] IntervalChoices = [1, 2, 3, 5, 10, 15, 30, 60];
    private static readonly (string Label, int? Offset)[] DefaultReminderChoices =
        [("없음", null), ("정시", 0), ("5분 전", -5), ("10분 전", -10), ("30분 전", -30), ("1시간 전", -60), ("1일 전", -1440)];
    private static readonly int[] SnoozeChoices = [5, 10, 15, 30, 60];
    private static readonly int[] PersistentChoices = [1, 3, 5, 10, 15, 30];

    private readonly App _app = App.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        _loading = true;
        var settings = _app.Settings.Current;

        RunAtLogin.IsChecked = settings.Startup.RunAtLogin;
        StartMinimized.IsChecked = settings.Startup.StartMinimized;
        StartMinimized.IsEnabled = settings.Startup.RunAtLogin;
        CloseToTray.IsChecked = settings.Tray.CloseToTray;
        Fill(TrayClick, [("오늘 할 일 팝업", TrayClickAction.OpenToday), ("메인 창 열기", TrayClickAction.OpenMain), ("빠른 추가", TrayClickAction.QuickAdd)],
            settings.Tray.LeftClick);
        ShowBadge.IsChecked = settings.Tray.ShowBadge;
        SaveAfterEdit.IsChecked = settings.AutoSave.SaveAfterEdit;

        AutoSaveInterval.Items.Clear();
        foreach (var minutes in IntervalChoices.Union([settings.AutoSave.IntervalMinutes]).Order())
        {
            AutoSaveInterval.Items.Add(new ComboBoxItem { Content = $"{minutes}분마다", Tag = minutes });
        }
        AutoSaveInterval.SelectedItem = AutoSaveInterval.Items.Cast<ComboBoxItem>()
            .First(i => (int)i.Tag == settings.AutoSave.IntervalMinutes);

        var reminders = settings.Reminders;
        var defaultOffset = reminders.AutoAddDefault && reminders.DefaultOffsets.Length > 0 ? reminders.DefaultOffsets[0] : (int?)null;
        var reminderChoices = DefaultReminderChoices.Select(c => (c.Label, (object?)c.Offset)).ToList();
        if (defaultOffset is int custom && DefaultReminderChoices.All(c => c.Offset != custom))
        {
            reminderChoices.Add((Core.Reminders.ReminderRules.Describe(new Core.Reminders.Reminder { OffsetMinutes = custom }, default), custom));
        }
        Fill(DefaultReminder, reminderChoices, defaultOffset);
        var times = Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Fill(DateOnlyTime, times.Union([reminders.DateOnlyReminderTime]).Order().Select(t => (t, (object?)t)), reminders.DateOnlyReminderTime);
        Fill(BriefingTime, times.Union([reminders.DailyBriefingTime]).Order().Select(t => (t, (object?)t)), reminders.DailyBriefingTime);
        Fill(SnoozeMinutes, SnoozeChoices.Union([reminders.DefaultSnoozeMinutes]).Order().Select(m => ($"{m}분", (object?)m)), reminders.DefaultSnoozeMinutes);
        Fill(PersistentMinutes, PersistentChoices.Union([reminders.PersistentRepeatMinutes]).Order().Select(m => ($"{m}분마다", (object?)m)), reminders.PersistentRepeatMinutes);
        var sounds = new List<(string, object?)> { ("기본 (Windows 알림음)", "Default"), ("무음", "None") };
        if (reminders.Sound is not ("Default" or "None"))
        {
            sounds.Add((System.IO.Path.GetFileName(reminders.Sound), reminders.Sound));
        }
        Fill(SoundChoice, sounds, reminders.Sound);
        BriefingEnabled.IsChecked = reminders.DailyBriefingEnabled;
        BriefingTime.IsEnabled = reminders.DailyBriefingEnabled;

        WidgetEnabled.IsChecked = settings.Widget.Enabled;
        var fonts = Services.FontService.InstalledFonts();
        Fill(UiFontChoice, [("기본 (Segoe UI + 맑은 고딕)", null), .. fonts.Union(settings.Ui.FontFamily is { } uiFont ? [uiFont] : []).Select(f => (f, (object?)f))],
            settings.Ui.FontFamily);
        Fill(UiFontSize, new[] { 12, 13, 14, 15, 16, 18, 20 }.Union([settings.Ui.FontSize]).Order()
            .Select(s => ($"{s}pt" + (s == 16 ? " (기본)" : ""), (object?)s)), settings.Ui.FontSize);
        Fill(EditorFontChoice, [("기본 (Cascadia Mono + 맑은 고딕)", null), .. fonts.Union(settings.Notes.EditorFontFamily is { } editorFont ? [editorFont] : []).Select(f => (f, (object?)f))],
            settings.Notes.EditorFontFamily);
        Fill(EditorFontSize, new[] { 10, 11, 12, 13, 14, 15, 16, 18, 20, 24 }.Union([settings.Notes.EditorFontSize]).Order()
            .Select(s => ($"{s}pt" + (s == 16 ? " (기본)" : ""), (object?)s)), settings.Notes.EditorFontSize);
        UpdateFontPreview();
        Fill(CompletedDisplayChoice, [("그대로 보기", CompletedDisplay.Show), ("흐리게 보기", CompletedDisplay.Dim), ("숨기기 (완료됨 화면에만)", CompletedDisplay.Hide)],
            settings.Ui.CompletedTasks);
        Fill(ThemeChoice, [("시스템 설정 따름", AppTheme.System), ("밝게", AppTheme.Light), ("어둡게", AppTheme.Dark)], settings.Ui.Theme);

        var calendar = settings.Calendar;
        Fill(WeekStart, [("일요일", DayOfWeek.Sunday), ("월요일", DayOfWeek.Monday)], calendar.WeekStartsOn);
        Fill(SlotMinutes, new[] { 5, 10, 15, 30 }.Select(m => ($"{m}분", (object?)m)), calendar.TimeSlotMinutes);
        ShowHolidays.IsChecked = calendar.ShowKoreanHolidays;
        ShowLunar.IsChecked = calendar.ShowLunar;
        var hourChoices = Enumerable.Range(0, 25).Select(h => h == 24 ? "24:00" : $"{h:00}:00").ToList();
        var (workStart, workEnd) = calendar.WorkHours is [var ws, var we] ? (ws, we) : ("09:00", "18:00");
        Fill(WorkStart, hourChoices.Take(24).Union([workStart]).Order().Select(h => (h, (object?)h)), workStart);
        Fill(WorkEnd, hourChoices.Skip(1).Where(h => h != "24:00").Union([workEnd]).Order().Select(h => (h, (object?)h)), workEnd);

        HotkeyQuickAdd.Text = settings.Hotkeys.QuickAdd;
        HotkeyOpenMain.Text = settings.Hotkeys.OpenMain;
        HotkeyNewNote.Text = settings.Hotkeys.NewNote;
        HotkeyQuickAddEnabled.IsChecked = settings.Hotkeys.QuickAddEnabled;
        HotkeyOpenMainEnabled.IsChecked = settings.Hotkeys.OpenMainEnabled;
        HotkeyNewNoteEnabled.IsChecked = settings.Hotkeys.NewNoteEnabled;
        UpdateHotkeyBoxes();
        UpdateHotkeyStatus();
        FormatShortcuts.IsChecked = settings.Notes.FormatShortcuts;
        AutoUpdateCheck.IsChecked = settings.Updates.CheckAutomatically;
        CurrentVersionText.Text = $"현재 {Services.UpdateService.CurrentVersion.ToString(3)}";
        if (Services.UpdateService.ManagedByStore)
        {
            // Store 판은 Store가 업데이트한다. 앱이 직접 확인·설치하는 기능은 숨긴다.
            AutoUpdateCheck.Visibility = Visibility.Collapsed;
            AutoUpdateHint.Visibility = Visibility.Collapsed;
            CheckUpdateButton.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = Services.UpdateService.StoreManagedMessage;
        }
        PasteImages.IsChecked = settings.Notes.PasteImages;
        StartInObsidianMode.IsChecked = settings.Notes.StartInObsidianMode;

        var paths = _app.Paths;
        DataFolder.Text = paths.DataDirectory;
        DataFolder.ToolTip = paths.DataDirectory;
        var notesRoot = paths.ResolveNotesRoot(settings.Notes.RootPath);
        NotesFolder.Text = notesRoot;
        NotesFolder.ToolTip = notesRoot;
        PortableHint.Text = (paths.IsPackaged
            ? "Microsoft Store 판은 Windows가 정한 앱 전용 폴더에 저장합니다. 앱을 제거하면 이 폴더도 지워지니, 필요하면 먼저 백업하세요."
            : paths.IsPortable
            ? "데이터는 실행 파일 옆의 Data 폴더에 저장됩니다. exe와 Data 폴더를 함께 옮기면 다른 PC(또는 USB)에서 그대로 쓸 수 있습니다."
            : "실행 파일 폴더에 쓸 수 없어(예: Program Files) %APPDATA%\\DayDeck에 저장합니다. 쓸 수 있는 폴더로 exe를 옮기면 그 옆 Data를 씁니다.")
            + "\n다른 PC로 옮기려면 트레이에서 종료한 뒤 데이터 폴더를 통째로 복사하세요.";

        UpdateBackupStatus();

        var version = typeof(App).Assembly.GetName().Version;
        AboutText.Text = $"DayDeck {version?.ToString(3)} · .NET {Environment.Version} · DB 스키마 v{_app.Database.GetSchemaVersion()}";

        _loading = false;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var settings = _app.Settings.Current;
        settings.Startup.RunAtLogin = RunAtLogin.IsChecked == true;
        settings.Startup.StartMinimized = StartMinimized.IsChecked == true;
        settings.Tray.CloseToTray = CloseToTray.IsChecked == true;
        if (Selected(TrayClick) is TrayClickAction click)
        {
            settings.Tray.LeftClick = click;
        }
        settings.Tray.ShowBadge = ShowBadge.IsChecked == true;
        settings.AutoSave.SaveAfterEdit = SaveAfterEdit.IsChecked == true;
        if (AutoSaveInterval.SelectedItem is ComboBoxItem { Tag: int minutes })
        {
            settings.AutoSave.IntervalMinutes = minutes;
        }

        var reminders = settings.Reminders;
        if (DefaultReminder.SelectedItem is not null)
        {
            var offset = Selected(DefaultReminder);
            reminders.AutoAddDefault = offset is not null;
            reminders.DefaultOffsets = offset is int o ? [o] : [];
        }
        if (Selected(DateOnlyTime) is string dateOnlyTime)
        {
            reminders.DateOnlyReminderTime = dateOnlyTime;
        }
        if (Selected(SnoozeMinutes) is int snooze)
        {
            reminders.DefaultSnoozeMinutes = snooze;
        }
        if (Selected(PersistentMinutes) is int persistent)
        {
            reminders.PersistentRepeatMinutes = persistent;
        }
        if (Selected(SoundChoice) is string sound)
        {
            reminders.Sound = sound;
        }
        reminders.DailyBriefingEnabled = BriefingEnabled.IsChecked == true;
        if (Selected(BriefingTime) is string briefingTime)
        {
            reminders.DailyBriefingTime = briefingTime;
        }
        BriefingTime.IsEnabled = reminders.DailyBriefingEnabled;

        if (Selected(WeekStart) is DayOfWeek weekStart)
        {
            settings.Calendar.WeekStartsOn = weekStart;
        }
        if (Selected(SlotMinutes) is int slot)
        {
            settings.Calendar.TimeSlotMinutes = slot;
        }
        settings.Calendar.ShowKoreanHolidays = ShowHolidays.IsChecked == true;
        settings.Calendar.ShowLunar = ShowLunar.IsChecked == true;
        if (Selected(WorkStart) is string workStart && Selected(WorkEnd) is string workEnd && string.CompareOrdinal(workStart, workEnd) < 0)
        {
            settings.Calendar.WorkHours = [workStart, workEnd];
        }
        settings.Widget.Enabled = WidgetEnabled.IsChecked == true;
        if (UiFontChoice.SelectedItem is ComboBoxItem uiFontItem)
        {
            settings.Ui.FontFamily = uiFontItem.Tag as string;
        }
        if (Selected(UiFontSize) is int uiSize)
        {
            settings.Ui.FontSize = uiSize;
        }
        if (EditorFontChoice.SelectedItem is ComboBoxItem editorFontItem)
        {
            settings.Notes.EditorFontFamily = editorFontItem.Tag as string;
        }
        if (Selected(EditorFontSize) is int editorSize)
        {
            settings.Notes.EditorFontSize = editorSize;
        }
        UpdateFontPreview();
        settings.Notes.FormatShortcuts = FormatShortcuts.IsChecked == true;
        settings.Notes.PasteImages = PasteImages.IsChecked == true;
        settings.Notes.StartInObsidianMode = StartInObsidianMode.IsChecked == true;
        settings.Updates.CheckAutomatically = AutoUpdateCheck.IsChecked == true;
        if (Selected(CompletedDisplayChoice) is CompletedDisplay completedDisplay)
        {
            settings.Ui.CompletedTasks = completedDisplay;
        }
        if (Selected(ThemeChoice) is AppTheme theme)
        {
            settings.Ui.Theme = theme;
        }

        StartMinimized.IsEnabled = settings.Startup.RunAtLogin;
        _app.Settings.Save();
    }

    /// <summary>입력칸을 누른 채 키 조합을 누르면 기록한다. Backspace/Delete는 해제.</summary>
    private void OnHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        string? gesture;
        if (key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            gesture = "";
        }
        else if (Services.HotkeyService.Format(System.Windows.Input.Keyboard.Modifiers, key) is { } formatted)
        {
            gesture = formatted;
        }
        else
        {
            return;
        }

        box.Text = gesture;
        var hotkeys = _app.Settings.Current.Hotkeys;
        if (box == HotkeyQuickAdd)
        {
            hotkeys.QuickAdd = gesture;
        }
        else if (box == HotkeyOpenMain)
        {
            hotkeys.OpenMain = gesture;
        }
        else
        {
            hotkeys.NewNote = gesture;
        }
        _app.Settings.Save();
        UpdateHotkeyStatus();
    }

    private void OnHotkeyDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = new Core.Settings.HotkeySettings();
        _app.Settings.Current.Hotkeys = defaults;
        _loading = true;
        HotkeyQuickAdd.Text = defaults.QuickAdd;
        HotkeyOpenMain.Text = defaults.OpenMain;
        HotkeyNewNote.Text = defaults.NewNote;
        HotkeyQuickAddEnabled.IsChecked = HotkeyOpenMainEnabled.IsChecked = HotkeyNewNoteEnabled.IsChecked = true;
        _loading = false;
        UpdateHotkeyBoxes();
        _app.Settings.Save();
        UpdateHotkeyStatus();
    }

    /// <summary>단축키 하나 켜기/끄기. 끈 단축키는 등록을 풀어 다른 프로그램이 쓸 수 있게 한다.</summary>
    private void OnHotkeyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var hotkeys = _app.Settings.Current.Hotkeys;
        hotkeys.QuickAddEnabled = HotkeyQuickAddEnabled.IsChecked == true;
        hotkeys.OpenMainEnabled = HotkeyOpenMainEnabled.IsChecked == true;
        hotkeys.NewNoteEnabled = HotkeyNewNoteEnabled.IsChecked == true;
        UpdateHotkeyBoxes();
        _app.Settings.Save();
        UpdateHotkeyStatus();
    }

    private void UpdateHotkeyBoxes()
    {
        HotkeyQuickAdd.IsEnabled = HotkeyQuickAddEnabled.IsChecked == true;
        HotkeyOpenMain.IsEnabled = HotkeyOpenMainEnabled.IsChecked == true;
        HotkeyNewNote.IsEnabled = HotkeyNewNoteEnabled.IsChecked == true;
    }

    private void UpdateHotkeyStatus()
    {
        var failures = _app.HotkeyFailures;
        HotkeyStatus.Text = failures.Count == 0
            ? "칸을 누르고 원하는 조합(Ctrl/Alt/Shift/Win + 키)을 누르면 바뀝니다. 지우려면 Backspace."
            : $"⚠ 등록하지 못함: {string.Join(", ", failures)} — 다른 프로그램이 쓰고 있을 수 있습니다. 다른 조합을 고르세요.";
        HotkeyStatus.Foreground = failures.Count == 0 ? (System.Windows.Media.Brush)FindResource("SubTextBrush") : (System.Windows.Media.Brush)FindResource("DangerBrush");
    }

    private void OnTestSound(object sender, RoutedEventArgs e) => _app.ReminderPresenter.PlaySound();

    private void OnShowBriefing(object sender, RoutedEventArgs e) => _app.ShowBriefingNow();

    private static void Fill(ComboBox combo, IEnumerable<(string Label, object? Value)> choices, object? selected)
    {
        combo.Items.Clear();
        foreach (var (label, value) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            combo.Items.Add(item);
            if (Equals(value, selected))
            {
                combo.SelectedItem = item;
            }
        }
    }

    /// <summary>끌어서 바꾼 구역 너비와 창 위치·크기를 지운다. 창을 다시 열면 처음 배치로 보인다.</summary>
    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _app.Layout.Reset();
        LayoutStatus.Text = "되돌렸습니다. 창을 닫았다가 다시 열면 처음 배치로 보입니다.";
    }

    /// <summary>고른 메모 글꼴로 미리보기 (화면 글꼴은 창 전체에 바로 적용되어 보인다).</summary>
    private void UpdateFontPreview()
    {
        var notes = _app.Settings.Current.Notes;
        FontPreview.FontFamily = Services.FontService.EditorFont(notes);
        FontPreview.FontSize = notes.EditorFontSize;
    }

    private static object? Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag;

    private void UpdateBackupStatus()
    {
        BackupFolder.Text = _app.Backup.BackupDirectory;
        BackupFolder.ToolTip = _app.Backup.BackupDirectory;
        ResetBackupFolder.IsEnabled = _app.Settings.Current.Backup.Directory is not null;
        var count = _app.Backup.GetBackups().Count;
        BackupStatus.Text = _app.Backup.LastBackup is { } last
            ? $"마지막 백업 {last:yyyy-MM-dd HH:mm} · 보관 {count}개"
            : "아직 백업 없음";
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "확인하는 중…";
        UpdateStatus.Text = await _app.Updates.CheckAsync(manual: true);
        CheckUpdateButton.IsEnabled = true;
    }

    private async void OnBackupNow(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        BackupStatus.Text = "백업하는 중…";
        try
        {
            var path = await Task.Run(_app.Backup.CreateBackup);
            UpdateBackupStatus();
            BackupStatus.Text += $" · {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            BackupStatus.Text = "백업 실패: " + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_app.Backup.BackupDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Backup.BackupDirectory}\"") { UseShellExecute = true });
    }

    /// <summary>백업을 다른 디스크(예: 외장 디스크, OneDrive)에 두면 PC 고장에도 안전하다.</summary>
    private void OnChangeBackupFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "백업 위치", InitialDirectory = _app.Backup.BackupDirectory };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        _app.Settings.Current.Backup.Directory = _app.Paths.ToStoredPath(dialog.FolderName);
        _app.Settings.Save();
        UpdateBackupStatus();
    }

    private void OnResetBackupFolder(object sender, RoutedEventArgs e)
    {
        _app.Settings.Current.Backup.Directory = null;
        _app.Settings.Save();
        UpdateBackupStatus();
    }

    private void OnImportIcs(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "iCalendar (*.ics)|*.ics|모든 파일 (*.*)|*.*", Title = "ICS 가져오기" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            var summary = new Core.Exchange.IcsImporter(_app.Tasks, _app.Events, TimeProvider.System)
                .Import(System.IO.File.ReadAllText(dialog.FileName));
            ExchangeStatus.Text = $"✓ 가져옴: 일정 {summary.Events}개, 할 일 {summary.Tasks}개"
                                  + (summary.Duplicates > 0 ? $" · 이미 있어서 건너뜀 {summary.Duplicates}개" : "")
                                  + (summary.Skipped > 0 ? $" · 읽지 못함 {summary.Skipped}개" : "");
        }
        catch (Exception ex) when (ex is System.IO.IOException or FormatException or UnauthorizedAccessException)
        {
            ExchangeStatus.Text = "가져오기 실패: " + ex.Message;
        }
    }

    private void OnExportIcs(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "iCalendar (*.ics)|*.ics", FileName = $"DayDeck-{DateTime.Now:yyyyMMdd}.ics", Title = "ICS 내보내기",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        Core.IO.AtomicFile.WriteAllText(dialog.FileName, new Core.Exchange.IcsImporter(_app.Tasks, _app.Events, TimeProvider.System).ExportAll());
        ExchangeStatus.Text = $"✓ 내보냄: {dialog.FileName}";
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv", FileName = $"할 일-{DateTime.Now:yyyyMMdd}.csv", Title = "할 일 CSV 내보내기",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        Core.IO.AtomicFile.WriteAllText(dialog.FileName, Core.Exchange.CsvExport.Tasks(_app.Tasks.GetAll()), Core.Exchange.CsvExport.Encoding);
        ExchangeStatus.Text = $"✓ 내보냄: {dialog.FileName}";
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_app.Paths.DataDirectory}\"") { UseShellExecute = true });
}
