using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DayDeck.Core.Data;

namespace DayDeck.Core.Notes;

/// <summary>열려 있던 메모 탭 하나 (세션 복원용).</summary>
public sealed record NoteTabState(string Path, int CaretIndex);

/// <summary>
/// 메모 화면의 세션 (Notepad++처럼 다시 열면 탭, 커서 위치, 마지막 폴더를 복원한다).
/// 앱 상태(DB)에 저장하므로 데이터 폴더와 함께 옮겨진다.
/// </summary>
public sealed record NoteSession(IReadOnlyList<NoteTabState> Tabs, string? ActivePath, string Folder, bool WordWrap = true)
{
    private const string StateKey = "notes.session";

    public static readonly NoteSession Empty = new([], null, "");

    public static NoteSession Load(AppStateStore state)
    {
        var json = state.Get(StateKey);
        if (string.IsNullOrEmpty(json))
        {
            return Empty;
        }
        try
        {
            var data = JsonSerializer.Deserialize(json, NoteSessionJsonContext.Default.SessionData);
            return data is null
                ? Empty
                : new NoteSession(data.Tabs?.Select(t => new NoteTabState(t.Path, t.Caret)).ToList() ?? [], data.Active, data.Folder ?? "", data.WordWrap ?? true);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public void Save(AppStateStore state) =>
        state.Set(StateKey, JsonSerializer.Serialize(
            new SessionData([.. Tabs.Select(t => new TabData(t.Path, t.CaretIndex))], ActivePath, Folder, WordWrap),
            NoteSessionJsonContext.Default.SessionData));

    internal sealed record TabData(string Path, int Caret);

    internal sealed record SessionData(TabData[]? Tabs, string? Active, string? Folder, bool? WordWrap);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NoteSession.SessionData))]
internal sealed partial class NoteSessionJsonContext : JsonSerializerContext;

/// <summary>메모 본문에서 할 일로 만들 줄을 읽는다.</summary>
public static partial class NoteText
{
    [GeneratedRegex(@"^\s*(?:[-*+]\s*)?(?:\[[ xX]?\]\s*)?(?:\d+[.)]\s*)?(?<title>.*?)\s*$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*(?:[-*+]\s*)?\[[xX]\]")]
    private static partial Regex CheckedItem();

    /// <summary>"- [ ] 보고서", "* 보고서", "1. 보고서" 같은 줄에서 제목만 꺼낸다. 빈 줄이면 null.</summary>
    public static string? ExtractTaskTitle(string line)
    {
        var title = MarkdownDoc.ToPlainText(ListItem().Match(line).Groups["title"].Value.TrimStart('#', ' ')).Trim();
        return title.Length > 0 ? title : null;
    }

    /// <summary>"- [x] ..."처럼 이미 체크된 줄인지.</summary>
    public static bool IsChecked(string line) => CheckedItem().IsMatch(line);

    /// <summary>메모 첫 줄(제목 기호 제외)로 파일 이름을 제안한다.</summary>
    public static string SuggestTitle(string content)
    {
        var first = content.Split('\n').Select(l => MarkdownDoc.ToPlainText(l.Trim().TrimStart('#').Trim()).Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length > 40 ? first[..40] : first;
    }
}
