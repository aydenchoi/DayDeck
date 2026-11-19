using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.QuickAdd;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class QuickAddCommandTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 28, 10, 0, 0);
    private readonly TempDirectory _dir = new();
    private readonly TaskRepository _tasks;
    private readonly EventRepository _events;
    private readonly ReminderRepository _reminders;
    private readonly QuickAddCommand _command;
    private readonly QuickAddParser _parser = new(Now);

    public QuickAddCommandTests()
    {
        var time = new FakeTimeProvider();
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        time.SetUtcNow(new DateTimeOffset(Now, TimeSpan.Zero));
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        _tasks = new TaskRepository(db, time);
        _events = new EventRepository(db, time);
        _reminders = new ReminderRepository(db, time, () => new ReminderSettings());
        _command = new QuickAddCommand(_tasks, _events, _reminders, time);
    }

    public void Dispose() => _dir.Dispose();

    private QuickAddCreated Add(string text, QuickAddContext? context = null) =>
        _command.Create(_parser.Parse(text), context ?? new QuickAddContext());

    [Fact]
    public void TaskWithoutDate_GoesToMyDay()
    {
        var created = Add("장보기");

        var task = _tasks.Get(created.Id)!;
        Assert.Equal(new DateOnly(2026, 10, 28), task.MyDay);
        Assert.Null(task.DueDate);
        Assert.Contains(task.Id, _tasks.GetToday().Select(t => t.Id));
    }

    [Fact]
    public void TaskWithDeadline_GetsDefaultReminder()
    {
        var created = Add("금요일 오후 5시까지 견적서 !높음");

        var task = _tasks.Get(created.Id)!;
        Assert.Equal(new DateOnly(2026, 10, 30), task.DueDate);
        Assert.Equal(new TimeOnly(17, 0), task.DueTime);
        Assert.Equal(Priority.High, task.Priority);
        Assert.Equal([-10], _reminders.GetForItem(task.Id).Select(r => r.OffsetMinutes!.Value));
    }

    [Fact]
    public void ListTag_UsesExistingOrCreatesList()
    {
        var work = _tasks.GetLists().Single(l => l.Name == "업무").Id;

        Assert.Equal(work, _tasks.Get(Add("보고서 #업무").Id)!.ListId);
        var newList = _tasks.Get(Add("단어 외우기 #영어").Id)!.ListId;
        Assert.Equal("영어", _tasks.GetLists().Single(l => l.Id == newList).Name);
    }

    [Fact]
    public void PageContext_AppliesWhenTextHasNone()
    {
        var list = _tasks.GetLists()[0].Id;

        var task = _tasks.Get(Add("책 반납", new QuickAddContext(ListId: list)).Id)!;

        Assert.Equal(list, task.ListId);
        Assert.Null(task.MyDay);
    }

    [Fact]
    public void RecurringEvent_WithExplicitReminder()
    {
        var created = Add("매주 월요일 9시 주간회의 10분전 알림");

        Assert.Equal(QuickAddKind.Event, created.Kind);
        var e = _events.Get(created.Id)!;
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", e.RRule);
        Assert.Equal(new DateTime(2026, 11, 2, 9, 0, 0), e.Start);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0), e.End);
        Assert.Equal([-10], _reminders.GetForItem(e.Id).Select(r => r.OffsetMinutes!.Value));
    }

    [Fact]
    public void AllDayRangeEvent()
    {
        var e = _events.Get(Add("11/3~11/5 제주 여행").Id)!;

        Assert.True(e.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 3), e.Start);
        Assert.Equal(new DateTime(2026, 11, 5), e.End);
    }

    [Theory]
    [InlineData("내일 오후 3시 치과 #개인 !높음", "📅 일정 · 내일 15:00–16:00 · #개인 · !!! 높음")]
    [InlineData("금요일까지 보고서", "☐ 할 일 · 모레까지")]
    [InlineData("장보기", "☐ 할 일 · 오늘 할 일")]
    [InlineData("매주 월요일 9시 회의 10분전 알림", "📅 일정 · 월요일 09:00–10:00 · ↻ 매주 월 · 🔔 10분 전")]
    [InlineData("11/3~11/5 여행", "📅 일정 · 화요일 – 11월 5일 (목) (종일)")]
    public void Describe(string text, string expected) => Assert.Equal(expected, _command.Describe(_parser.Parse(text)));
}
