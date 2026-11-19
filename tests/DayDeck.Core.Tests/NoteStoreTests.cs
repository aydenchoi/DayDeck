using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Data;
using DayDeck.Core.Notes;

namespace DayDeck.Core.Tests;

public sealed class NoteStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AppStateStore _state;
    private readonly NoteStore _store;

    public NoteStoreTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        _state = new AppStateStore(db);
        _store = new NoteStore(_dir.Combine("Notes"), _state, _time);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CreateReadSave_RoundTripsUtf8()
    {
        var path = _store.CreateFolder("", "업무");
        var note = _store.Create(path, "주간회의", ".md", "# 주간회의\n- [ ] 자료 준비");

        Assert.Equal("업무/주간회의.md", note);
        Assert.Equal("# 주간회의\n- [ ] 자료 준비", _store.Read(note));

        _store.Save(note, "수정됨 ✓");
        Assert.Equal("수정됨 ✓", _store.Read(note));
        Assert.Equal([0xEC, 0x88, 0x98], File.ReadAllBytes(_store.FullPath(note))[..3]); // BOM 없음
    }

    [Fact]
    public void Save_KeepsExistingBom()
    {
        var full = _store.FullPath("bom.txt");
        File.WriteAllText(full, "a", new System.Text.UTF8Encoding(true));

        _store.Save("bom.txt", "b");

        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'b'], File.ReadAllBytes(full));
    }

    [Fact]
    public void Create_AvoidsNameClashes_AndSanitizes()
    {
        Assert.Equal("메모.md", _store.Create("", "메모"));
        Assert.Equal("메모 (2).md", _store.Create("", "메모"));
        Assert.Equal("a_b_c.txt", _store.Create("", "a/b:c", ".txt"));
        Assert.Equal("제목 없음.md", _store.Create("", "  ...  "));
    }

    [Fact]
    public void FolderTree_IgnoresTrashAndCountsNotes()
    {
        _store.Create(_store.CreateFolder(_store.CreateFolder("", "업무"), "회의록"), "a");
        _store.Create("업무", "b");
        _store.Create("", "c");
        _store.Delete(_store.Create("", "d"));

        var tree = _store.GetFolderTree();

        Assert.Equal(1, tree.NoteCount);
        var work = Assert.Single(tree.Children);
        Assert.Equal("업무", work.Name);
        Assert.Equal(1, work.NoteCount);
        Assert.Equal("업무/회의록", Assert.Single(work.Children).RelativePath);
    }

    [Fact]
    public void GetNotes_PinnedFirstThenRecent_WithPreview()
    {
        var old = _store.Create("", "old", ".md", "# 제목\n\n본문 첫 줄");
        File.SetLastWriteTime(_store.FullPath(old), new DateTime(2026, 1, 1));
        var recent = _store.Create("", "recent");
        var pinned = _store.Create("", "pinned");
        File.SetLastWriteTime(_store.FullPath(pinned), new DateTime(2025, 1, 1));
        _store.SetPinned(pinned, true);

        var notes = _store.GetNotes("");

        Assert.Equal([pinned, recent, old], notes.Select(n => n.RelativePath));
        Assert.True(notes[0].IsPinned);
        Assert.Equal("제목 · 본문 첫 줄", notes[2].Preview);
    }

    [Fact]
    public void RenameAndMove_KeepPins()
    {
        _store.CreateFolder("", "보관");
        var note = _store.Create("", "a");
        _store.SetPinned(note, true);

        var renamed = _store.Rename(note, "b");
        var moved = _store.Move(renamed, "보관");

        Assert.Equal("보관/b.md", moved);
        Assert.True(_store.GetNotes("보관").Single().IsPinned);
    }

    [Fact]
    public void RenameFolder_KeepsPinsInside()
    {
        _store.CreateFolder("", "업무");
        _store.SetPinned(_store.Create("업무", "a"), true);

        _store.RenameFolder("업무", "회사");

        Assert.True(_store.GetNotes("회사").Single().IsPinned);
    }

    [Fact]
    public void DeleteAndRestore_ReturnsToOriginalFolder()
    {
        _store.CreateFolder("", "개인");
        var note = _store.Create("개인", "장보기", ".txt", "우유");

        _store.Delete(note);
        Assert.Empty(_store.GetNotes("개인"));
        var trashed = Assert.Single(_store.GetTrash());
        Assert.Equal("개인/장보기.txt", trashed.OriginalPath);
        Assert.Equal(new DateTime(2026, 10, 28, 9, 0, 0), trashed.DeletedAt);

        var restored = _store.Restore(trashed.TrashPath);
        Assert.Equal("개인/장보기.txt", restored);
        Assert.Equal("우유", _store.Read(restored));
        Assert.Empty(_store.GetTrash());
    }

    [Fact]
    public void DeleteFolder_MovesWholeFolderToTrash()
    {
        _store.Create(_store.CreateFolder("", "임시"), "a");

        _store.DeleteFolder("임시");

        Assert.Empty(_store.GetFolderTree().Children);
        Assert.Equal("임시/a.md", Assert.Single(_store.GetTrash()).OriginalPath);
    }

    [Fact]
    public void PurgeTrash_RemovesOnlyOldEntries()
    {
        _store.Delete(_store.Create("", "old"));
        _time.Advance(TimeSpan.FromDays(20));
        _store.Delete(_store.Create("", "new"));
        _time.Advance(TimeSpan.FromDays(15));

        Assert.Equal(1, _store.PurgeTrash());
        Assert.Equal("new.md", Assert.Single(_store.GetTrash()).OriginalPath);
    }

    [Fact]
    public void Search_FindsTitleAndBody_WithSnippet()
    {
        _store.Create(_store.CreateFolder("", "업무"), "주간회의", ".md", "참석: 김팀장\n디자인 리뷰 일정 확정");
        _store.Create("", "디자인 아이디어", ".md", "색상");
        _store.Delete(_store.Create("", "삭제된 디자인"));

        var results = _store.Search("디자인");

        Assert.Equal(["디자인 아이디어", "주간회의"], results.Select(r => r.Note.Title));
        Assert.Contains("디자인 리뷰", results[1].Snippet);
        Assert.Single(_store.Search("디자인", "업무"));
    }

    [Fact]
    public void Paths_CannotEscapeNotesRoot()
    {
        Assert.Throws<ArgumentException>(() => _store.Read("../data.db"));
        Assert.Throws<ArgumentException>(() => _store.Create("../..", "x"));
    }

    [Fact]
    public void Changed_RaisedOnWrites()
    {
        var count = 0;
        _store.Changed += (_, _) => count++;

        var note = _store.Create("", "a");
        _store.Save(note, "x");
        _store.Rename(note, "b");

        Assert.Equal(3, count);
    }

    [Fact]
    public void Session_RoundTrips()
    {
        new NoteSession([new NoteTabState("업무/a.md", 12), new NoteTabState("b.txt", 0)], "b.txt", "업무", WordWrap: false).Save(_state);

        var session = NoteSession.Load(_state);

        Assert.Equal(2, session.Tabs.Count);
        Assert.Equal(12, session.Tabs[0].CaretIndex);
        Assert.Equal("b.txt", session.ActivePath);
        Assert.Equal("업무", session.Folder);
        Assert.False(session.WordWrap);
    }

    [Theory]
    [InlineData("- [ ] 디자인 리뷰 자료 준비", "디자인 리뷰 자료 준비")]
    [InlineData("  * [x] 일정 공유", "일정 공유")]
    [InlineData("1. 거래처 견적 요청", "거래처 견적 요청")]
    [InlineData("## 할 일", "할 일")]
    [InlineData("그냥 한 줄", "그냥 한 줄")]
    [InlineData("   ", null)]
    [InlineData("- [ ]", null)]
    public void ExtractTaskTitle(string line, string? expected) => Assert.Equal(expected, NoteText.ExtractTaskTitle(line));

    [Fact]
    public void IsChecked_DetectsDoneItems()
    {
        Assert.True(NoteText.IsChecked("- [x] 끝남"));
        Assert.False(NoteText.IsChecked("- [ ] 아직"));
    }

    [Fact]
    public void Save_KeepsVersionsAtMostEveryInterval()
    {
        var note = _store.Create("", "기록", ".md", "v1");

        _store.Save(note, "v2");                    // v1을 남긴다
        _time.Advance(TimeSpan.FromMinutes(3));
        _store.Save(note, "v3");                    // 10분이 안 지나서 남기지 않는다
        _store.Save(note, "v3");                    // 같은 내용
        _time.Advance(TimeSpan.FromMinutes(10));
        _store.Save(note, "v4");                    // v3을 남긴다

        var history = _store.GetHistory(note);
        Assert.Equal(["v3", "v1"], history.Select(v => _store.ReadVersion(note, v.Id)));
        Assert.Equal(new DateTime(2026, 10, 28, 9, 13, 0), history[0].SavedAt);
        Assert.DoesNotContain(_store.GetNotes(""), n => n.RelativePath.Contains(".history", StringComparison.Ordinal));
        Assert.Empty(_store.Search("v1"));          // 기록은 검색에 나오지 않는다
    }

    [Fact]
    public void RestoreVersion_KeepsCurrentContentAsAVersion()
    {
        var note = _store.Create("", "기록", ".md", "처음");
        _store.Save(note, "나중");
        var first = _store.GetHistory(note).Single();
        _time.Advance(TimeSpan.FromMinutes(1));

        _store.RestoreVersion(note, first.Id);

        Assert.Equal("처음", _store.Read(note));
        Assert.Equal(["나중", "처음"], _store.GetHistory(note).Select(v => _store.ReadVersion(note, v.Id)));
    }

    [Fact]
    public void History_KeepsOnlyRecentVersions()
    {
        var note = _store.Create("", "많이", ".md", "0");
        for (var i = 1; i <= NoteStore.HistoryKeep + 5; i++)
        {
            _time.Advance(NoteStore.HistoryInterval);
            _store.Save(note, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var history = _store.GetHistory(note);
        Assert.Equal(NoteStore.HistoryKeep, history.Count);
        Assert.Equal((NoteStore.HistoryKeep + 4).ToString(System.Globalization.CultureInfo.InvariantCulture), _store.ReadVersion(note, history[0].Id));
    }

    [Fact]
    public void History_FollowsRenameMoveAndFolderRename()
    {
        var folder = _store.CreateFolder("", "업무");
        var note = _store.Create(folder, "a", ".md", "v1");
        _store.Save(note, "v2");

        note = _store.Rename(note, "b");
        Assert.Single(_store.GetHistory(note));
        note = _store.Move(note, "");
        Assert.Single(_store.GetHistory(note));
        note = _store.Move(note, folder);
        var renamedFolder = _store.RenameFolder(folder, "보관");
        Assert.Single(_store.GetHistory(renamedFolder + "/b.md"));
    }

    [Fact]
    public void SaveImage_StoresBesideTheNote_HiddenFromList()
    {
        var folder = _store.CreateFolder("", "업무");
        var note = _store.Create(folder, "주간 회의", ".md");

        var first = _store.SaveImage(note, [1, 2, 3]);
        var second = _store.SaveImage(note, [4]);

        Assert.Equal(".images/주간-회의-20261028-090000.png", first);
        Assert.Equal(".images/주간-회의-20261028-090000-2.png", second);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_store.ResolveRelative(note, first)!));
        Assert.Equal(["주간 회의.md"], _store.GetNotes(folder).Select(n => Path.GetFileName(n.RelativePath)));
        Assert.DoesNotContain(_store.GetFolderTree().Children.Single().Children, c => c.Name.StartsWith('.'));
    }

    [Fact]
    public void ResolveRelative_StaysInsideNotes()
    {
        var note = _store.Create("", "a");

        Assert.Equal(_store.FullPath(".images/x y.png"), _store.ResolveRelative(note, ".images/x%20y.png"));
        Assert.Null(_store.ResolveRelative(note, "../../secret.png"));
        Assert.Null(_store.ResolveRelative(note, "https://example.com/a.png"));
        Assert.Null(_store.ResolveRelative(note, ""));
    }

    [Fact]
    public void Move_RewritesImageLinks()
    {
        var folder = _store.CreateFolder("", "업무");
        var note = _store.Create(folder, "회의", ".md");
        var image = _store.SaveImage(note, [1]);
        _store.Save(note, $"사진 ![화면]({image}) 그리고 ![웹](https://example.com/a.png) ![없음](.images/none.png)");

        var moved = _store.Move(note, "");

        Assert.Equal($"사진 ![화면](업무/{image}) 그리고 ![웹](https://example.com/a.png) ![없음](.images/none.png)", _store.Read(moved));
        Assert.NotNull(_store.ResolveRelative(moved, "업무/" + image));
    }

    [Fact]
    public void MoveFolder_MovesNotesPinsAndOutsideImageLinks()
    {
        _store.CreateFolder("", "업무");
        var sub = _store.CreateFolder("업무", "회의");
        _store.CreateFolder("", "보관");
        var top = _store.Create("", "표지", ".md");
        var outside = _store.SaveImage(top, [1]);
        var note = _store.Create(sub, "주간", ".md");
        var inside = _store.SaveImage(note, [2]);
        _store.Save(note, $"![안]({inside}) ![밖](../../{outside})");
        _store.SetPinned(note, true);

        var moved = _store.MoveFolder("업무", "보관");

        Assert.Equal("보관/업무", moved);
        Assert.False(Directory.Exists(_store.FullPath("업무")));
        var movedNote = "보관/업무/회의/주간.md";
        Assert.True(_store.GetNotes("보관/업무/회의").Single().IsPinned);
        Assert.Equal($"![안]({inside}) ![밖](../../../{outside})", _store.Read(movedNote));
        Assert.NotNull(_store.ResolveRelative(movedNote, "../../../" + outside));
    }

    [Fact]
    public void MoveFolder_SameParent_DoesNothing()
    {
        _store.CreateFolder("", "업무");

        Assert.Equal("업무", _store.MoveFolder("업무", ""));
        Assert.True(Directory.Exists(_store.FullPath("업무")));
    }

    [Fact]
    public void MoveFolder_IntoItselfOrOntoExistingName_Throws()
    {
        _store.CreateFolder("", "업무");
        _store.CreateFolder("업무", "하위");
        _store.CreateFolder("", "보관");
        _store.CreateFolder("보관", "업무");

        Assert.Throws<IOException>(() => _store.MoveFolder("업무", "업무"));
        Assert.Throws<IOException>(() => _store.MoveFolder("업무", "업무/하위"));
        Assert.Throws<IOException>(() => _store.MoveFolder("업무", "보관"));
        Assert.Throws<ArgumentException>(() => _store.MoveFolder("", "보관"));
    }

    [Fact]
    public void GetNotes_IncludeSubfolders_ListsNested_WithoutHiddenFolders()
    {
        var work = _store.CreateFolder("", "업무");
        var minutes = _store.CreateFolder(work, "회의록");
        _store.Create(work, "아이디어", ".md");
        var nested = _store.Create(minutes, "주간회의", ".md", "내용");
        _store.SaveImage(nested, [1]);
        _store.Save(nested, "고친 내용"); // 기록(.history)이 생겨도 목록에는 없다

        Assert.Equal(["업무/아이디어.md"], _store.GetNotes(work).Select(n => n.RelativePath));
        var all = _store.GetNotes(work, includeSubfolders: true).Select(n => n.RelativePath).ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains("업무/회의록/주간회의.md", all);
        Assert.DoesNotContain(_store.GetNotes("", includeSubfolders: true), n => n.RelativePath.Contains(".history", StringComparison.Ordinal)
            || n.RelativePath.Contains(".images", StringComparison.Ordinal));
    }
}
