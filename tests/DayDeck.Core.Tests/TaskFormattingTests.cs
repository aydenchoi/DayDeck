using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class TaskFormattingTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 14, 30, 0); // 수요일
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    [Theory]
    [InlineData(0, "오늘")]
    [InlineData(1, "내일")]
    [InlineData(2, "모레")]
    [InlineData(-1, "어제")]
    [InlineData(3, "토요일")]
    [InlineData(6, "화요일")]
    [InlineData(7, "11월 4일 (수)")]
    [InlineData(70, "2027년 1월 6일 (수)")]
    public void FormatDate_UsesRelativeKoreanWords(int days, string expected)
    {
        Assert.Equal(expected, TaskFormatting.FormatDate(Today.AddDays(days), Today));
    }

    [Fact]
    public void FormatDue_DescribesDeadlineAndLateness()
    {
        Assert.Null(TaskFormatting.FormatDue(new TaskItem(), Now));
        Assert.Equal("오늘까지", TaskFormatting.FormatDue(new TaskItem { DueDate = Today }, Now));
        Assert.Equal("오늘 17:00", TaskFormatting.FormatDue(new TaskItem { DueDate = Today, DueTime = new TimeOnly(17, 0) }, Now));
        Assert.Equal("오늘 09:00 마감 · 지남", TaskFormatting.FormatDue(new TaskItem { DueDate = Today, DueTime = new TimeOnly(9, 0) }, Now));
        Assert.Equal("어제 18:00 마감 · 1일 지남", TaskFormatting.FormatDue(new TaskItem { DueDate = Today.AddDays(-1), DueTime = new TimeOnly(18, 0) }, Now));
    }
}
