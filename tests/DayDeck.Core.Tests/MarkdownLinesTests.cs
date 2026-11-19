using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

/// <summary>메모 "보기"에서 바로 고칠 때 쓰는 줄 모델: 원문을 잃지 않고 왕복하는지.</summary>
public sealed class MarkdownLinesTests
{
    private static string RoundTrip(string markdown) => MarkdownLines.Write(MarkdownLines.Parse(markdown));

    [Theory]
    [InlineData("# 주간 회의\n\n- 자료 준비\n  - 표 정리\n1. 첫째\n2. 둘째\n- [ ] 할 일\n- [x] 끝난 일\n> 인용\n\n---\n보통 줄")]
    [InlineData("**굵게** *기울임* ***둘다*** ~~취소~~ <u>밑줄</u> <mark>형광</mark> `코드` <span style=\"color:#C42B1C\">빨강</span>")]
    [InlineData("[링크](https://example.com) 그리고 https://example.com/a 와 www.example.com")]
    [InlineData("![화면](.images/a%20b.png)")]
    [InlineData("```cs\nvar x = 1 * 2;\n\n# 주석 아님\n```\n뒤")]
    [InlineData("C:\\Users\\me 경로와 2\\*3 별표, 언더바 a_b_c, 파이프 | 표 |")]
    [InlineData("줄1<br>줄2")]
    [InlineData("\\- 목록 아님\n\\# 제목 아님\n1\\. 번호 아님\n\\> 인용 아님\n\\---")]
    [InlineData("<div>모르는 태그</div> <https://x.com> <span>색 없는 span</span>")]
    [InlineData("# C#\n## 제목 ##")]
    [InlineData("")]
    [InlineData("\n\n앞뒤 빈 줄\n\n")]
    public void RoundTrip_KeepsSource(string markdown)
    {
        var expected = markdown.Replace("## 제목 ##", "## 제목");
        Assert.Equal(expected, RoundTrip(markdown));
    }

    [Theory]
    [InlineData("* 별 목록\n+ 더하기", "- 별 목록\n- 더하기")]
    [InlineData("1) 괄호 번호", "1. 괄호 번호")]
    [InlineData("<b>굵게</b> <i>기울임</i> <s>취소</s>", "**굵게** *기울임* ~~취소~~")]
    [InlineData("~~~\n코드\n~~~", "```\n코드\n```")]
    [InlineData("```\n닫히지 않은 코드", "```\n닫히지 않은 코드\n```")]
    public void RoundTrip_NormalizesEquivalentSyntax(string markdown, string expected) => Assert.Equal(expected, RoundTrip(markdown));

    [Fact]
    public void Parse_LineKinds()
    {
        var lines = MarkdownLines.Parse("# 제목\n\n  - [x] 끝\n3. 셋\n```\na\n```");

        Assert.Equal([LineKind.Heading, LineKind.Blank, LineKind.Task, LineKind.Numbered, LineKind.Code], lines.Select(l => l.Kind));
        Assert.Equal(1, lines[2].Level);
        Assert.True(lines[2].Checked);
        Assert.Equal(3, lines[3].Number);
        Assert.Equal("", lines[4].Language);
    }

    [Theory]
    [InlineData("- 항목", "\\- 항목")]
    [InlineData("1. 번호", "1\\. 번호")]
    [InlineData("# 제목", "\\# 제목")]
    [InlineData("> 인용", "\\> 인용")]
    [InlineData("---", "\\---")]
    [InlineData("```", "\\`\\`\\`")]
    [InlineData("#해시태그", "#해시태그")]
    [InlineData("-5도", "-5도")]
    public void Write_TextLine_EscapesBlockSyntax(string text, string expected)
    {
        var markdown = MarkdownLines.WriteLine(new MarkdownLine(LineKind.Text, [new InlineRun(text)]));

        Assert.Equal(expected, markdown);
        Assert.Equal(text, MarkdownLines.Parse(markdown).Single().PlainText);
        Assert.Equal(LineKind.Text, MarkdownLines.Parse(markdown).Single().Kind);
    }

