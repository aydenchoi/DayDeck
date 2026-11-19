using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class TaskRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 14, 30, 0);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private static TaskItem Task(DateOnly? due = null, TimeOnly? time = null, ItemStatus status = ItemStatus.Open,
        DateOnly? myDay = null, DateTime? completedAt = null) =>
        new() { Title = "t", DueDate = due, DueTime = time, Status = status, MyDay = myDay, CompletedAt = completedAt };

    [Fact]
    public void DateOnlyDue_IsOverdueOnlyAfterThatDay()
    {
        Assert.False(TaskRules.IsOverdue(Task(Today), Now));
        Assert.True(TaskRules.IsOverdue(Task(Today.AddDays(-1)), Now));
    }

    [Fact]
    public void TimedDue_IsOverdueAfterTime()
    {
        Assert.True(TaskRules.IsOverdue(Task(Today, new TimeOnly(14, 0)), Now));
        Assert.False(TaskRules.IsOverdue(Task(Today, new TimeOnly(15, 0)), Now));
    }

    [Fact]
    public void DoneTask_IsNeverOverdue()
    {
        Assert.False(TaskRules.IsOverdue(Task(Today.AddDays(-3), status: ItemStatus.Completed), Now));
        Assert.Equal(TaskDisplayState.Completed, TaskRules.GetState(Task(Today.AddDays(-3), status: ItemStatus.Completed), Now));
        Assert.Equal(TaskDisplayState.Cancelled, TaskRules.GetState(Task(status: ItemStatus.Cancelled), Now));
    }

    [Fact]
    public void GetState_DistinguishesOpenAndOverdue()
    {
        Assert.Equal(TaskDisplayState.Open, TaskRules.GetState(Task(), Now));
        Assert.Equal(TaskDisplayState.Overdue, TaskRules.GetState(Task(Today.AddDays(-1)), Now));
    }

    [Fact]
    public void Today_IncludesDueTodayOverdueAndMyDay()
    {
        Assert.True(TaskRules.IsInToday(Task(Today), Now));
        Assert.True(TaskRules.IsInToday(Task(Today.AddDays(-5)), Now));
        Assert.True(TaskRules.IsInToday(Task(myDay: Today), Now));
        Assert.False(TaskRules.IsInToday(Task(Today.AddDays(1)), Now));
        Assert.False(TaskRules.IsInToday(Task(myDay: Today.AddDays(-1)), Now));
        Assert.False(TaskRules.IsInToday(Task(), Now));
    }

    [Fact]
    public void Today_IncludesOnlyTasksCompletedToday()
    {
        Assert.True(TaskRules.IsInToday(Task(Today, status: ItemStatus.Completed, completedAt: Now.AddHours(-1)), Now));
        Assert.False(TaskRules.IsInToday(Task(Today.AddDays(-2), status: ItemStatus.Completed, completedAt: Now.AddDays(-1)), Now));
    }

    [Fact]
    public void Upcoming_IsNextSevenDaysExcludingOverdue()
    {
        Assert.True(TaskRules.IsUpcoming(Task(Today), Now));
        Assert.True(TaskRules.IsUpcoming(Task(Today.AddDays(7)), Now));
        Assert.False(TaskRules.IsUpcoming(Task(Today.AddDays(8)), Now));
        Assert.False(TaskRules.IsUpcoming(Task(Today, new TimeOnly(9, 0)), Now));
        Assert.False(TaskRules.IsUpcoming(Task(), Now));
        Assert.True(TaskRules.IsUpcoming(Task(Today.AddDays(14)), Now, days: 14));
        Assert.False(TaskRules.IsUpcoming(Task(Today.AddDays(15)), Now, days: 14));
    }

    [Fact]
    public void OrderOpen_PutsOverdueFirstThenDueThenPriority()
    {
        var noDue = Task();
        var later = Task(Today.AddDays(3));
        var todayLow = Task(Today); todayLow.Priority = Priority.Low;
        var todayHigh = Task(Today); todayHigh.Priority = Priority.High;
        var overdue = Task(Today.AddDays(-1));

        var ordered = TaskRules.OrderOpen([noDue, later, todayLow, todayHigh, overdue], Now).ToList();

        Assert.Equal([overdue, todayHigh, todayLow, later, noDue], ordered);
    }
}
