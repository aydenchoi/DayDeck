using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Data;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class ReminderTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ReminderSettings _settings = new() { DailyBriefingEnabled = false };
    private readonly TaskRepository _tasks;
    private readonly ReminderRepository _reminders;
    private readonly AppStateStore _state;
    private readonly ReminderEngine _engine;
    private readonly List<IReadOnlyList<DueReminder>> _fired = [];
    private readonly List<Briefing> _briefings = [];

    public ReminderTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.Zero));
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        _tasks = new TaskRepository(db, _time);
        _reminders = new ReminderRepository(db, _time, () => _settings);
        _state = new AppStateStore(db);
        _engine = new ReminderEngine(_reminders, _tasks, _state, () => _settings, _time, context: null);
        _engine.RemindersDue += (_, due) => _fired.Add(due);
        _engine.BriefingDue += (_, b) => _briefings.Add(b);

        // 앱과 같은 연결: 할 일이 바뀌면 다시 계산, 알림이 바뀌면 다시 예약
        _tasks.Changed += (_, _) => _reminders.RecomputeAll();
        _reminders.Changed += (_, _) => _engine.Reschedule();
    }

    public void Dispose()
    {
        _engine.Dispose();
        _dir.Dispose();
    }

    private DateTime Now => _time.GetLocalNow().DateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    private long TaskDueAt(int hour, int minute = 0) =>
        _tasks.Add(new TaskItem { Title = "팀 회의", DueDate = Today, DueTime = new TimeOnly(hour, minute) });

    [Fact]
    public void RelativeReminder_FiresAtOffsetBeforeDue()
    {
        var item = TaskDueAt(14);
        _reminders.AddRelative(item, -10);
        _engine.Start();

        _time.Advance(TimeSpan.FromMinutes(49));
        Assert.Empty(_fired);

        _time.Advance(TimeSpan.FromMinutes(1));
        var due = Assert.Single(Assert.Single(_fired));
        Assert.Equal(item, due.ItemId);
        Assert.Equal(new DateTime(2026, 10, 28, 13, 50, 0), due.FireAt);
        Assert.False(due.IsMissed);
    }

    [Fact]
    public void Reminder_FiresOnlyOnce()
    {
        _reminders.AddRelative(TaskDueAt(14), 0);
        _engine.Start();

        _time.Advance(TimeSpan.FromHours(3));

        Assert.Single(_fired);
    }

    [Fact]
    public void DateOnlyDue_UsesConfiguredTime()
    {
        _settings.DateOnlyReminderTime = "15:30";
        var item = _tasks.Add(new TaskItem { Title = "보고서", DueDate = Today });
        var id = _reminders.AddRelative(item, 0);

        Assert.Equal(new DateTime(2026, 10, 28, 15, 30, 0), _reminders.Get(id)!.NextFireAt);
    }

    [Fact]
    public void ChangingDue_ReschedulesReminder()
    {
        var item = TaskDueAt(14);
        var id = _reminders.AddRelative(item, -10);
        var task = _tasks.Get(item)!;
        task.DueTime = new TimeOnly(16, 0);

        _tasks.Update(task);

        Assert.Equal(new DateTime(2026, 10, 28, 15, 50, 0), _reminders.Get(id)!.NextFireAt);
    }

    [Fact]
    public void ChangingDueIntoThePast_DoesNotFire()
    {
        var item = TaskDueAt(14);
        var id = _reminders.AddRelative(item, -10);
        var task = _tasks.Get(item)!;
        task.DueTime = new TimeOnly(9, 0);

        _tasks.Update(task);
        _engine.Start();

        Assert.Null(_reminders.Get(id)!.NextFireAt);
        Assert.Empty(_fired);
    }

    [Fact]
    public void CompletedTask_DoesNotFire_AndReopeningRearms()
    {
        var item = TaskDueAt(14);
        var id = _reminders.AddRelative(item, -10);
        _engine.Start();

        _tasks.SetCompleted(item, true);
        Assert.Null(_reminders.Get(id)!.NextFireAt);
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(_fired);

        _tasks.SetCompleted(item, false);
        Assert.Equal(new DateTime(2026, 10, 28, 13, 50, 0), _reminders.Get(id)!.NextFireAt);
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Single(_fired);
    }

    [Fact]
    public void DeletedTask_DoesNotFire()
    {
        var item = TaskDueAt(14);
        _reminders.AddRelative(item, -10);
        _engine.Start();

        _tasks.Delete(item);
        _time.Advance(TimeSpan.FromHours(2));

        Assert.Empty(_fired);
    }

    [Fact]
    public void MissedWhileAppWasClosed_FiresAsMissedOnStart()
    {
        _reminders.AddRelative(TaskDueAt(14), -10);
        // 앱이 꺼져 있는 동안 시간이 지남 (엔진 시작 전)
        _time.Advance(TimeSpan.FromHours(2));

        _engine.Start();

        var due = Assert.Single(Assert.Single(_fired));
        Assert.True(due.IsMissed);
    }

    [Fact]
    public void RecomputeAfterRestart_KeepsMissedReminder()
    {
        var id = _reminders.AddRelative(TaskDueAt(14), -10);
        _time.Advance(TimeSpan.FromHours(2));

        // 다시 시작하면 먼저 전체 재계산을 한다. 마감이 그대로면 놓친 알림을 지우면 안 된다.
        _reminders.RecomputeAll();

        Assert.NotNull(_reminders.Get(id)!.NextFireAt);
    }

    [Fact]
    public void Snooze_FiresAgainLater()
    {
        _reminders.AddRelative(TaskDueAt(14), 0);
        _engine.Start();
        _time.Advance(TimeSpan.FromHours(1));
        var fired = Assert.Single(Assert.Single(_fired));

        _reminders.Snooze(fired.ReminderId, Now.AddMinutes(10));
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Single(_fired);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public void PersistentReminder_RepeatsUntilDismissed()
    {
        _settings.PersistentRepeatMinutes = 5;
        _reminders.AddRelative(TaskDueAt(14), 0, persistent: true);
        _engine.Start();

        _time.Advance(TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMinutes(5));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(3, _fired.Count);

        _reminders.Dismiss(_fired[0][0].ReminderId);
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(3, _fired.Count);
    }

    [Fact]
    public void AbsoluteReminder_WorksWithoutDueDate()
    {
        var item = _tasks.Add(new TaskItem { Title = "전화하기" });
        _reminders.AddAbsolute(item, Now.AddMinutes(30));
        _engine.Start();

        _time.Advance(TimeSpan.FromMinutes(30));

        Assert.Single(_fired);
    }

    [Fact]
    public void Pause_HoldsRemindersAndDeliversThemAsMissed()
    {
        _reminders.AddRelative(TaskDueAt(14), -10);
        _engine.Start();
        _engine.Pause(Now.AddHours(2));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Empty(_fired);

        _time.Advance(TimeSpan.FromHours(1));
        var due = Assert.Single(Assert.Single(_fired));
        Assert.True(due.IsMissed);
        Assert.Null(_engine.PausedUntil);
    }

    [Fact]
    public void AddDefaultsIfNone_UsesSettingsOnce()
    {
        _settings.DefaultOffsets = [-10, -60];
        var item = TaskDueAt(16);

        Assert.True(_reminders.AddDefaultsIfNone(item));
        Assert.False(_reminders.AddDefaultsIfNone(item));
        Assert.Equal([-60, -10], _reminders.GetForItem(item).Select(r => r.OffsetMinutes!.Value));

        _settings.AutoAddDefault = false;
        Assert.False(_reminders.AddDefaultsIfNone(TaskDueAt(17)));
    }

    [Fact]
    public void DailyBriefing_FiresOncePerDayAtConfiguredTime()
    {
        _settings.DailyBriefingEnabled = true;
        _settings.DailyBriefingTime = "14:00";
        _tasks.Add(new TaskItem { Title = "보고서", DueDate = Today.AddDays(-1) });
        _tasks.Add(new TaskItem { Title = "장보기", MyDay = Today });
        _engine.Start();
        Assert.Empty(_briefings);

        _time.Advance(TimeSpan.FromHours(1));
        var briefing = Assert.Single(_briefings);
        Assert.Equal(2, briefing.TodayOpen);
        Assert.Equal(1, briefing.Overdue);
        Assert.Equal("보고서", briefing.TopItems[0].Title);

        _time.Advance(TimeSpan.FromHours(5));
        Assert.Single(_briefings);

        _time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, _briefings.Count);
    }

    [Fact]
    public void DailyBriefing_ShownAtStartIfMissedToday()
    {
        _settings.DailyBriefingEnabled = true;
        _settings.DailyBriefingTime = "09:00";

        _engine.Start();
        Assert.Single(_briefings);

        // 같은 날 다시 시작해도 다시 보이지 않는다 (날짜는 DB에 저장)
        using var engine2 = new ReminderEngine(_reminders, _tasks, _state, () => _settings, _time, context: null);
        var count = 0;
        engine2.BriefingDue += (_, _) => count++;
        engine2.Start();
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData(0, "정시")]
    [InlineData(-10, "10분 전")]
    [InlineData(-90, "1시간 30분 전")]
    [InlineData(-1440, "1일 전")]
    [InlineData(15, "15분 후")]
    public void Describe_Offsets(int offset, string expected)
    {
        Assert.Equal(expected, ReminderRules.Describe(new Reminder { OffsetMinutes = offset }, Today));
    }
}
