using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DayDeck.App.Services;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views.Calendar;

/// <summary>한 화면(월/주/일/목록)을 그리는 데 필요한 데이터.</summary>
internal sealed record CalendarData(
    DateOnly From,
    DateOnly To,
    DateOnly Anchor,
    IReadOnlyList<EventOccurrence> Events,
    IReadOnlyList<TaskItem> Todos,
    IReadOnlyDictionary<DateOnly, List<Holiday>> Holidays,
    DateTime Now,
    int SlotMinutes)
{
    public DateOnly Today => DateOnly.FromDateTime(Now);

    public IEnumerable<EventOccurrence> EventsOn(DateOnly day) => Events.Where(e => e.OccursOn(day));

    public IEnumerable<TaskItem> TodosOn(DateOnly day) => Todos.Where(t => t.DueDate == day);

    public bool IsHoliday(DateOnly day) => Holidays.ContainsKey(day);

    public string? HolidayName(DateOnly day) => Holidays.TryGetValue(day, out var list) ? string.Join(", ", list.Select(h => h.Name)) : null;
}

/// <summary>달력 보기들이 사용자 동작을 페이지에 알리는 통로.</summary>
internal interface ICalendarHost
{
    void OpenOccurrence(EventOccurrence occurrence);
    void OpenTodo(TaskItem item);
    void CreateEvent(DateTime start, DateTime end, bool allDay);
    void MoveOccurrence(EventOccurrence occurrence, DateTime start, DateTime end);
    void ToggleTodo(TaskItem item);
    void ShowDay(DateOnly day);

    /// <summary>그 날짜가 있는 달의 월 보기로 이동한다 (월 보기에서 지난달/다음 달 날짜를 눌렀을 때).</summary>
    void ShowMonth(DateOnly day);
    void ShowOccurrenceMenu(EventOccurrence occurrence, FrameworkElement target);
}

/// <summary>달력 색과 서식 (설계 §5.2: 완료는 회색+취소선+✓, 지난 미완료는 빗금+⚠).</summary>
internal static class CalendarStyle
{
    public static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    // 테마 토큰을 그릴 때마다 읽는다 (테마가 바뀌면 달력을 다시 그린다).
    public static Brush Text => ThemeService.Brush("TextBrush");
    public static Brush SubText => ThemeService.Brush("SubTextBrush");
    public static Brush Muted => ThemeService.Brush("MutedBrush");
    public static Brush Line => ThemeService.Brush("RowLineBrush");
    public static Brush Holiday => ThemeService.Brush("DangerBrush");
    public static Brush Saturday => ThemeService.Brush("InfoBrush");
    public static Brush Accent => ThemeService.Brush("AccentBrush");
    public static Brush Card => ThemeService.Brush("CardBrush");
    public static Brush TodayBackground => ThemeService.Brush("TodayBrush");
    public static Brush DoneBackground => ThemeService.Brush("DoneBgBrush");
    public static Brush DoneBorder => ThemeService.Brush("DoneBorderBrush");
    public static Brush PastOpenText => ThemeService.Brush("WarnTextBrush");
    public static Brush PastOpenBorder => ThemeService.Brush("WarnBrush");
    public static readonly Color DefaultEventColor = Color.FromRgb(0x2B, 0x6C, 0xD4);

    private static Brush? _hatch;
    private static bool _hatchDark;

    /// <summary>지난 미완료 일정의 빗금 배경.</summary>
    public static Brush Hatch
    {
        get
        {
            if (_hatch is null || _hatchDark != ThemeService.IsDark)
            {
                _hatch = CreateHatch();
                _hatchDark = ThemeService.IsDark;
            }
            return _hatch;
        }
    }

    public static Color EventColor(EventOccurrence occurrence) =>
        Parse(occurrence.Event.Color) ?? Parse(occurrence.Event.ListColor) ?? DefaultEventColor;

    public static Brush Solid(Color color) => Frozen(color);

    /// <summary>카드 바탕에 섞은 연한 색 (시간 블록 배경). 어두운 테마는 더 진하게 섞는다.</summary>
    public static Brush Tint(Color color, double amount = 0.14)
    {
        var bg = ThemeService.Color("CardBrush");
        if (ThemeService.IsDark)
        {
            amount = Math.Min(1, amount * 2.4);
        }
        return Frozen(Color.FromRgb(Mix(bg.R, color.R, amount), Mix(bg.G, color.G, amount), Mix(bg.B, color.B, amount)));
    }

