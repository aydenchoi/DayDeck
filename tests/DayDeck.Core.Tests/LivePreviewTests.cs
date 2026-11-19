using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

/// <summary>옵시디언 모드: 줄마다 서식 범위와 숨길 기호를 바르게 찾는지.</summary>
public sealed class LivePreviewTests
{
    private static string Hidden(string line) =>
        string.Concat(line.Select((c, i) => LivePreview.ParseLine(line).Markers.Any(m => i >= m.Start && i < m.End) ? "" : c.ToString()));

    [Theory]
    [InlineData("# 제목", "제목")]
    [InlineData("### 작은 제목", "작은 제목")]
    [InlineData("**굵게** 와 *기울임*", "굵게 와 기울임")]
    [InlineData("~~취소~~ ==형광== <mark>표시</mark> <u>밑줄</u>", "취소 형광 표시 밑줄")]
    [InlineData("`코드 **그대로**`", "코드 **그대로**")]
    [InlineData("[링크](https://example.com) 끝", "링크 끝")]
    [InlineData("[[회의록|오늘 회의]] 와 [[메모]]", "오늘 회의 와 메모")]
    [InlineData("<span style=\"color:#C42B1C\">빨강</span>", "빨강")]
    [InlineData("> 인용 *기울임*", "인용 기울임")]
    [InlineData("snake_case_name 그대로", "snake_case_name 그대로")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    [InlineData("\\*별표\\*", "*별표*")]
    [InlineData("***둘다***", "둘다")]
    [InlineData("#", "#")]
    [InlineData("## ", "## ")]
    public void HidesMarkers(string line, string visible) => Assert.Equal(visible, Hidden(line));

    [Fact]
    public void Heading_HasLevel()
    {
        var line = LivePreview.ParseLine("## 둘째");
        Assert.Equal(LiveLineKind.Heading, line.Kind);
        Assert.Equal(2, line.Level);
    }

    [Fact]
    public void Task_ReplacesBulletAndCheckbox()
    {
        var line = LivePreview.ParseLine("  - [x] 끝난 일");
        Assert.Equal(LiveLineKind.Task, line.Kind);
        Assert.Contains(line.Markers, m => m.Kind == LiveReplace.Hide && m.Start == 2);
        Assert.Contains(line.Markers, m => m.Kind == LiveReplace.CheckboxDone && m.Start == 4 && m.Length == 3);
        Assert.Contains(line.Spans, s => s.Style == LiveStyle.Done);
        Assert.Equal(4, line.CheckboxStart);
    }

    [Fact]
    public void Bullet_ReplacedByDot()
    {
        var line = LivePreview.ParseLine("- 항목");
        Assert.Equal(LiveLineKind.Bullet, line.Kind);
        Assert.Equal(new LiveMarker(0, 1, LiveReplace.Bullet), Assert.Single(line.Markers));
    }

    [Fact]
    public void Image_WithWidth()
    {
        var marker = Assert.Single(LivePreview.ParseLine("![사진|300](.images/a.png)").Markers);
        Assert.Equal(LiveReplace.Image, marker.Kind);
        Assert.Equal(".images/a.png", marker.Data);
        Assert.Equal(300, marker.Width);

        var wiki = Assert.Single(LivePreview.ParseLine("![[그림.png|200]]").Markers);
        Assert.Equal("그림.png", wiki.Data);
        Assert.Equal(200, wiki.Width);
    }

    [Fact]
    public void Links_CarryUrl()
    {
        var line = LivePreview.ParseLine("보기 https://example.com/a?b=1. 그리고 [x](https://y.com)");
        Assert.Contains(line.Spans, s => s.Style == LiveStyle.Link && s.Url == "https://example.com/a?b=1");
        Assert.Contains(line.Spans, s => s.Style == LiveStyle.Link && s.Url == "https://y.com");
    }

    [Fact]
    public void Rule_And_Table()
    {
        Assert.Equal(LiveLineKind.Rule, LivePreview.ParseLine("---").Kind);
        Assert.Equal(LiveLineKind.Table, LivePreview.ParseLine("| a | b |").Kind);
        Assert.Empty(LivePreview.ParseLine("| **a** | b |").Markers);
    }

    [Fact]
    public void CodeBlocks()
    {
        string[] lines = ["앞", "```cs", "# 주석", "```", "뒤", "~~~", "열린 채"];
        Assert.Equal(new byte[] { 0, 1, 2, 1, 0, 1, 2 }, LivePreview.CodeBlockStates(lines));
        Assert.Equal(LiveLineKind.Code, LivePreview.ParseLine("# 주석", 2).Kind);
        Assert.Empty(LivePreview.ParseLine("**x**", 2).Spans);
    }

    [Theory]
    [InlineData("- [ ] 일", "- [x] 일")]
    [InlineData("1. [X] 일", "1. [ ] 일")]
    public void ToggleCheckbox(string line, string expected) => Assert.Equal(expected, LivePreview.ToggleCheckbox(line));

    [Theory]
    [InlineData("- 항목", "- ", false)]
    [InlineData("  * 항목", "  * ", false)]
    [InlineData("3. 셋", "4. ", false)]
    [InlineData("- [x] 끝", "- [ ] ", false)]
    [InlineData("> 인용", "> ", false)]
    [InlineData("- ", "- ", true)]
    [InlineData("2. ", "3. ", true)]
    public void ContinuePrefix(string line, string prefix, bool end)
    {
        Assert.Equal(prefix, LivePreview.ContinuePrefix(line, out var endList));
        Assert.Equal(end, endList);
    }

    [Fact]
    public void PlainLine_HasNoPrefix() => Assert.Null(LivePreview.ContinuePrefix("보통 줄", out _));
}
