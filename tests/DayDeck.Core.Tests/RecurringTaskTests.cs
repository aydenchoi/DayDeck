using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.QuickAdd;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

/// <summary>반복 할 일, 취소 상태, 태그.</summary>
public sealed class RecurringTaskTests : IDisposable
{
    // 2026-10-28은 수요일
    private static readonly DateOnly Today = new(2026, 10, 28);
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Database _db;
    private readonly TaskRepository _tasks;

    public RecurringTaskTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        _db = new Database(_dir.Combine("data.db"));
        _db.Migrate();
        _tasks = new TaskRepository(_db, _time);
    }

    public void Dispose() => _dir.Dispose();

    // ---------- 다음 마감 ----------

    [Theory]
    [InlineData("FREQ=DAILY", "2026-10-28", "2026-10-29")]
    [InlineData("FREQ=WEEKLY;BYDAY=FR", "2026-10-30", "2026-11-06")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TH", "2026-10-29", "2026-11-02")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", "2026-10-31", "2026-12-31")]
    [InlineData("FREQ=YEARLY", "2026-11-03", "2027-11-03")]
    // 밀린 회차는 건너뛰고 오늘 이후 첫 회차
    [InlineData("FREQ=DAILY", "2026-10-20", "2026-10-28")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", "2026-10-12", "2026-11-02")]
    public void NextDue(string rrule, string due, string expected)
    {
        var next = TaskRules.NextDue(RecurrenceRule.Parse(rrule)!, DateOnly.Parse(due), Today);
        Assert.Equal(DateOnly.Parse(expected), next);
    }

    [Fact]
    public void NextDue_AfterUntil_IsNull() =>
        Assert.Null(TaskRules.NextDue(RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=FR;UNTIL=20261101")!, new DateOnly(2026, 10, 30), Today));

    // ---------- 완료하면 다음 회차 ----------

    private long AddRecurring(string rrule = "FREQ=WEEKLY;BYDAY=FR", DateOnly? due = null, TimeOnly? time = null)
    {
        var listId = _tasks.GetLists()[0].Id;
        return _tasks.Add(new TaskItem
        {
            Title = "주간 보고",
            Notes = "팀장님께",
            ListId = listId,
            Priority = Priority.High,
            DueDate = due ?? new DateOnly(2026, 10, 30),
            DueTime = time,
            RRule = rrule,
            Tags = ["업무", "보고"],
        });
    }

    [Fact]
    public void Complete_CreatesNextOccurrence_WithSameDetails()
    {
        var id = AddRecurring(time: new TimeOnly(17, 0));
        var reminders = new ReminderRepository(_db, _time, () => new ReminderSettings());
        reminders.AddRelative(id, -30, persistent: true);
        var sub = _tasks.Add(new TaskItem { Title = "자료 모으기", ParentId = id });
        _tasks.SetCompleted(sub, true);

        _tasks.SetCompleted(id, true);

        var done = _tasks.Get(id)!;
        Assert.Equal(ItemStatus.Completed, done.Status);
        var next = _tasks.GetAll().Single(t => t.Id != id);
        Assert.Equal(ItemStatus.Open, next.Status);
        Assert.Equal("주간 보고", next.Title);
        Assert.Equal("팀장님께", next.Notes);
        Assert.Equal(Priority.High, next.Priority);
        Assert.Equal(done.ListId, next.ListId);
        Assert.Equal(new DateOnly(2026, 11, 6), next.DueDate);
        Assert.Equal(new TimeOnly(17, 0), next.DueTime);
        Assert.Equal("FREQ=WEEKLY;BYDAY=FR", next.RRule);
        Assert.Equal(["보고", "업무"], next.Tags);
        var nextReminder = Assert.Single(reminders.GetForItem(next.Id));
        Assert.Equal(-30, nextReminder.OffsetMinutes);
        Assert.True(nextReminder.IsPersistent);
        var subtask = Assert.Single(_tasks.GetSubtasks(next.Id));
        Assert.Equal("자료 모으기", subtask.Title);
        Assert.Equal(ItemStatus.Open, subtask.Status);
    }

    [Fact]
    public void CompleteTwice_DoesNotCreateTwo()
    {
        var id = AddRecurring();
        _tasks.SetCompleted(id, true);
        _tasks.SetCompleted(id, true);
        Assert.Equal(2, _tasks.GetAll().Count);
    }

    [Fact]
    public void Uncomplete_RemovesUntouchedNext()
    {
        var id = AddRecurring();
        _tasks.SetCompleted(id, true);
        _tasks.SetCompleted(id, false);
        var only = Assert.Single(_tasks.GetAll());
        Assert.Equal(ItemStatus.Open, only.Status);

        // 다시 완료하면 다시 만든다.
        _tasks.SetCompleted(id, true);
        Assert.Equal(2, _tasks.GetAll().Count);
    }

    [Fact]
    public void Uncomplete_KeepsNextThatWasEdited()
    {
        var id = AddRecurring();
        _tasks.SetCompleted(id, true);
        var next = _tasks.GetAll().Single(t => t.Id != id);
        _time.Advance(TimeSpan.FromMinutes(1));
        next.Title = "주간 보고 (수정)";
        _tasks.Update(next);

        _tasks.SetCompleted(id, false);
        Assert.Equal(2, _tasks.GetAll().Count);
    }

    [Fact]
    public void Count_DecreasesAndEnds()
    {
        var id = AddRecurring("FREQ=DAILY;COUNT=2", Today);
        _tasks.SetCompleted(id, true);
        var second = _tasks.GetAll().Single(t => t.Id != id);
        Assert.Equal("FREQ=DAILY;COUNT=1", second.RRule);
        _tasks.SetCompleted(second.Id, true);
        Assert.Equal(2, _tasks.GetAll().Count); // 마지막 회차를 마치면 더 만들지 않음
    }

    [Fact]
    public void NoDueDate_StartsFromToday()
    {
        var id = _tasks.Add(new TaskItem { Title = "물주기", RRule = "FREQ=DAILY;INTERVAL=3" });
        _tasks.SetCompleted(id, true);
        Assert.Equal(new DateOnly(2026, 10, 31), _tasks.GetAll().Single(t => t.Id != id).DueDate);
    }

    // ---------- 취소 ----------

    [Fact]
    public void Cancel_MovesToDoneArea_AndCreatesNextForRecurring()
    {
        var plain = _tasks.Add(new TaskItem { Title = "안 하기로 함", MyDay = Today });
        _tasks.SetCancelled(plain, true);
        var cancelled = _tasks.Get(plain)!;
        Assert.Equal(ItemStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Contains(_tasks.GetCompleted(), t => t.Id == plain);

        _tasks.SetCancelled(plain, false);
        Assert.Equal(ItemStatus.Open, _tasks.Get(plain)!.Status);

        var recurring = AddRecurring();
        _tasks.SetCancelled(recurring, true);
        Assert.Contains(_tasks.GetAll(), t => t.Id != recurring && t.Id != plain && t.Status == ItemStatus.Open);
    }

    [Fact]
    public void GetCompleted_Since_FiltersByCompletionDate()
    {
        var old = _tasks.Add(new TaskItem { Title = "지난달" });
        _tasks.SetCompleted(old, true);
        _time.Advance(TimeSpan.FromDays(10));
        var recent = _tasks.Add(new TaskItem { Title = "오늘" });
        _tasks.SetCompleted(recent, true);

        Assert.Equal(["오늘", "지난달"], _tasks.GetCompleted().Select(t => t.Title));
        Assert.Equal(["오늘"], _tasks.GetCompleted(since: new DateOnly(2026, 11, 7)).Select(t => t.Title));
    }

    // ---------- 드래그: 순서, 목록 옮기기, 태그 ----------

    [Fact]
    public void MoveBefore_ReordersOpenTasksInList()
    {
        var list = _tasks.AddList("정렬");
        var a = _tasks.Add(new TaskItem { Title = "A", ListId = list });
        var b = _tasks.Add(new TaskItem { Title = "B", ListId = list });
        var c = _tasks.Add(new TaskItem { Title = "C", ListId = list });
        string Order() => string.Concat(TaskRules.OrderManual(_tasks.GetByList(list).Where(t => t.Status == ItemStatus.Open)).Select(t => t.Title));

        _tasks.MoveBefore(c, a);
        Assert.Equal("CAB", Order());
        _tasks.MoveBefore(c, null);
        Assert.Equal("ABC", Order());
        _tasks.MoveBefore(a, c);
        Assert.Equal("BAC", Order());
    }

    [Fact]
    public void MoveToList_MovesSubtasksToo_AndAddTagKeepsExisting()
    {
        var target = _tasks.AddList("다른 목록");
        var id = _tasks.Add(new TaskItem { Title = "옮길 일", Tags = ["원래"] });
        var sub = _tasks.Add(new TaskItem { Title = "하위", ParentId = id });
        _tasks.MoveToList(id, target);
        Assert.Equal(target, _tasks.Get(id)!.ListId);
        Assert.Equal(target, _tasks.Get(sub)!.ListId);

        _tasks.AddTag(id, "새");
        _tasks.AddTag(id, "원래");
        Assert.Equal(["새", "원래"], _tasks.Get(id)!.Tags);
    }

    [Fact]
    public void SetListColor_ChangesListAndTaskColor()
    {
        var list = _tasks.AddList("색 바꿀 목록", "#2B6CD4");
        var task = _tasks.Add(new TaskItem { Title = "할 일", ListId = list });

        _tasks.SetListColor(list, "#c2386f");
        Assert.Equal("#C2386F", _tasks.GetLists().Single(l => l.Id == list).Color);
        Assert.Equal("#C2386F", _tasks.Get(task)!.ListColor);

        _tasks.SetListColor(list, null);
        Assert.Null(_tasks.GetLists().Single(l => l.Id == list).Color);
        Assert.Throws<ArgumentException>(() => _tasks.SetListColor(list, "red"));
    }

    // ---------- 태그 ----------

    [Fact]
    public void Tags_SetQueryAndCleanup()
    {
        var a = _tasks.Add(new TaskItem { Title = "A", Tags = ["@전화", "#급함", "전화"] });
        var b = _tasks.Add(new TaskItem { Title = "B" });
        _tasks.SetTags(b, ["급함"]);

        Assert.Equal(["급함", "전화"], _tasks.Get(a)!.Tags);
        Assert.Equal(["A", "B"], _tasks.GetByTag("급함").Select(t => t.Title).Order());
        Assert.Equal([("급함", 2), ("전화", 1)], _tasks.GetTags());

        _tasks.SetTags(a, []);
        Assert.Empty(_tasks.Get(a)!.Tags);
        Assert.Equal(["급함"], _tasks.GetTags().Select(t => t.Name)); // 안 쓰는 태그는 지움
    }

    // ---------- 빠른 추가 ----------

    private static readonly QuickAddParser Parser = new(new DateTime(2026, 10, 28, 10, 0, 0));

    [Theory]
    [InlineData("매주 금요일까지 주간보고", "T|주간보고|2026-10-30||FREQ=WEEKLY|||")]
    [InlineData("매일 까지 물 마시기", "T|물 마시기|2026-10-28||FREQ=DAILY|||")]
    [InlineData("매월 25일까지 카드값", "T|카드값|2026-11-25||FREQ=MONTHLY|||")]
    public void QuickAdd_RecurringDeadline_IsRecurringTask(string input, string expected) =>
        Assert.Equal(expected, Parser.Parse(input).Describe());

    [Fact]
    public void QuickAdd_Tags()
    {
        var result = Parser.Parse("내일 우유 사기 @마트 @장보기 #개인");
        Assert.Equal("T|우유 사기|2026-10-29|||개인||", result.Describe());
        Assert.Equal(["마트", "장보기"], result.Tags);
        Assert.Equal(2, result.Tokens.Count(t => t.Kind == QuickAddTokenKind.Tag));
    }

    [Fact]
    public void QuickAdd_EmailIsNotATag()
    {
        var result = Parser.Parse("kim@example.com 메일 회신");
        Assert.Empty(result.Tags!);
        Assert.Equal("kim@example.com 메일 회신", result.Title);
    }

    [Fact]
    public void QuickAddCommand_CreatesRecurringTaskWithTags()
    {
        var reminders = new ReminderRepository(_db, _time, () => new ReminderSettings());
        var command = new QuickAddCommand(_tasks, new EventRepository(_db, _time), reminders, _time);
        var created = command.Create(Parser.Parse("매주 금요일까지 주간보고 @보고"), new QuickAddContext());
        var task = _tasks.Get(created.Id)!;
        Assert.Equal(QuickAddKind.Task, created.Kind);
        // 요일은 마감일(금)에서 정해진다.
        Assert.Equal("FREQ=WEEKLY", task.RRule);
        Assert.Equal(["보고"], task.Tags);
        _tasks.SetCompleted(task.Id, true);
        Assert.Equal(new DateOnly(2026, 11, 6), _tasks.GetAll().Single(t => t.Id != task.Id).DueDate);
        Assert.Contains("↻", created.Summary);
        Assert.Contains("@보고", created.Summary);
    }
}
