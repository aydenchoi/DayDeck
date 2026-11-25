namespace DayDeck.Core.Settings;

// settings.json 구조. 설계 문서 §4.4 참고.
// 누락된 항목은 기본값으로 채워지도록 모든 속성에 기본값을 둔다.

public sealed class AppSettings
{
    /// <summary>설정 형식 버전. 기본값이 바뀌었을 때 예전 기본값을 그대로 쓰던 파일을 새 기본값으로 올린다.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; }

    public AutoSaveSettings AutoSave { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();
    public TraySettings Tray { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public ReminderSettings Reminders { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public NotesSettings Notes { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public WidgetSettings Widget { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public WeeklyReportSettings WeeklyReport { get; set; } = new();

    /// <summary>
    /// 예전 버전 설정을 올린다. 바뀐 것이 있으면 true (파일에 다시 써야 함).
    /// v2: 기본 글자 크기 14 → 16pt (예전 기본값 14 그대로였던 경우만).
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
        {
            return false;
        }
        Ui ??= new();
        Notes ??= new();
        if (Version < 2)
        {
            if (Ui.FontSize == 14)
            {
                Ui.FontSize = 16;
            }
            if (Notes.EditorFontSize == 14)
            {
                Notes.EditorFontSize = 16;
            }
        }
        Version = CurrentVersion;
        return true;
    }

    /// <summary>범위를 벗어난 값이나 null 섹션을 바로잡는다. 사용자가 파일을 직접 편집한 경우를 대비한다.</summary>
    public void Normalize()
    {
        AutoSave ??= new();
        Startup ??= new();
        Tray ??= new();
        Hotkeys ??= new();
        Reminders ??= new();
        Calendar ??= new();
        Notes ??= new();
        Ui ??= new();
        Widget ??= new();
        Backup ??= new();
        Updates ??= new();
        WeeklyReport ??= new();
        WeeklyReport.Normalize();

        AutoSave.IntervalMinutes = Math.Clamp(AutoSave.IntervalMinutes, AutoSaveSettings.MinIntervalMinutes, AutoSaveSettings.MaxIntervalMinutes);
        AutoSave.DebounceSeconds = Math.Clamp(AutoSave.DebounceSeconds, 1, 60);
        Reminders.DefaultSnoozeMinutes = Math.Clamp(Reminders.DefaultSnoozeMinutes, 1, 24 * 60);
        Reminders.DefaultOffsets ??= [];
        Reminders.PersistentRepeatMinutes = Math.Clamp(Reminders.PersistentRepeatMinutes, 1, 120);
        Reminders.Sound ??= "Default";
        Reminders.DateOnlyReminderTime = ReminderSettings.ParseTime(Reminders.DateOnlyReminderTime, new TimeOnly(9, 0)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Reminders.DailyBriefingTime = ReminderSettings.ParseTime(Reminders.DailyBriefingTime, new TimeOnly(9, 0)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Calendar.TimeSlotMinutes = Calendar.TimeSlotMinutes is 5 or 10 or 15 or 30 ? Calendar.TimeSlotMinutes : 15;
        Calendar.WorkHours ??= ["09:00", "18:00"];
        Ui.FontSize = Math.Clamp(Ui.FontSize, 10, 24);
        Notes.EditorFontSize = Math.Clamp(Notes.EditorFontSize, 8, 48);
        Widget.Opacity = Math.Clamp(Widget.Opacity, 0.4, 1.0);
    }
}

public sealed class AutoSaveSettings
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 60;

    public int IntervalMinutes { get; set; } = 1;
    public bool SaveAfterEdit { get; set; } = true;
    public int DebounceSeconds { get; set; } = 3;
    public bool SaveOnFocusLost { get; set; } = true;
}

public sealed class StartupSettings
{
    public bool RunAtLogin { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
}

public sealed class TraySettings
{
    public bool CloseToTray { get; set; } = true;
    /// <summary>기한 지남·확인 안 한 알림이 있으면 아이콘에 빨간 점.</summary>
    public bool ShowBadge { get; set; } = true;

    /// <summary>아이콘 왼쪽 클릭. 두 번 클릭은 항상 메인 창.</summary>
    public TrayClickAction LeftClick { get; set; } = TrayClickAction.OpenToday;
}

public enum TrayClickAction
{
    OpenMain,
    OpenToday,
    QuickAdd,
}

public sealed class HotkeySettings
{
    public string QuickAdd { get; set; } = "Ctrl+Alt+Space";
    public string OpenMain { get; set; } = "Ctrl+Alt+T";
    public string NewNote { get; set; } = "Ctrl+Alt+N";

    // 다른 프로그램과 겹치면 끌 수 있다. 꺼도 조합은 기억해 두었다가 다시 켜면 쓴다.
    public bool QuickAddEnabled { get; set; } = true;
    public bool OpenMainEnabled { get; set; } = true;
    public bool NewNoteEnabled { get; set; } = true;
}

public sealed class ReminderSettings
{
    /// <summary>마감을 정하면 자동으로 붙는 알림 (분, 음수 = 마감 전). 비어 있으면 자동으로 붙이지 않는다.</summary>
    public int[] DefaultOffsets { get; set; } = [-10];
    public bool AutoAddDefault { get; set; } = true;

    /// <summary>시간 없이 날짜만 있는 마감의 알림 기준 시각.</summary>
    public string DateOnlyReminderTime { get; set; } = "09:00";

    public int DefaultSnoozeMinutes { get; set; } = 10;

    /// <summary>끈질긴 알림: 확인할 때까지 몇 분마다 다시 울릴지.</summary>
    public int PersistentRepeatMinutes { get; set; } = 5;

    /// <summary>"Default", "None", 또는 .wav 파일 경로.</summary>
    public string Sound { get; set; } = "Default";

    public bool DailyBriefingEnabled { get; set; } = true;
    public string DailyBriefingTime { get; set; } = "09:00";

    public TimeOnly GetDateOnlyReminderTime() => ParseTime(DateOnlyReminderTime, new TimeOnly(9, 0));

    public TimeOnly GetDailyBriefingTime() => ParseTime(DailyBriefingTime, new TimeOnly(9, 0));

    internal static TimeOnly ParseTime(string? value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var time) ? time : fallback;
}

public sealed class CalendarSettings
{
    public DayOfWeek WeekStartsOn { get; set; } = DayOfWeek.Sunday;
    public int TimeSlotMinutes { get; set; } = 15;
    public string[] WorkHours { get; set; } = ["09:00", "18:00"];
    public bool ShowKoreanHolidays { get; set; } = true;
    /// <summary>월 보기에 음력 날짜 표시.</summary>
    public bool ShowLunar { get; set; }
}

public sealed class NotesSettings
{
    /// <summary>메모 편집기 글꼴. null이면 기본 (Cascadia Mono, Consolas, D2Coding, 맑은 고딕).</summary>
    public string? EditorFontFamily { get; set; }

    public int EditorFontSize { get; set; } = 16;

    /// <summary>
    /// 메모 폴더. null이면 데이터 폴더의 <c>Notes</c>. 상대 경로는 데이터 폴더 기준이다.
    /// 데이터 폴더 밖의 절대 경로를 쓰면 그 폴더는 따로 복사해야 한다. <see cref="AppPaths.ResolveNotesRoot"/> 참고.
    /// </summary>
    public string? RootPath { get; set; }
    public string DefaultExtension { get; set; } = ".md";

    /// <summary>메모·할 일 메모·일정 메모의 서식 단축키 (Ctrl+B/I/U/E/K 등). 끄면 서식 도구 단추만 쓴다.</summary>
    public bool FormatShortcuts { get; set; } = true;

    /// <summary>메모 화면을 열 때 "보기" 대신 "옵시디언" 모드(원문에 서식을 입히고 커서 줄만 기호가 보임)로 시작.</summary>
    public bool StartInObsidianMode { get; set; }

    /// <summary>메모(.md)에서 Ctrl+V로 클립보드 그림을 붙여 넣기. 끄면 그림은 단추나 끌어다 놓기로만 넣는다.</summary>
    public bool PasteImages { get; set; } = true;
}

public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>화면 글꼴. null이면 기본 (Segoe UI + 맑은 고딕).</summary>
    public string? FontFamily { get; set; }

    /// <summary>목록에서 완료한 할 일을 어떻게 보일지 (설계 §5.2).</summary>
    public CompletedDisplay CompletedTasks { get; set; } = CompletedDisplay.Show;
    /// <summary>글자 크기 (pt). 화면은 14pt 기준으로 만들어져 있어 창 전체를 FontSize/14 비율로 키우거나 줄인다. 기본 16pt.</summary>
    public int FontSize { get; set; } = 16;
}

public enum CompletedDisplay
{
    /// <summary>"완료됨" 구역에 그대로 (회색 + 취소선).</summary>
    Show,

    /// <summary>더 흐리게 (투명도 50%).</summary>
    Dim,

    /// <summary>목록에서 숨김 (완료됨 화면에서만 보임).</summary>
    Hide,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>자동 백업 (설계 §5.6).</summary>
public sealed class BackupSettings
{
    /// <summary>백업 폴더. null이면 데이터 폴더의 Backups\. 데이터 폴더 안이면 상대 경로로 저장한다.</summary>
    public string? Directory { get; set; }
}

/// <summary>자동 업데이트. 새 버전이 있으면 알리기만 하고, 설치는 항상 사용자가 확인한 뒤에 한다.</summary>
public sealed class UpdateSettings
{
    /// <summary>하루 한 번 새 버전을 확인한다 (GitHub 릴리스). 끄면 설정의 "지금 확인"으로만.</summary>
    public bool CheckAutomatically { get; set; } = true;
}

/// <summary>바탕화면 위젯 (M8). 위치는 app_state에 저장한다.</summary>
public sealed class WidgetSettings
{
    public bool Enabled { get; set; }
    public double Opacity { get; set; } = 0.92;
}

/// <summary>주간업무보고에서 항목 머리글 옆에 붙이는 날짜 범위.</summary>
public enum ReportDateRange
{
    /// <summary>날짜 없음 (예: "이슈사항").</summary>
    None,

    /// <summary>이번 주 월~금 (예: "금주실적(10/26 ~ 10/30)").</summary>
    ThisWeek,

    /// <summary>다음 주 월~금 (예: "차주계획(11/2 ~ 11/6)").</summary>
    NextWeek,
}

/// <summary>주간업무보고의 한 항목 (금주실적, 차주계획, 이슈사항 등). 보고 화면에서 항목마다 칸이 따로 있다.</summary>
public sealed class ReportSection
{
    public ReportSection()
    {
    }

    public ReportSection(string name, ReportDateRange dates = ReportDateRange.None)
    {
        Name = name;
        Dates = dates;
    }

    public string Name { get; set; } = "";

    public ReportDateRange Dates { get; set; }

    /// <summary>끄면 보고 화면과 새 보고에서 빠진다. 이미 쓴 내용이 있는 보고에는 그대로 남는다.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>예전 이름. 이름을 바꾸기 전에 저장한 보고도 이 항목으로 읽는다.</summary>
    public List<string> Aliases { get; set; } = [];
}

/// <summary>
/// 주간업무보고 양식 (설정 → 주간업무보고). 제목의 <c>{년} {월} {주차} {금주} {차주}</c>는 주차에서 채운다.
/// </summary>
public sealed class WeeklyReportSettings
{
    public const string DefaultTitle = "주간업무 보고 {월}월 {주차}주차";

    public string Title { get; set; } = DefaultTitle;

    public List<ReportSection> Sections { get; set; } = DefaultSections();

    public static List<ReportSection> DefaultSections() =>
    [
        new("금주실적", ReportDateRange.ThisWeek),
        new("차주계획", ReportDateRange.NextWeek),
        new("이슈사항"),
        new("문의사항"),
    ];

    /// <summary>보고 화면에 보이는 항목 (켜 둔 것, 순서대로).</summary>
    public IEnumerable<ReportSection> ActiveSections() => Sections.Where(s => s.Enabled);

    /// <summary>빈 제목, 빈·겹치는 항목 이름을 바로잡는다. 항목이 없으면 기본 항목으로, 모두 꺼져 있으면 첫 항목을 켠다.</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = DefaultTitle;
        }
        Title = Title.Trim();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<ReportSection>();
        foreach (var section in Sections ?? [])
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Name) || !names.Add(section.Name.Trim()))
            {
                continue;
            }
            section.Name = section.Name.Trim();
            section.Aliases = [.. (section.Aliases ?? []).Select(a => a?.Trim() ?? "")
                .Where(a => a.Length > 0 && a != section.Name).Distinct(StringComparer.Ordinal)];
            if (!Enum.IsDefined(section.Dates))
            {
                section.Dates = ReportDateRange.None;
            }
            sections.Add(section);
        }
        // 예전 이름이 다른 항목의 지금 이름과 같으면 그 항목의 머리글을 잘못 가져가므로 뺀다.
        foreach (var section in sections)
        {
            section.Aliases.RemoveAll(names.Contains);
        }
        if (sections.Count == 0)
        {
            sections = DefaultSections();
        }
        else if (!sections.Any(s => s.Enabled))
        {
            sections[0].Enabled = true;
        }
        Sections = sections;
    }
}
