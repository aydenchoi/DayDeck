using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class EventRepositoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly EventRepository _events;
    private readonly TaskRepository _tasks;
    private readonly ReminderRepository _reminders;
    private readonly ReminderSettings _settings = new() { DailyBriefingEnabled = false };

    public EventRepositoryTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero)); // 수요일
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        _events = new EventRepository(db, _time);
        _tasks = new TaskRepository(db, _time);
        _reminders = new ReminderRepository(db, _time, () => _settings);
    }

    public void Dispose() => _dir.Dispose();

    private static readonly DateOnly Wed = new(2026, 10, 28);

    private long AddTimed(string title, DateTime start, int minutes, string? rrule = null) =>
        _events.Add(new CalendarEvent { Title = title, Start = start, End = start.AddMinutes(minutes), RRule = rrule });

    [Fact]
    public void AddAndGet_TimedAndAllDay()
    {
        var timed = AddTimed("팀 회의", new DateTime(2026, 10, 28, 14, 0, 0), 60);
        var allDay = _events.Add(new CalendarEvent { Title = "제주 여행", IsAllDay = true, Start = new(2026, 11, 3, 15, 0, 0), End = new(2026, 11, 5) });

        Assert.Equal(new DateTime(2026, 10, 28, 15, 0, 0), _events.Get(timed)!.End);
        var trip = _events.Get(allDay)!;
        Assert.True(trip.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 3), trip.Start);
        Assert.Equal(new DateTime(2026, 11, 5), trip.End);
    }

    [Fact]
    public void GetOccurrences_FiltersByRangeIncludingMultiDay()
    {
        _events.Add(new CalendarEvent { Title = "마감 주간", IsAllDay = true, Start = new(2026, 10, 26), End = new(2026, 10, 30) });
        AddTimed("오늘", new DateTime(2026, 10, 28, 14, 0, 0), 60);
        AddTimed("다음 주", new DateTime(2026, 11, 4, 14, 0, 0), 60);
        AddTimed("밤샘", new DateTime(2026, 10, 27, 23, 0, 0), 120);

        var today = _events.GetOccurrences(Wed, Wed);

        Assert.Equal(["마감 주간", "밤샘", "오늘"], today.Select(o => o.Title));
        Assert.True(today[0].IsMultiDay);
    }

    [Fact]
    public void TimedEventEndingAtMidnight_DoesNotSpillIntoNextDay()
    {
        AddTimed("저녁", new DateTime(2026, 10, 27, 22, 0, 0), 120);

        Assert.Empty(_events.GetOccurrences(Wed, Wed));
    }

    [Fact]
    public void RecurringEvent_ExpandsInRange()
    {
        AddTimed("스탠드업", new DateTime(2026, 10, 26, 9, 0, 0), 15, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");

        var week = _events.GetOccurrences(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1));

        Assert.Equal(5, week.Count);
        Assert.All(week, o => Assert.True(o.IsRecurring));
        Assert.Equal(new DateTime(2026, 10, 30, 9, 0, 0), week[^1].Start);
    }

    [Fact]
    public void ThisOccurrenceOnly_MoveCompleteDelete()
    {
        var id = AddTimed("스탠드업", new DateTime(2026, 10, 26, 9, 0, 0), 15, "FREQ=DAILY");
        var tue = new DateTime(2026, 10, 27, 9, 0, 0);
        var wed = new DateTime(2026, 10, 28, 9, 0, 0);
        var thu = new DateTime(2026, 10, 29, 9, 0, 0);

        _events.UpdateOccurrence(id, tue, tue.AddHours(2), tue.AddHours(2).AddMinutes(15), "스탠드업 (늦게)");
        _events.SetOccurrenceCompleted(id, wed, true);
        _events.DeleteOccurrence(id, thu);

        var list = _events.GetOccurrences(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 30));
        Assert.Equal(4, list.Count);
        var moved = list.Single(o => o.OriginalStart == tue);
        Assert.Equal(new DateTime(2026, 10, 27, 11, 0, 0), moved.Start);
        Assert.Equal("스탠드업 (늦게)", moved.Title);
        Assert.True(moved.IsException);
        Assert.True(list.Single(o => o.OriginalStart == wed).IsCompleted);
        Assert.DoesNotContain(list, o => o.OriginalStart == thu);
    }

    [Fact]
    public void UpdateFollowing_SplitsSeries()
    {
        var id = AddTimed("주간회의", new DateTime(2026, 10, 5, 10, 0, 0), 60, "FREQ=WEEKLY");
        var original = _events.Get(id)!;
        var changed = original.Clone();
        changed.Start = new DateTime(2026, 10, 26, 15, 0, 0);
        changed.End = changed.Start.AddHours(1);

        var newId = _events.UpdateFollowing(id, new DateTime(2026, 10, 26, 10, 0, 0), changed);

        Assert.NotEqual(id, newId);
        var all = _events.GetOccurrences(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 10));
        Assert.Equal([10, 10, 10, 15, 15, 15], all.Select(o => o.Start.Hour));
    }

    [Fact]
    public void UpdateFollowing_KeepsTotalCount()
    {
        var id = AddTimed("PT", new DateTime(2026, 10, 1, 19, 0, 0), 60, "FREQ=DAILY;COUNT=10");
        var changed = _events.Get(id)!.Clone();
        changed.Start = new DateTime(2026, 10, 5, 20, 0, 0);
        changed.End = changed.Start.AddHours(1);

        _events.UpdateFollowing(id, new DateTime(2026, 10, 5, 19, 0, 0), changed);

        Assert.Equal(10, _events.GetOccurrences(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)).Count);
    }

    [Fact]
    public void DeleteFollowing_EndsSeries()
    {
        var id = AddTimed("PT", new DateTime(2026, 10, 1, 19, 0, 0), 60, "FREQ=DAILY");

        _events.DeleteFollowing(id, new DateTime(2026, 10, 4, 19, 0, 0));

        Assert.Equal(3, _events.GetOccurrences(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)).Count);
    }

    [Fact]
    public void DeleteAndRestore()
    {
        var id = AddTimed("회의", new DateTime(2026, 10, 28, 14, 0, 0), 60);
        _events.Delete(id);
        Assert.Empty(_events.GetOccurrences(Wed, Wed));
        _events.Restore(id);
        Assert.Single(_events.GetOccurrences(Wed, Wed));
    }

    [Fact]
    public void Events_DoNotAppearInTodoLists()
    {
        AddTimed("회의", new DateTime(2026, 10, 28, 14, 0, 0), 60);

        Assert.Empty(_tasks.GetToday());
        Assert.Empty(_tasks.GetUpcoming());
    }

    [Fact]
    public void EventReminder_UsesStartTime()
    {
        var id = AddTimed("회의", new DateTime(2026, 10, 28, 14, 0, 0), 60);
        var reminder = _reminders.AddRelative(id, -10);

        Assert.Equal(new DateTime(2026, 10, 28, 13, 50, 0), _reminders.Get(reminder)!.NextFireAt);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 13, 50, 0, TimeSpan.Zero));
        var due = Assert.Single(_reminders.GetDue(_time.GetLocalNow().DateTime));
        Assert.True(due.IsEvent);
        Assert.Equal(new TimeOnly(14, 0), due.DueTime);
    }

    [Fact]
    public void RecurringEventReminder_AdvancesToNextOccurrence()
    {
        var id = AddTimed("스탠드업", new DateTime(2026, 10, 26, 9, 0, 0), 15, "FREQ=DAILY");
        var reminderId = _reminders.AddRelative(id, -5);
        Assert.Equal(new DateTime(2026, 10, 28, 8, 55, 0), _reminders.Get(reminderId)!.NextFireAt);

        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 8, 55, 0, TimeSpan.Zero));
        _reminders.MarkFired(reminderId, _time.GetLocalNow().DateTime);
        _reminders.RecomputeAll();

        Assert.Equal(new DateTime(2026, 10, 29, 8, 55, 0), _reminders.Get(reminderId)!.NextFireAt);
    }

    [Fact]
    public void RecurringEventReminder_MissedWhileOff_FiresOnceThenAdvances()
    {
        var id = AddTimed("스탠드업", new DateTime(2026, 10, 26, 9, 0, 0), 15, "FREQ=DAILY");
        var reminderId = _reminders.AddRelative(id, -5);

        // 3일 동안 꺼져 있었다
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        _reminders.RecomputeAll();
        var now = _time.GetLocalNow().DateTime;
        var due = Assert.Single(_reminders.GetDue(now));
        Assert.True(due.IsMissed);
        Assert.Equal(new DateTime(2026, 10, 31, 9, 0, 0), due.DueDate!.Value.ToDateTime(due.DueTime!.Value));

        _reminders.MarkFired(reminderId, now);
        _reminders.RecomputeAll();
        Assert.Empty(_reminders.GetDue(now));
        Assert.Equal(new DateTime(2026, 11, 1, 8, 55, 0), _reminders.Get(reminderId)!.NextFireAt);
    }

    [Fact]
    public void AllDayEventReminder_UsesConfiguredMorningTime()
    {
        _settings.DateOnlyReminderTime = "08:30";
        var id = _events.Add(new CalendarEvent { Title = "엄마 생신", IsAllDay = true, Start = new(2026, 10, 29), End = new(2026, 10, 29) });
        var reminderId = _reminders.AddRelative(id, 0);

        Assert.Equal(new DateTime(2026, 10, 29, 8, 30, 0), _reminders.Get(reminderId)!.NextFireAt);
    }
}
