using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Data;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class TaskRepositoryTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly TaskRepository _repo;
    private DateTime Now => _time.GetLocalNow().DateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    public TaskRepositoryTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 14, 30, 0, TimeSpan.Zero));
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        _repo = new TaskRepository(db, _time);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Migration_SeedsDefaultLists()
    {
        Assert.Equal(["개인", "업무"], _repo.GetLists().Select(l => l.Name));
    }

    [Fact]
    public void Add_ThenGet_RoundTripsAllFields()
    {
        var list = _repo.GetLists()[1];
        var id = _repo.Add(new TaskItem
        {
            Title = "  분기 보고서 제출  ", Notes = "초안 공유", ListId = list.Id, Priority = Priority.High,
            DueDate = Today, DueTime = new TimeOnly(18, 0), MyDay = Today,
        });

        var item = _repo.Get(id)!;

        Assert.Equal("분기 보고서 제출", item.Title);
        Assert.Equal("초안 공유", item.Notes);
        Assert.Equal(list.Id, item.ListId);
        Assert.Equal("업무", item.ListName);
        Assert.Equal(Priority.High, item.Priority);
        Assert.Equal(Today, item.DueDate);
        Assert.Equal(new TimeOnly(18, 0), item.DueTime);
        Assert.Equal(Today, item.MyDay);
        Assert.Equal(ItemStatus.Open, item.Status);
        Assert.Equal(Now, item.CreatedAt);
    }

    [Fact]
    public void Add_RejectsEmptyTitle()
    {
        Assert.Throws<ArgumentException>(() => _repo.Add(new TaskItem { Title = "   " }));
    }

    [Fact]
    public void Update_ChangesEditableFields()
    {
        var id = _repo.Add(new TaskItem { Title = "a", DueDate = Today });
        var item = _repo.Get(id)!;
        item.Title = "b";
        item.DueDate = null;
        item.Priority = Priority.Low;

        _repo.Update(item);

        var reloaded = _repo.Get(id)!;
        Assert.Equal("b", reloaded.Title);
        Assert.Null(reloaded.DueDate);
        Assert.Equal(Priority.Low, reloaded.Priority);
    }

    [Fact]
    public void SetCompleted_RecordsAndClearsCompletionTime()
    {
        var id = _repo.Add(new TaskItem { Title = "a" });

        _repo.SetCompleted(id, true);
        Assert.Equal(ItemStatus.Completed, _repo.Get(id)!.Status);
        Assert.Equal(Now, _repo.Get(id)!.CompletedAt);

        _repo.SetCompleted(id, false);
        Assert.Equal(ItemStatus.Open, _repo.Get(id)!.Status);
        Assert.Null(_repo.Get(id)!.CompletedAt);
    }

    [Fact]
    public void SmartLists_MatchRules()
    {
        var overdue = _repo.Add(new TaskItem { Title = "overdue", DueDate = Today.AddDays(-1) });
        var overdueToday = _repo.Add(new TaskItem { Title = "overdue today", DueDate = Today, DueTime = new TimeOnly(9, 0) });
        var dueToday = _repo.Add(new TaskItem { Title = "due today", DueDate = Today });
        var myDay = _repo.Add(new TaskItem { Title = "my day", MyDay = Today });
        var nextWeek = _repo.Add(new TaskItem { Title = "next week", DueDate = Today.AddDays(5) });
        var farAway = _repo.Add(new TaskItem { Title = "far", DueDate = Today.AddDays(30) });
        var doneToday = _repo.Add(new TaskItem { Title = "done today", DueDate = Today });
        _repo.SetCompleted(doneToday, true);
        _repo.Add(new TaskItem { Title = "no due" });

        Assert.Equal(new[] { overdue, overdueToday, dueToday, myDay, doneToday }.Order(), _repo.GetToday().Select(i => i.Id).Order());
        Assert.Equal([overdue, overdueToday], _repo.GetOverdue().Select(i => i.Id));
        Assert.Equal([dueToday, nextWeek], _repo.GetUpcoming().Select(i => i.Id));
        Assert.Equal([doneToday], _repo.GetCompleted().Select(i => i.Id));
        Assert.DoesNotContain(farAway, _repo.GetToday().Select(i => i.Id));

        var counts = _repo.GetCounts();
        Assert.Equal(4, counts.TodayOpen);
        Assert.Equal(1, counts.TodayDone);
        Assert.Equal(2, counts.Overdue);
    }

    [Fact]
    public void RangeDays_LimitUpcomingAndOverdue()
    {
        var weekAgo = _repo.Add(new TaskItem { Title = "7일 전", DueDate = Today.AddDays(-7) });
        var tenDaysAgo = _repo.Add(new TaskItem { Title = "10일 전", DueDate = Today.AddDays(-10) });
        var monthAgo = _repo.Add(new TaskItem { Title = "30일 전", DueDate = Today.AddDays(-30) });
        var inFive = _repo.Add(new TaskItem { Title = "5일 뒤", DueDate = Today.AddDays(5) });
        var inTen = _repo.Add(new TaskItem { Title = "10일 뒤", DueDate = Today.AddDays(10) });
        var inTwenty = _repo.Add(new TaskItem { Title = "20일 뒤", DueDate = Today.AddDays(20) });

        Assert.Equal([inFive], _repo.GetUpcoming(7).Select(i => i.Id));
        Assert.Equal([inFive, inTen], _repo.GetUpcoming(14).Select(i => i.Id));
        Assert.DoesNotContain(inTwenty, _repo.GetUpcoming(14).Select(i => i.Id));

        Assert.Equal([weekAgo], _repo.GetOverdue(7).Select(i => i.Id));
        Assert.Equal(new[] { tenDaysAgo, weekAgo }.Order(), _repo.GetOverdue(14).Select(i => i.Id).Order());
        Assert.Equal(3, _repo.GetOverdue().Count); // 기간 없이: 전부
        Assert.Contains(monthAgo, _repo.GetOverdue().Select(i => i.Id));

        Assert.Equal(1, _repo.GetCounts().Upcoming);
        Assert.Equal(2, _repo.GetCounts(upcomingDays: 14).Upcoming);
        Assert.Equal(3, _repo.GetCounts(upcomingDays: 14).Overdue);
    }

    [Fact]
    public void Today_DropsYesterdaysCompletedAndMyDayAfterMidnight()
    {
        var myDay = _repo.Add(new TaskItem { Title = "my day", MyDay = Today });
        var done = _repo.Add(new TaskItem { Title = "done", DueDate = Today });
        _repo.SetCompleted(done, true);

        _time.Advance(TimeSpan.FromDays(1));

        Assert.Empty(_repo.GetToday());
        Assert.Equal(myDay, _repo.Get(myDay)!.Id);
    }

    [Fact]
    public void Subtasks_AreCountedAndExcludedFromSmartLists()
    {
        var parent = _repo.Add(new TaskItem { Title = "parent", MyDay = Today });
        _repo.Add(new TaskItem { Title = "child 1", ParentId = parent, MyDay = Today });
        var child2 = _repo.Add(new TaskItem { Title = "child 2", ParentId = parent });
        _repo.SetCompleted(child2, true);

        var today = _repo.GetToday();

        var item = Assert.Single(today);
        Assert.Equal(2, item.SubtaskCount);
        Assert.Equal(1, item.SubtaskDoneCount);
        Assert.Equal(["child 1", "child 2"], _repo.GetSubtasks(parent).Select(s => s.Title));
    }

    [Fact]
    public void DeleteAndRestore_IncludesSubtasks()
    {
        var parent = _repo.Add(new TaskItem { Title = "parent", MyDay = Today });
        var child = _repo.Add(new TaskItem { Title = "child", ParentId = parent });

        _repo.Delete(parent);
        Assert.Empty(_repo.GetToday());
        Assert.Empty(_repo.GetSubtasks(parent));

        _repo.Restore(parent);
        Assert.Single(_repo.GetToday());
        Assert.Equal([child], _repo.GetSubtasks(parent).Select(s => s.Id));
    }

    [Fact]
    public void Restore_DoesNotResurrectSubtaskDeletedEarlier()
    {
        var parent = _repo.Add(new TaskItem { Title = "parent" });
        var child = _repo.Add(new TaskItem { Title = "child", ParentId = parent });
        _repo.Delete(child);
        _time.Advance(TimeSpan.FromMinutes(1));
        _repo.Delete(parent);

        _repo.Restore(parent);

        Assert.Empty(_repo.GetSubtasks(parent));
    }

    [Fact]
    public void Lists_AddRenameDeleteAndCounts()
    {
        var id = _repo.AddList("공부", "#7A3FBF");
        _repo.Add(new TaskItem { Title = "단어 30개", ListId = id });
        var done = _repo.Add(new TaskItem { Title = "문법", ListId = id });
        _repo.SetCompleted(done, true);

        Assert.Equal(1, _repo.GetLists().Single(l => l.Id == id).OpenCount);
        Assert.Equal(2, _repo.GetByList(id).Count);

        _repo.RenameList(id, "영어");
        Assert.Equal("영어", _repo.GetLists().Single(l => l.Id == id).Name);

        _repo.DeleteList(id);
        Assert.DoesNotContain(_repo.GetLists(), l => l.Id == id);
        Assert.Empty(_repo.GetCompleted());
    }

    [Fact]
    public void ByList_OpenFirstThenRecentDone()
    {
        var list = _repo.GetLists()[0].Id;
        var done = _repo.Add(new TaskItem { Title = "done", ListId = list });
        _repo.SetCompleted(done, true);
        var open = _repo.Add(new TaskItem { Title = "open", ListId = list });

        Assert.Equal([open, done], _repo.GetByList(list).Select(i => i.Id));
    }

    [Fact]
    public void GetDueBetween_IncludesDoneAndTimed()
    {
        var a = _repo.Add(new TaskItem { Title = "a", DueDate = Today });
        var b = _repo.Add(new TaskItem { Title = "b", DueDate = Today.AddDays(2), DueTime = new TimeOnly(23, 0) });
        _repo.Add(new TaskItem { Title = "c", DueDate = Today.AddDays(3) });
        _repo.SetCompleted(a, true);

        Assert.Equal([a, b], _repo.GetDueBetween(Today, Today.AddDays(2)).Select(i => i.Id));
    }

    [Fact]
    public void Writes_RaiseChanged()
    {
        var count = 0;
        _repo.Changed += (_, _) => count++;

        var id = _repo.Add(new TaskItem { Title = "a" });
        _repo.SetCompleted(id, true);
        _repo.SetMyDay(id, true);
        _repo.Delete(id);

        Assert.Equal(4, count);
    }

    [Fact]
    public void FullTextIndex_FollowsTitles()
    {
        _repo.Add(new TaskItem { Title = "치과 예약" });
        using var connection = new Database(_dir.Combine("data.db")).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM items_fts WHERE items_fts MATCH '치과*'";

        Assert.Equal(1L, command.ExecuteScalar());
    }
}
