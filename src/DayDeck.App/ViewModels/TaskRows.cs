using System.Globalization;
using System.Windows.Media;
using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.App.ViewModels;

/// <summary>목록 화면의 한 줄. 구역 머리글 또는 할 일.</summary>
public abstract class TaskListRow : ObservableObject
{
    public abstract bool IsHeader { get; }

    /// <summary>화면 읽기 프로그램과 UI 자동화에 쓰는 이름.</summary>
    public abstract string AutomationName { get; }

    /// <summary>다시 그릴 필요가 있는지 비교하는 데 쓰는 값.</summary>
    public abstract string Signature { get; }
}

public enum SectionTone
{
    Normal,
    Alert,
    Done,
}

public sealed class SectionRow : TaskListRow
{
    private bool _isCollapsed;

    public SectionRow(string key, string title, int count, SectionTone tone, bool isCollapsible, bool isCollapsed)
    {
        Key = key;
        Title = title;
        Count = count;
        Tone = tone;
        IsCollapsible = isCollapsible;
        _isCollapsed = isCollapsed;
    }

    public string Key { get; }
    public string Title { get; }
    public int Count { get; }
    public SectionTone Tone { get; }
    public bool IsCollapsible { get; }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (SetProperty(ref _isCollapsed, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public string Chevron => !IsCollapsible ? "" : IsCollapsed ? "▸" : "▾";
    public string Header => Tone == SectionTone.Alert ? $"⚠ {Title} {Count}" : $"{Title} {Count}";
    public override bool IsHeader => true;
    public override string AutomationName => Header;
    public override string Signature => $"H|{Key}|{Count}|{IsCollapsed}";
}

public sealed class TaskRowViewModel : TaskListRow
{
    private static readonly Brush DefaultCheckBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    // 우선순위 색은 테마 토큰을 따른다 (행은 새로 고칠 때마다 다시 만든다).
    private static Brush HighBrush => Services.ThemeService.Brush("DangerBrush");
    private static Brush MediumBrush => Services.ThemeService.Brush("WarnBrush");
    private static Brush LowBrush => Services.ThemeService.Brush("InfoBrush");

    public TaskRowViewModel(TaskItem item, DateTime now, bool showMyDayMark)
    {
        Item = item;
        State = TaskRules.GetState(item, now);

        var meta = new List<string>();
        if (TaskFormatting.FormatDue(item, now) is { } due)
        {
            meta.Add(due);
        }
        if (item.SubtaskCount > 0)
        {
            meta.Add($"하위 할 일 {item.SubtaskDoneCount}/{item.SubtaskCount}");
        }
        if (item.NextReminderAt is { } reminder && item.Status == ItemStatus.Open)
        {
            var reminderDate = DateOnly.FromDateTime(reminder);
            var today = DateOnly.FromDateTime(now);
            meta.Add(reminderDate == today
                ? $"🔔 {TaskFormatting.FormatTime(TimeOnly.FromDateTime(reminder))}"
                : $"🔔 {TaskFormatting.FormatDate(reminderDate, today)} {TaskFormatting.FormatTime(TimeOnly.FromDateTime(reminder))}");
        }
        if (item.IsRecurring)
        {
            meta.Add("↻ " + (item.Rule?.Describe(item.DueDate ?? DateOnly.FromDateTime(now)) ?? "반복"));
        }
        if (item.Tags.Count > 0)
        {
            meta.Add(string.Join(" ", item.Tags.Select(t => "@" + t)));
        }
        if (showMyDayMark && item.MyDay == DateOnly.FromDateTime(now))
        {
            meta.Add("☀ 오늘 할 일");
        }
        if (!string.IsNullOrWhiteSpace(item.Notes))
        {
            meta.Add("📝");
        }
        if (item.Status != ItemStatus.Open && item.CompletedAt is { } completed)
        {
            meta.Add($"{TaskFormatting.FormatDate(DateOnly.FromDateTime(completed), DateOnly.FromDateTime(now))} " +
                     $"{TaskFormatting.FormatTime(TimeOnly.FromDateTime(completed))} {(item.Status == ItemStatus.Cancelled ? "취소" : "완료")}");
        }
        Meta = string.Join(" · ", meta);

        ListBrush = TryParseBrush(item.ListColor) ?? DefaultCheckBrush;
    }

    public TaskItem Item { get; }
    public long Id => Item.Id;

    /// <summary>설정 "완료 항목 흐리게"일 때 완료 구역의 행.</summary>
    public bool IsDimmed { get; init; }
    public string Title => Item.Title;
    public TaskDisplayState State { get; }
    public string Meta { get; }
    public bool HasMeta => Meta.Length > 0;
    public string? ListName => Item.ListName;
    public bool HasList => !string.IsNullOrEmpty(Item.ListName);
    public Brush ListBrush { get; }
    public string PriorityText => TaskFormatting.FormatPriority(Item.Priority);

    public Brush PriorityBrush => Item.Priority switch
    {
        Priority.High => HighBrush,
        Priority.Medium => MediumBrush,
        Priority.Low => LowBrush,
        _ => DefaultCheckBrush,
    };

    public override bool IsHeader => false;
    public override string AutomationName => Title;
    public override string Signature => $"T|{Id}|{State}|{Title}|{Meta}|{Item.Priority}|{ListName}|{IsDimmed}";

    internal static Brush? TryParseBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }

        try
        {
            return Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>할 일 화면(오늘/기한 지남/완료됨)에 함께 보여 주는 달력 일정 한 줄.</summary>
public sealed class EventRowViewModel : TaskListRow
{
    /// <param name="showDate">여러 날을 보여 주는 화면이면 시간 앞에 날짜를 붙인다.</param>
    public EventRowViewModel(EventOccurrence occurrence, DateTime now, bool showDate = false)
    {
        Occurrence = occurrence;
        var day = occurrence.FirstDay.ToString("M/d", CultureInfo.InvariantCulture);
        When = occurrence.IsAllDay
            ? occurrence.IsMultiDay ? $"종일 · {occurrence.FirstDay:M/d}–{occurrence.LastDay:M/d}" : showDate ? $"{day} 종일" : "종일"
            : showDate
                ? $"{day} {TaskFormatting.FormatTime(TimeOnly.FromDateTime(occurrence.Start))}"
                : $"{TaskFormatting.FormatTime(TimeOnly.FromDateTime(occurrence.Start))} – {TaskFormatting.FormatTime(TimeOnly.FromDateTime(occurrence.End))}";
        IsPastAndOpen = occurrence.IsPastAndOpen(now);
        var color = TaskRowViewModel.TryParseBrush(occurrence.Event.Color) ?? TaskRowViewModel.TryParseBrush(occurrence.Event.ListColor);
        ColorBrush = color ?? TaskRowViewModel.TryParseBrush("#2B6CD4")!;
        Detail = string.Join(" · ", new[] { occurrence.Event.Location, occurrence.IsRecurring ? "↻ 반복" : null }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public EventOccurrence Occurrence { get; }
    public string Title => Occurrence.Title;
    public string When { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public bool IsCompleted => Occurrence.IsCompleted;
    public bool IsPastAndOpen { get; }
    public Brush ColorBrush { get; }
    public override bool IsHeader => false;
    public override string AutomationName => Title;
    public override string Signature => $"E|{Occurrence.Event.Id}|{Occurrence.Start:O}|{Title}|{IsCompleted}|{IsPastAndOpen}";
}