    [Fact]
    public void WriteInlines_MovesSpacesOutsideMarkers()
    {
        var runs = new[] { new InlineRun("앞 "), new InlineRun("굵게 ", InlineStyle.Bold), new InlineRun("뒤") };

        var markdown = MarkdownLines.WriteInlines(runs);

        Assert.Equal("앞 **굵게** 뒤", markdown);
        Assert.Equal(runs.Select(r => r.Text.Trim()).Where(t => t.Length > 0), MarkdownDoc.ParseInlines(markdown).Select(r => r.Text.Trim()).Where(t => t.Length > 0));
    }

    [Fact]
    public void WriteInlines_NestedStyles_ReadBackTheSame()
    {
        var runs = new[]
        {
            new InlineRun("빨강 ", Color: "#C42B1C"),
            new InlineRun("굵은 빨강", InlineStyle.Bold, "#C42B1C"),
            new InlineRun(" 형광 코드", InlineStyle.Highlight | InlineStyle.Code),
            new InlineRun(" 링크", InlineStyle.Italic, Link: "https://example.com"),
            new InlineRun("그림", Image: ".images/x.png"),
        };

        var parsed = MarkdownDoc.ParseInlines(MarkdownLines.WriteInlines(runs));

        Assert.Contains(parsed, r => r.Text == "굵은 빨강" && r.Style == InlineStyle.Bold && r.Color == "#C42B1C");
        Assert.Contains(parsed, r => r.Text == "형광 코드" && r.Style == (InlineStyle.Highlight | InlineStyle.Code));
        Assert.Contains(parsed, r => r.Text == "링크" && r.Style == InlineStyle.Italic && r.Link == "https://example.com");
        Assert.Contains(parsed, r => r.Image == ".images/x.png" && r.Text == "그림");
        Assert.Equal("빨강 굵은 빨강 형광 코드 링크그림", string.Concat(parsed.Select(r => r.Text)));
    }

    [Fact]
    public void WriteInlines_ImageWidth_RoundTrips()
    {
        var runs = new[] { new InlineRun("그림", Image: ".images/x.png", ImageWidth: 240), new InlineRun("", Image: ".images/y.png") };

        var markdown = MarkdownLines.WriteInlines(runs);

        Assert.Equal("![그림|240](.images/x.png)![](.images/y.png)", markdown);
        Assert.Equal(runs, MarkdownDoc.ParseInlines(markdown));
    }

    [Fact]
    public void WriteInlines_CodeWithBacktick_FallsBackToPlainText()
    {
        var markdown = MarkdownLines.WriteInlines([new InlineRun("a`b", InlineStyle.Code)]);
        Assert.Equal("a`b", string.Concat(MarkdownDoc.ParseInlines(markdown).Select(r => r.Text)));
    }

    [Fact]
    public void Write_ListAndCodeLines()
    {
        var markdown = MarkdownLines.Write(
        [
            new MarkdownLine(LineKind.Bullet, [new InlineRun("[ ] 체크 아님")]),
            new MarkdownLine(LineKind.Task, [new InlineRun("체크")], Level: 1, Checked: true),
            new MarkdownLine(LineKind.Code, [new InlineRun("x")], Language: "js"),
            new MarkdownLine(LineKind.Code, [new InlineRun("y")]),
            new MarkdownLine(LineKind.Text, [new InlineRun("끝")]),
        ]);

        Assert.Equal("- \\[ ] 체크 아님\n  - [x] 체크\n```js\nx\ny\n```\n끝", markdown);
        Assert.Equal(LineKind.Bullet, MarkdownLines.Parse(markdown)[0].Kind);
    }

    [Fact]
    public void ToPlainText_KeepsOnlyVisibleText() =>
        Assert.Equal("제목\n\n항목 하나\n할 일\n굵게 빨강 링크 그림설명\n\nvar x = 1;",
            MarkdownLines.ToPlainText("# 제목\n\n- 항목 **하나**\n- [x] 할 일\n**굵게** <span style=\"color:#C42B1C\">빨강</span> [링크](https://a.com) ![그림설명](.images/a.png)\n---\n```\nvar x = 1;\n```"));
}
