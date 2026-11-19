using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

public sealed class MarkdownTableTests
{
    private static string Markdown(MarkdownTable table) => string.Join("\n", table.Write());

    /// <summary>«»로 선택을 표시한 글에 고치기를 적용하고, 결과에 새 선택을 «»로 표시한다 (MarkdownEditorTests와 같은 방식).</summary>
    private static string Apply(string marked, Func<string, int, int, TextEdit> edit)
    {
        var start = marked.IndexOf('«');
        var end = marked.IndexOf('»');
        var text = marked.Remove(end, 1).Remove(start, 1);
        var result = edit(text, start, end - start - 1);
        var applied = result.ApplyTo(text);
        return applied.Insert(result.SelectionStart + result.SelectionLength, "»").Insert(result.SelectionStart, "«");
    }

    [Fact]
    public void FromTsv_ExcelCopy_BecomesTable_NumbersRightAligned()
    {
        var table = MarkdownTable.FromTsv("이름\t수량\t금액\r\n사과\t3\t4,500\r\n배\t\t-1,200.5\r\n");

        Assert.NotNull(table);
        Assert.Equal(
            "| 이름 | 수량 | 금액 |\n| --- | ---: | ---: |\n| 사과 | 3 | 4,500 |\n| 배 |  | -1,200.5 |",
            Markdown(table!));
    }

    [Fact]
    public void FromTsv_QuotedCells_KeepTabsQuotesAndLineBreaks()
    {
        var table = MarkdownTable.FromTsv("a\tb\n\"줄1\n줄2\"\t\"말 \"\"인용\"\"\"\n");

        Assert.NotNull(table);
        Assert.Equal("줄1<br>줄2", table!.Rows[1][0]);
        Assert.Equal("말 \"인용\"", table.Rows[1][1]);
    }

    [Fact]
    public void FromTsv_EscapesMarkdownAndPipes_SoCellsReadBackAsTyped()
    {
        var table = MarkdownTable.FromTsv("*별* | 막대\t`코드`\n1\t2\n")!;

        var parsed = MarkdownTable.TryParse(table.Write(), 0, out var back, out _);

        Assert.True(parsed);
        Assert.Equal("*별* | 막대", MarkdownDoc.ToPlainText(back!.Rows[0][0]));
        Assert.Equal(InlineStyle.None, MarkdownDoc.ParseInlines(back.Rows[0][0]).Single().Style);
        Assert.Equal("`코드`", MarkdownDoc.ToPlainText(back.Rows[0][1]));
    }

    [Theory]
    [InlineData("한 칸만\n")]
    [InlineData("")]
    [InlineData("a\tb\nc\n")] // 줄마다 칸 수가 다름
    public void FromTsv_NotATable_ReturnsNull(string text) => Assert.Null(MarkdownTable.FromTsv(text));

    [Fact]
    public void FromTsv_SingleColumn_IsATable() =>
        Assert.Equal("| 항목 |\n| --- |\n| 하나 |", Markdown(MarkdownTable.FromTsv("항목\n하나\n")!));

    [Fact]
    public void TryParse_ReadsAlignmentsAndRows_StopsAtLineWithoutPipe()
    {
        string[] lines = ["앞 글", "이름 | 값", ":--|:-:|", "가 | 1", "나", "| 다 | 2 | 3 |", "뒤 글"];

        Assert.False(MarkdownTable.TryParse(lines, 0, out _, out _));
        Assert.True(MarkdownTable.TryParse(lines, 1, out var table, out var count));

        Assert.Equal(3, count); // 머리글, 구분 줄, "가 | 1" ("나"에는 |가 없어 표가 끝난다)
        Assert.Equal([TableAlign.Left, TableAlign.Center], table!.Alignments);
        Assert.Equal(["가", "1"], table.Rows[1]);
    }

    [Fact]
    public void TryParse_PadsShortRows_AndKeepsEscapedPipes()
    {
        string[] lines = ["| 이름 | 값 |", "| :-- | --: |", "| 가 \\| 나 |", "뒤"];

        Assert.True(MarkdownTable.TryParse(lines, 0, out var table, out var count));

        Assert.Equal(3, count);
        Assert.Equal([TableAlign.Left, TableAlign.Right], table!.Alignments);
        Assert.Equal(["가 \\| 나", ""], table.Rows[1]);
        Assert.Equal("가 | 나", MarkdownDoc.ToPlainText(table.Rows[1][0]));
    }

