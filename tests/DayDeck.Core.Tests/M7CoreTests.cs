using System.IO.Compression;
using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.Exchange;
using DayDeck.Core.Notes;
using DayDeck.Core.Search;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

public sealed class M7CoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new();
    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly TaskRepository _tasks;
    private readonly EventRepository _events;
    private readonly NoteStore _notes;
    private readonly AppStateStore _state;

    public M7CoreTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        _paths = new AppPaths(_dir.Combine("Data"), isPortable: true);
        _paths.EnsureCreated();
        _db = new Database(_paths.DatabasePath);
        _db.Migrate();
        _tasks = new TaskRepository(_db, _time);
        _events = new EventRepository(_db, _time);
        _state = new AppStateStore(_db);
        _notes = new NoteStore(_paths.DefaultNotesRoot, _state, _time);
    }

    public void Dispose() => _dir.Dispose();

    // ---------- 검색 ----------

    [Fact]
    public void Search_FindsTasksEventsAndNotes_BySubstring()
    {
        _tasks.Add(new TaskItem { Title = "디자인리뷰 자료", Notes = "피드백" });
        _tasks.Add(new TaskItem { Title = "장보기", Notes = "디자인 잡지 사기" });
        _events.Add(new CalendarEvent { Title = "디자인 리뷰", Start = new(2026, 10, 30, 11, 0, 0), End = new(2026, 10, 30, 12, 0, 0), Location = "회의실 B" });
        _notes.Create("", "아이디어", ".md", "새 디자인 시안");
        var deleted = _tasks.Add(new TaskItem { Title = "삭제된 디자인" });
        _tasks.Delete(deleted);

        var hits = new SearchService(_db, _notes).Search("디자인");

        Assert.Equal(["디자인리뷰 자료", "장보기"], hits.Where(h => h.Kind == SearchHitKind.Task).Select(h => h.Title));
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Event && h.Detail.Contains("회의실 B"));
        Assert.Contains(hits, h => h.Kind == SearchHitKind.Note && h.NotePath == "아이디어.md");
        Assert.Contains("디자인 잡지", hits.Single(h => h.Title == "장보기").Detail);
    }

    [Fact]
    public void Search_MatchesAndShowsVisibleTextOfMarkdown()
    {
        _tasks.Add(new TaskItem { Title = "보고서", Notes = "- [ ] **중요**한 <span style=\"color:#C42B1C\">빨간</span> [자료](https://example.com/x)" });
        _tasks.Add(new TaskItem { Title = "태그만", Notes = "<span style=\"color:#C42B1C\">다른 글</span>" });
        _notes.Create("", "메모", ".md", "# 회의\n\n**굵**게 쓴 <mark>핵심</mark>");

        var hits = new SearchService(_db, _notes).Search("중요한 빨간 자료");
        Assert.Equal("보고서", hits.Single().Title);
        Assert.Contains("중요한 빨간 자료", hits.Single().Detail);
        Assert.DoesNotContain("**", hits.Single().Detail);

        Assert.Empty(new SearchService(_db, _notes).Search("span")); // 태그·주소는 찾지 않는다
        Assert.Empty(new SearchService(_db, _notes).Search("example.com"));

        var note = new SearchService(_db, _notes).Search("굵게 쓴 핵심").Single();
        Assert.Equal(SearchHitKind.Note, note.Kind);
        Assert.Contains("굵게 쓴 핵심", note.Detail);
        Assert.DoesNotContain("<mark>", note.Detail);
    }

    [Fact]
    public void Search_EscapesLikeWildcards()
    {
        _tasks.Add(new TaskItem { Title = "100% 완료" });
        _tasks.Add(new TaskItem { Title = "1000 완료" });

        Assert.Equal(["100% 완료"], new SearchService(_db, null).Search("0%").Select(h => h.Title));
    }

    // ---------- 백업 ----------

    [Fact]
    public void Backup_ContainsDbSettingsAndNotes_AndKeeps14()
    {
        File.WriteAllText(_paths.SettingsPath, "{}");
        _tasks.Add(new TaskItem { Title = "백업될 할 일" });
        _notes.Create(_notes.CreateFolder("", "업무"), "회의", ".md", "내용");
        _notes.Delete(_notes.Create("", "지운 메모"));
        var backup = new BackupService(_paths, _db, _state, _time, () => _paths.DefaultNotesRoot);

        var zipPath = backup.CreateBackup();

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.Equal(["Notes/업무/회의.md", "data.db", "settings.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            var restoredDb = _dir.Combine("restored.db");
            zip.GetEntry("data.db")!.ExtractToFile(restoredDb);
            Assert.Contains(new TaskRepository(new Database(restoredDb), _time).GetAll(), t => t.Title == "백업될 할 일");
        }

        for (var i = 0; i < 16; i++)
        {
            _time.Advance(TimeSpan.FromDays(1));
            Assert.NotNull(backup.BackupIfDue());
        }
        Assert.Equal(BackupService.KeepCount, backup.GetBackups().Count);
        Assert.Null(backup.BackupIfDue()); // 같은 날 두 번 하지 않음
    }

    // ---------- ICS ----------

    [Fact]
    public void Ics_RoundTripsEventsAndTasks()
    {
        var work = _tasks.GetLists().Single(l => l.Name == "업무").Id;
        _events.Add(new CalendarEvent { Title = "주간회의, 1층", Start = new(2026, 11, 2, 9, 0, 0), End = new(2026, 11, 2, 10, 0, 0), RRule = "FREQ=WEEKLY;BYDAY=MO", Location = "회의실; A", Notes = "안건:\n1. 일정", ListId = work });
        _events.Add(new CalendarEvent { Title = "제주 여행", IsAllDay = true, Start = new(2026, 11, 3), End = new(2026, 11, 5) });
        var done = _tasks.Add(new TaskItem { Title = "보고서", DueDate = new(2026, 10, 30), DueTime = new(17, 0), Priority = Priority.High });
        _tasks.SetCompleted(done, true);
        _tasks.Add(new TaskItem { Title = "아주 긴 제목의 할 일입니다 — 줄 접기가 75바이트를 넘어야 해서 길게 씁니다 한글 한글 한글", DueDate = new(2026, 11, 1) });

        var ics = new IcsImporter(_tasks, _events, _time).ExportAll();
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));

        var parsed = IcsFormat.Import(ics);

        var meeting = parsed.Events.Single(e => e.RRule is not null);
        Assert.Equal("주간회의, 1층", meeting.Title);
        Assert.Equal("회의실; A", meeting.Location);
        Assert.Equal("안건:\n1. 일정", meeting.Notes);
        Assert.Equal("업무", meeting.ListName);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0), meeting.End);
        var trip = parsed.Events.Single(e => e.IsAllDay);
        Assert.Equal(new DateTime(2026, 11, 5), trip.End);
        var report = parsed.Tasks.Single(t => t.Title == "보고서");
        Assert.Equal(ItemStatus.Completed, report.Status);
        Assert.Equal(new TimeOnly(17, 0), report.DueTime);
        Assert.Equal(Priority.High, report.Priority);
        Assert.Contains(parsed.Tasks, t => t.Title.StartsWith("아주 긴 제목") && t.Title.EndsWith("한글 한글 한글"));
    }

    [Fact]
    public void Ics_ImportsFromOtherCalendars()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Google Inc//Google Calendar 70.9054//EN
            BEGIN:VEVENT
            DTSTART:20261105T010000Z
            DTEND:20261105T020000Z
            SUMMARY:UTC 회의
            BEGIN:VALARM
            TRIGGER:-PT10M
            ACTION:DISPLAY
            END:VALARM
            END:VEVENT
            BEGIN:VEVENT
            DTSTART;VALUE=DATE:20261225
            DTEND;VALUE=DATE:20261226
            SUMMARY:성탄절 모임
            DESCRIPTION:긴 설명이
              접혀 있음
            END:VEVENT
            BEGIN:VEVENT
            DTSTART:20261110T140000
            DURATION:PT1H30M
            SUMMARY:길이로 정한 일정
            END:VEVENT
            BEGIN:VEVENT
            SUMMARY:시작 없음
            END:VEVENT
            END:VCALENDAR
            """;

        var parsed = IcsFormat.Import(ics);

        Assert.Equal(3, parsed.Events.Count);
        Assert.Equal(1, parsed.Skipped);
        Assert.Equal(new DateTime(2026, 11, 5, 1, 0, 0), parsed.Events[0].Start); // 테스트 시간대 = UTC
        Assert.True(parsed.Events[1].IsAllDay);
        Assert.Equal(parsed.Events[1].Start, parsed.Events[1].End);
        Assert.Equal("긴 설명이 접혀 있음", parsed.Events[1].Notes);
        Assert.Equal(new DateTime(2026, 11, 10, 15, 30, 0), parsed.Events[2].End);
    }

    [Fact]
    public void Ics_ImportSkipsDuplicatesAndCreatesLists()
    {
        var importer = new IcsImporter(_tasks, _events, _time);
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20261105T100000\r\nSUMMARY:회의\r\nCATEGORIES:프로젝트\r\nEND:VEVENT\r\nBEGIN:VTODO\r\nSUMMARY:할 일\r\nDUE;VALUE=DATE:20261106\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";

        Assert.Equal(new ImportSummary(1, 1, 0, 0), importer.Import(ics));
        Assert.Equal(new ImportSummary(0, 0, 2, 0), importer.Import(ics));
        Assert.Contains(_tasks.GetLists(), l => l.Name == "프로젝트");
    }

    // ---------- CSV ----------

    [Fact]
    public void Csv_QuotesAndHeader()
    {
        _tasks.Add(new TaskItem { Title = "따옴표 \"테스트\", 쉼표", DueDate = new(2026, 10, 30), Notes = "여러\n줄" });

        var csv = CsvExport.Tasks(_tasks.GetAll());

        Assert.StartsWith("제목,목록,마감일", csv);
        Assert.Contains("\"따옴표 \"\"테스트\"\", 쉼표\",,2026-10-30,,,미완료,,,\"여러\n줄\",2026-10-28 09:00", csv);
    }

    // ---------- 휴지통 ----------

    [Fact]
    public void Trash_ListsRestoresAndPurges()
    {
        var trash = new ItemTrash(_db, _time);
        var parent = _tasks.Add(new TaskItem { Title = "상위" });
        _tasks.Add(new TaskItem { Title = "하위", ParentId = parent });
        var eventId = _events.Add(new CalendarEvent { Title = "일정", Start = new(2026, 11, 1, 9, 0, 0), End = new(2026, 11, 1, 10, 0, 0) });
        _tasks.Delete(parent);
        _time.Advance(TimeSpan.FromDays(20));
        _events.Delete(eventId);

        var items = trash.GetItems();
        Assert.Equal(["일정", "상위"], items.Select(i => i.Title));
        Assert.Equal(1, items[1].SubtaskCount);

        trash.Restore(parent);
        Assert.Single(_tasks.GetSubtasks(parent));
        _tasks.Delete(parent);

        _time.Advance(TimeSpan.FromDays(15)); // 일정은 15일, 상위는 방금 지움
        Assert.Equal(0, trash.Purge());
        _time.Advance(TimeSpan.FromDays(16));
        Assert.Equal(3, trash.Purge());
        Assert.Empty(trash.GetItems());
    }

    [Fact]
    public void Trash_DeleteForeverRemovesRemindersToo()
    {
        var trash = new ItemTrash(_db, _time);
        var id = _tasks.Add(new TaskItem { Title = "a", DueDate = new(2026, 11, 1) });
        var reminders = new Reminders.ReminderRepository(_db, _time, () => new Settings.ReminderSettings());
        reminders.AddRelative(id, 0);
        _tasks.Delete(id);

        trash.DeleteForever(id);

        Assert.Empty(reminders.GetForItem(id));
        Assert.Null(_tasks.Get(id));
    }
}
