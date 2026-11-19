using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

/// <summary>메모 편집기 서식 도구 (마크다운으로 저장) 와 미리보기 해석.</summary>
public sealed class MarkdownEditorTests
{
    /// <summary>"«선택»" 표기로 선택을 나타낸 입력에 편집을 적용하고, 결과도 같은 표기로 돌려준다.</summary>
    private static string Apply(string marked, Func<string, int, int, TextEdit> edit)
    {
        var start = marked.IndexOf('«');
        var end = marked.IndexOf('»');
        var text = marked.Remove(end, 1).Remove(start, 1);
        var result = edit(text, start, end - start - 1);
        var applied = result.ApplyTo(text);
        return applied.Insert(result.SelectionStart + result.SelectionLength, "»").Insert(result.SelectionStart, "«");
    }

    [Theory]
    [InlineData("오늘 «회의» 있음", "오늘 **«회의»** 있음")]          // 감싸기
    [InlineData("오늘 **«회의»** 있음", "오늘 «회의» 있음")]          // 바깥 표시 풀기
    [InlineData("오늘 «**회의**» 있음", "오늘 «회의» 있음")]          // 선택 안의 표시 풀기
    [InlineData("오늘 *«회의»* 있음", "오늘 ***«회의»*** 있음")]      // 기울임 + 굵게
    [InlineData("오늘 ***«회의»*** 있음", "오늘 *«회의»* 있음")]      // 굵게만 빼기
    [InlineData("«»", "**«»**")]                                      // 빈 선택: 표시 넣고 커서는 가운데
    [InlineData("**«»**", "«»")]                                      // 빈 표시 사이에서 다시 누르면 지움
    [InlineData("보통 **굵게«»** 끝", "보통 **굵게**«» 끝")]           // 굵게 친 뒤 다시 누르면 표시 밖으로
    [InlineData("**앞** 그리고 «»**", "**앞** 그리고 **«»****")]      // 닫힌 서식 뒤에서는 새로 넣음
    public void Bold(string input, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, MarkdownEdit.Bold, MarkdownEdit.Bold)));

    [Theory]
    [InlineData("«중요»", "*«중요»*")]
    [InlineData("*«중요»*", "«중요»")]
    [InlineData("**«중요»**", "***«중요»***")]   // 굵게 위에 기울임
    [InlineData("***«중요»***", "**«중요»**")]
    [InlineData("*기울임«»* 끝", "*기울임*«» 끝")]
    [InlineData("**굵게«»** 끝", "**굵게*«»*** 끝")]   // 굵게의 닫는 표시는 기울임으로 보지 않음
    public void Italic(string input, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, MarkdownEdit.Italic, MarkdownEdit.Italic)));

    [Theory]
    [InlineData("«밑줄»", "<u>«밑줄»</u>")]
    [InlineData("<u>«밑줄»</u>", "«밑줄»")]
    [InlineData("«<u>밑줄</u>»", "«밑줄»")]
    public void Underline(string input, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, "<u>", "</u>")));

    [Fact]
    public void MultiLine_WrapsEachLineAfterListMarker_AndUnwraps()
    {
        var wrapped = Apply("«- 우유\n- 계란\n\n# 제목»", (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, "**", "**"));
        Assert.Equal("«- **우유**\n- **계란**\n\n# **제목**»", wrapped);
        var unwrapped = Apply(wrapped, (t, s, l) => MarkdownEdit.ToggleInline(t, s, l, "**", "**"));
        Assert.Equal("«- 우유\n- 계란\n\n# 제목»", unwrapped);
    }

    [Theory]
    [InlineData("«빨강»", "#c42b1c", "<span style=\"color:#C42B1C\">«빨강»</span>")]
    [InlineData("<span style=\"color:#C42B1C\">«빨강»</span>", "#2B6CD4", "<span style=\"color:#2B6CD4\">«빨강»</span>")]   // 색 바꾸기
    [InlineData("<span style=\"color:#C42B1C\">«빨강»</span>", null, "«빨강»")]                                              // 색 지우기
    [InlineData("«<span style=\"color:#C42B1C\">빨강</span>»", null, "«빨강»")]
    [InlineData("«»", "#0F7B0F", "<span style=\"color:#0F7B0F\">«»</span>")]
    public void Color(string input, string? hex, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.SetColor(t, s, l, hex)));

    [Theory]
    [InlineData("여기 «구글» 참고", "https://google.com", null, "여기 [구글](https://google.com)«» 참고")]
    [InlineData("«»", "https://a.com/x y", null, "[https://a.com/x y](https://a.com/x%20y)«»")]
    [InlineData("«»", "https://a.com", "회사 [홈]", "[회사 \\[홈\\]](https://a.com)«»")]
    public void Link(string input, string url, string? label, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.InsertLink(t, s, l, label, url)));

    [Theory]
    [InlineData("www.naver.com", "https://www.naver.com")]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("kim@example.com", "mailto:kim@example.com")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/notepad.exe", null)]
    [InlineData("그냥 글자", null)]
    public void NormalizeUrl(string input, string? expected) => Assert.Equal(expected, MarkdownEdit.NormalizeUrl(input));

    [Theory]
    [InlineData("«회의»", LineStyle.Heading1, "«# 회의»")]
    [InlineData("# «회의»", LineStyle.Heading2, "«## 회의»")]          // 다른 제목 수준으로
    [InlineData("## «회의»", LineStyle.Heading2, "«회의»")]              // 같은 서식이면 풀기
    [InlineData("«우유\n계란\n\n사과»", LineStyle.Bullet, "«- 우유\n- 계란\n\n- 사과»")]
    [InlineData("«- 우유\n- 계란»", LineStyle.Numbered, "«1. 우유\n2. 계란»")]
    [InlineData("«1. 우유\n2. 계란»", LineStyle.Task, "«- [ ] 우유\n- [ ] 계란»")]
    [InlineData("«- [x] 우유\n- [ ] 계란»", LineStyle.Task, "«우유\n계란»")]
    [InlineData("  «하위 항목»", LineStyle.Bullet, "«  - 하위 항목»")]   // 들여쓰기 유지
    [InlineData("«인용»", LineStyle.Quote, "«> 인용»")]
    public void LineStyles(string input, LineStyle style, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.SetLineStyle(t, s, l, style)));

    [Fact]
    public void LineStyle_CaretOnly_AppliesToCurrentLine_CaretAtEnd()
    {
        var text = "첫 줄\n둘째 줄\n셋째";
        var edit = MarkdownEdit.SetLineStyle(text, text.IndexOf("째 줄", StringComparison.Ordinal), 0, LineStyle.Heading2);
        Assert.Equal("첫 줄\n## 둘째 줄\n셋째", edit.ApplyTo(text));
        Assert.Equal(0, edit.SelectionLength);
        Assert.Equal("첫 줄\n## 둘째 줄".Length, edit.SelectionStart);
    }

    // ---------- 미리보기 해석 ----------

    [Fact]
    public void Inlines_StylesColorsLinks()
    {
        var runs = MarkdownDoc.ParseInlines("보통 **굵게** *기울임* ***둘다*** ~~취소~~ `코드` <u>밑줄</u> <mark>형광</mark> " +
                                            "<span style=\"color:#C42B1C\">빨강 **굵은빨강**</span> [링크](https://a.com) https://b.com.");
        InlineRun Run(string text) => runs.Single(r => r.Text == text);
        Assert.Equal(InlineStyle.Bold, Run("굵게").Style);
        Assert.Equal(InlineStyle.Italic, Run("기울임").Style);
        Assert.Equal(InlineStyle.Bold | InlineStyle.Italic, Run("둘다").Style);
        Assert.Equal(InlineStyle.Strike, Run("취소").Style);
        Assert.Equal(InlineStyle.Code, Run("코드").Style);
        Assert.Equal(InlineStyle.Underline, Run("밑줄").Style);
        Assert.Equal(InlineStyle.Highlight, Run("형광").Style);
        Assert.Equal("#C42B1C", Run("빨강 ").Color);
        Assert.Equal((InlineStyle.Bold, "#C42B1C"), (Run("굵은빨강").Style, Run("굵은빨강").Color));
        Assert.Equal("https://a.com", Run("링크").Link);
        Assert.Equal("https://b.com", Run("https://b.com").Link); // 끝의 마침표는 주소가 아님
        Assert.Null(Run("보통 ").Color);
    }

    [Theory]
    [InlineData("5 * 3 = 15")]          // 닫는 별이 없으면 글자 그대로
    [InlineData("a ** b")]
    [InlineData("1 < 2 그리고 3 > 2")]
    [InlineData("\\*별표\\* 그대로")]
    public void Inlines_LiteralWhenNotFormatting(string text)
    {
        var runs = MarkdownDoc.ParseInlines(text);
        Assert.All(runs, r => Assert.Equal(InlineStyle.None, r.Style));
        Assert.Equal(text.Replace("\\", ""), string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void Blocks()
    {
        var blocks = MarkdownDoc.Parse("# 회의록\n참석: 김, 이\n둘째 줄\n\n- [ ] 자료 준비\n- [x] 메일\n  - 하위\n1. 첫째\n> 인용\n---\n```\nvar x = **1**;\n```\n");
        Assert.Equal(
            [BlockKind.Heading, BlockKind.Paragraph, BlockKind.Task, BlockKind.Task, BlockKind.Bullet, BlockKind.Numbered, BlockKind.Quote, BlockKind.Rule, BlockKind.Code],
            blocks.Select(b => b.Kind));
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("참석: 김, 이\n둘째 줄", blocks[1].PlainText);
        Assert.False(blocks[2].Checked);
        Assert.True(blocks[3].Checked);
        Assert.Equal(5, blocks[3].Line);
        Assert.Equal(1, blocks[4].Level);
        Assert.Equal("var x = **1**;", blocks[8].PlainText); // 코드 안은 서식 없음
    }

    [Theory]
    [InlineData("# **회의** <span style=\"color:#C42B1C\">긴급</span>", "회의 긴급")]
    [InlineData("- [ ] [자료](https://a.com) 준비", "자료 준비")]
    public void PlainText_ForTitlesAndPreviews(string line, string expected) =>
        Assert.Equal(expected, NoteText.ExtractTaskTitle(line));

    [Fact]
    public void ChangeExtension_TxtToMd_KeepsContentAndPin()
    {
        using var dir = new TempDirectory();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var db = new Data.Database(dir.Combine("data.db"));
        db.Migrate();
        var store = new NoteStore(dir.Combine("Notes"), new Data.AppStateStore(db), time);
        var path = store.Create("개인", "장보기", ".txt", "우유");
        store.SetPinned(path, true);

        var md = store.ChangeExtension(path, ".md");

        Assert.Equal("개인/장보기.md", md);
        Assert.Equal("우유", store.Read(md));
        Assert.False(store.Exists(path));
        Assert.True(store.GetInfo(md).IsPinned);
        Assert.Equal(md, store.ChangeExtension(md, ".md")); // 이미 .md
    }

    [Theory]
    [InlineData("«»", "![](.images/a.png)\n«»")]
    [InlineData("앞«»뒤", "앞\n![](.images/a.png)\n«»뒤")]
    [InlineData("첫 줄«»\n둘째", "첫 줄\n![](.images/a.png)\n«»둘째")]
    public void InsertImages_TakesItsOwnLine(string input, string expected) =>
        Assert.Equal(expected, Apply(input, (t, s, l) => MarkdownEdit.InsertImages(t, s, l, [".images/a.png"])));

    [Fact]
    public void InsertImages_OnePerLine_AndEscapesSpaces() =>
        Assert.Equal("![](a%20b.png)\n![](c.png)\n«»",
            Apply("«»", (t, s, l) => MarkdownEdit.InsertImages(t, s, l, ["a b.png", "c.png"])));

    [Fact]
    public void Parse_ImageWidth_FromAltSuffix()
    {
        Assert.Equal(new InlineRun("화면", Image: ".images/s.png", ImageWidth: 320), MarkdownDoc.ParseInlines("![화면|320](.images/s.png)").Single());
        Assert.Equal(new InlineRun("", Image: ".images/s.png", ImageWidth: 80), MarkdownDoc.ParseInlines("![|80](.images/s.png)").Single());
        Assert.Equal(new InlineRun("a|b", Image: ".images/s.png"), MarkdownDoc.ParseInlines("![a|b](.images/s.png)").Single());
    }

    [Fact]
    public void Parse_Image_KeepsPathAndAltText()
    {
        var runs = MarkdownDoc.ParseInlines("보기 ![화면](.images/s.png) 끝");

        Assert.Equal(3, runs.Count);
        Assert.Equal(new InlineRun("화면", Image: ".images/s.png"), runs[1]);
        Assert.Null(runs[1].Link);
        Assert.Equal("보기 화면 끝", MarkdownDoc.ToPlainText("보기 ![화면](.images/s.png) 끝"));
        Assert.Equal("!", MarkdownDoc.ParseInlines("와!")[0].Text[^1..]);
    }
}