    [Theory]
    [InlineData("제목\n---")] // 가로줄·setext는 표가 아니다
    [InlineData("a | b\n---")]
    [InlineData("| a | b |\n| c | d |")]
    public void TryParse_NeedsPipeDelimiterRow(string markdown) =>
        Assert.False(MarkdownTable.TryParse(markdown.Split('\n'), 0, out _, out _));

    [Fact]
    public void MarkdownDoc_Parse_TableBlockBetweenParagraphs()
    {
        var blocks = MarkdownDoc.Parse("앞\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\n뒤");

        Assert.Equal([BlockKind.Paragraph, BlockKind.Table, BlockKind.Paragraph], blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[1].Line);
        Assert.Equal(2, blocks[1].Table!.Rows.Count);
    }

    [Fact]
    public void MarkdownLines_TableRoundTrips_AndPlainTextKeepsLineCount()
    {
        const string markdown = "앞\n\n| a | b |\n| --- | ---: |\n| **굵게** | 2 |\n\n뒤";

        var lines = MarkdownLines.Parse(markdown);

        Assert.Equal([LineKind.Text, LineKind.Blank, LineKind.Table, LineKind.Blank, LineKind.Text], lines.Select(l => l.Kind));
        Assert.Equal(markdown, MarkdownLines.Write(lines));
        Assert.Equal("앞\n\na  b\n\n굵게  2\n\n뒤", MarkdownLines.ToPlainText(markdown));
    }

    [Fact]
    public void WithCell_ChangesOnlyThatCell_AndPadsShortRows()
    {
        string[] lines = ["| 이름 | 수량 |", "| --- | ---: |", "| 사과 | 3 |", "| 배 |"];
        Assert.True(MarkdownTable.TryParse(lines, 0, out var table, out _));

        var changed = table!.WithCell(2, 1, "5");

        Assert.Equal("| 이름 | 수량 |\n| --- | ---: |\n| 사과 | 3 |\n| 배 | 5 |", Markdown(changed));
        Assert.Equal("| 이름 | 수량 |\n| --- | ---: |\n| 사과 | 3 |\n| 배 |  |", Markdown(table)); // 원래 표는 그대로
        Assert.Equal("", MarkdownTable.FromTsv("a\tb\n1\t2\n")!.WithCell(1, 0, "").Cell(1, 0));
    }

    [Theory]
    [InlineData("가 \\| 나", "가 | 나")]
    [InlineData("줄1<br>줄2", "줄1\n줄2")]
    [InlineData("줄1<BR/>줄2<br />줄3", "줄1\n줄2\n줄3")]
    [InlineData("**굵게** [링크](https://example.com) \\*별\\*", "**굵게** [링크](https://example.com) \\*별\\*")]
    public void CellEditText_ShowsPipesAndLineBreaks_KeepsOtherMarkdown(string cell, string text) =>
        Assert.Equal(text, MarkdownTable.CellEditText(cell));

    [Theory]
    [InlineData("가 | 나", "가 \\| 나")]
    [InlineData("  줄1\r\n줄2\n  ", "줄1<br>줄2")]
    [InlineData("이미 \\| 이스케이프", "이미 \\| 이스케이프")]
    [InlineData("**굵게**", "**굵게**")]
    public void CellFromEditText_EscapesPipes_AndWritesLineBreaks(string text, string cell) =>
        Assert.Equal(cell, MarkdownTable.CellFromEditText(text));

    [Fact]
    public void EditedCell_RoundTripsThroughMarkdown()
    {
        var table = MarkdownTable.FromTsv("이름\t메모\n사과\t빨강\n")!;

        var edited = table.WithCell(1, 1, MarkdownTable.CellFromEditText("빨강 | 초록\n둘째 줄"));

        Assert.True(MarkdownTable.TryParse(edited.Write(), 0, out var back, out _));
        Assert.Equal("빨강 | 초록\n둘째 줄", MarkdownDoc.ToPlainText(back!.Rows[1][1]));
        Assert.Equal("빨강 | 초록\n둘째 줄", MarkdownTable.CellEditText(back.Rows[1][1]));
    }

    [Theory]
    [InlineData("«»", "T\n«»")]
    [InlineData("앞«»", "앞\n\nT\n«»")]
    [InlineData("앞\n«»", "앞\n\nT\n«»")]
    [InlineData("앞\n\n«»뒤", "앞\n\nT\n\n«»뒤")]
    [InlineData("«»\n뒤", "T\n\n«»뒤")]
    public void InsertBlock_SeparatesWithBlankLines(string input, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.InsertBlock(t, s, l, "T")));
}