    /// <summary>블록 안 글자색: 밝은 테마는 색을 어둡게, 어두운 테마는 밝게.</summary>
    public static Brush Dark(Color color) => ThemeService.IsDark
        ? Frozen(Color.FromRgb(Mix(color.R, 255, 0.55), Mix(color.G, 255, 0.55), Mix(color.B, 255, 0.55)))
        : Frozen(Color.FromRgb((byte)(color.R * 0.55), (byte)(color.G * 0.55), (byte)(color.B * 0.55)));

    private static byte Mix(byte from, byte to, double amount) => (byte)Math.Round(from + (to - from) * amount);

    public static Brush DayBrush(CalendarData data, DateOnly day) =>
        data.IsHoliday(day) || day.DayOfWeek == DayOfWeek.Sunday ? Holiday
        : day.DayOfWeek == DayOfWeek.Saturday ? Saturday
        : Text;

    public static string Time(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DayName(DayOfWeek day) => Korean.DateTimeFormat.GetAbbreviatedDayName(day);

    public static TextBlock Label(string text, double size = 12, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? Text,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>월 보기 칸/종일 줄에 넣는 한 줄짜리 일정 표시.</summary>
    public static Border EventChip(EventOccurrence occurrence, DateTime now, bool showTime)
    {
        var color = EventColor(occurrence);
        var pastOpen = occurrence.IsPastAndOpen(now);
        var text = (occurrence.IsCompleted ? "✓ " : pastOpen ? "⚠ " : "")
                   + (showTime && !occurrence.IsAllDay ? Time(occurrence.Start) + " " : "")
                   + occurrence.Title;
        var label = Label(text, 11.5);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 1, 0, 0),
            Child = label,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Tooltip(occurrence),
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, occurrence.Title);

        if (occurrence.IsCompleted)
        {
            chip.Background = DoneBackground;
            label.Foreground = Muted;
            label.TextDecorations = TextDecorations.Strikethrough;
        }
        else if (occurrence.IsAllDay)
        {
            chip.Background = pastOpen ? Hatch : Solid(color);
            label.Foreground = pastOpen ? PastOpenText : Brushes.White;
        }
        else
        {
            chip.Background = pastOpen ? Hatch : Tint(color, 0.12);
            label.Foreground = pastOpen ? PastOpenText : Dark(color);
        }
        return chip;
    }

    public static Border TodoChip(TaskItem item, DateTime now)
    {
        var done = item.Status != ItemStatus.Open;
        var overdue = TaskRules.IsOverdue(item, now);
        var text = (done ? "☑ " : overdue ? "⚠ ☐ " : "☐ ") + (item.DueTime is { } t ? TaskFormatting.FormatTime(t) + " " : "") + item.Title;
        var label = Label(text, 11.5, done ? Muted : overdue ? Holiday : SubText, overdue ? FontWeights.Bold : null);
        if (done)
        {
            label.TextDecorations = TextDecorations.Strikethrough;
        }
        var chip = new Border
        {
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 1, 0, 0),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = Card,
            Child = label,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"할 일: {item.Title}" + (item.ListName is { } list ? $" · {list}" : ""),
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, "할 일: " + item.Title);
        return chip;
    }

    public static string Tooltip(EventOccurrence occurrence)
    {
        var when = occurrence.IsAllDay
            ? occurrence.IsMultiDay ? $"{occurrence.FirstDay:M/d} – {occurrence.LastDay:M/d} (종일)" : "종일"
            : $"{Time(occurrence.Start)} – {Time(occurrence.End)}";
        var lines = new List<string> { occurrence.Title, when };
        if (occurrence.Event.Location is { Length: > 0 } location)
        {
            lines.Add("📍 " + location);
        }
        if (occurrence.Event.Rule is { } rule)
        {
            lines.Add("↻ " + rule.Describe(DateOnly.FromDateTime(occurrence.Event.Start)));
        }
        if (occurrence.IsCompleted)
        {
            lines.Add("✓ 완료");
        }
        return string.Join("\n", lines);
    }

    private static Brush CreateHatch()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(7, 7),
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
        };
        var light = ThemeService.Color("HatchLightBrush");
        var dark = ThemeService.Color("HatchDarkBrush");
        brush.GradientStops.Add(new GradientStop(light, 0));
        brush.GradientStops.Add(new GradientStop(light, 0.5));
        brush.GradientStops.Add(new GradientStop(dark, 0.5));
        brush.GradientStops.Add(new GradientStop(dark, 1));
        brush.Freeze();
        return brush;
    }

    private static Color? Parse(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
