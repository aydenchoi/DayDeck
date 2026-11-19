using DayDeck.Core;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.Notes;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

// 사용법: dotnet run --project tools/DayDeck.DevSeed -- <데이터 폴더>
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: DayDeck.DevSeed <data-directory>");
    return 1;
}

var paths = new AppPaths(args[0], isPortable: true);
paths.EnsureCreated();

var settings = new SettingsStore(paths.SettingsPath);
settings.Load();
settings.Current.Startup.RunAtLogin = false;
// 테스트 중 새 버전 알림 창이 끼어들지 않도록 (UI 테스트는 "지금 확인"으로 따로 확인한다).
settings.Current.Updates.CheckAutomatically = false;
// 실행하자마자 브리핑이 보이도록
settings.Current.Reminders.DailyBriefingTime = "00:00";
settings.Save();

var db = new Database(paths.DatabasePath);
db.Migrate();
var repo = new TaskRepository(db, TimeProvider.System);

var now = DateTime.Now;
var today = DateOnly.FromDateTime(now);
var lists = repo.GetLists();
var personal = lists.First(l => l.Name == "개인").Id;
var work = lists.First(l => l.Name == "업무").Id;
var study = repo.AddList("공부", "#7A3FBF");

repo.Add(new TaskItem { Title = "분기 보고서 제출", ListId = work, Priority = Priority.High, DueDate = today.AddDays(-1), DueTime = new TimeOnly(18, 0) });
var inTwoHours = TimeOnly.FromDateTime(now.AddHours(2));
repo.Add(new TaskItem { Title = "팀 회의 (회의실 A)", ListId = work, Priority = Priority.Medium,
    DueDate = DateOnly.FromDateTime(now.AddHours(2)), DueTime = new TimeOnly(inTwoHours.Hour, 0) });
var design = repo.Add(new TaskItem { Title = "디자인 리뷰 자료 준비", ListId = work, Priority = Priority.Low, DueDate = today,
    Notes = "지난주 피드백 반영" });
repo.Add(new TaskItem { Title = "시안 3종 정리", ParentId = design, ListId = work });
var sub = repo.Add(new TaskItem { Title = "피드백 표 만들기", ParentId = design, ListId = work });
repo.SetCompleted(sub, true);
repo.Add(new TaskItem { Title = "발표 순서 정하기", ParentId = design, ListId = work });
repo.Add(new TaskItem { Title = "장보기 (우유, 계란)", ListId = personal, MyDay = today });
repo.Add(new TaskItem { Title = "영어 단어 30개", ListId = study, MyDay = today });
var standup = repo.Add(new TaskItem { Title = "스탠드업 미팅", ListId = work, MyDay = today });
repo.SetCompleted(standup, true);
var mail = repo.Add(new TaskItem { Title = "고객사 메일 회신", ListId = work, DueDate = today });
repo.SetCompleted(mail, true);
repo.Add(new TaskItem { Title = "치과 예약", ListId = personal, DueDate = today.AddDays(1), DueTime = new TimeOnly(16, 0), Tags = ["전화"] });
// 반복 할 일 + 태그 (오늘 화면 개수에 영향 없도록 3일 뒤)
repo.Add(new TaskItem { Title = "주간 업무 보고", ListId = work, DueDate = today.AddDays(3), RRule = "FREQ=WEEKLY", Tags = ["보고", "전화"] });
repo.Add(new TaskItem { Title = "디자인 리뷰 회의", ListId = work, Priority = Priority.High, DueDate = today.AddDays(2), DueTime = new TimeOnly(11, 0) });
repo.Add(new TaskItem { Title = "자동차 보험 갱신", ListId = personal, DueDate = today.AddDays(5) });
repo.Add(new TaskItem { Title = "건강검진 예약", ListId = personal, DueDate = today.AddDays(10) }); // 예정: 14일로 넓혀야 보임
var book = repo.Add(new TaskItem { Title = "책 반납", ListId = personal });

// 알림
var reminders = new ReminderRepository(db, TimeProvider.System, () => settings.Current.Reminders);
var teamMeeting = repo.GetToday().Concat(repo.GetUpcoming()).First(i => i.Title.StartsWith("팀 회의")).Id;
reminders.AddRelative(teamMeeting, -10);
// 곧 울릴 알림 (UI 테스트가 기다렸다가 확인한다)
var dentist = repo.GetUpcoming().First(i => i.Title == "치과 예약").Id;
reminders.AddAbsolute(dentist, now.AddSeconds(75));
// 앱이 꺼져 있는 동안 지난 알림: 1시간 전 시점에서 "30분 뒤/40분 뒤"로 만든 알림
var anHourAgo = new ShiftedTimeProvider(TimeSpan.FromHours(-1));
var past = new ReminderRepository(db, anHourAgo, () => settings.Current.Reminders);
var english = repo.GetToday().First(i => i.Title == "영어 단어 30개").Id;
past.AddAbsolute(english, now.AddMinutes(-30));
past.AddAbsolute(book, now.AddMinutes(-20));

// 달력 일정
var events = new EventRepository(db, TimeProvider.System);
var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).ToDateTime(TimeOnly.MinValue);
events.Add(new CalendarEvent { Title = "프로젝트 마감 주간", IsAllDay = true, Start = monday, End = monday.AddDays(4), ListId = work });
events.Add(new CalendarEvent { Title = "엄마 생신", IsAllDay = true, Start = today.AddDays(1).ToDateTime(TimeOnly.MinValue), End = today.AddDays(1).ToDateTime(TimeOnly.MinValue), Color = "#00807A" });
events.Add(new CalendarEvent { Title = "가족 여행", IsAllDay = true, Start = monday.AddDays(12), End = monday.AddDays(13), ListId = personal });
var standupEvent = events.Add(new CalendarEvent
{
    Title = "스탠드업", Start = monday.AddHours(9), End = monday.AddHours(9).AddMinutes(15), ListId = work,
    RRule = "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
});
reminders.AddRelative(standupEvent, -5);
var lunch = events.Add(new CalendarEvent { Title = "점심 약속", Start = today.ToDateTime(new TimeOnly(12, 0)), End = today.ToDateTime(new TimeOnly(13, 0)), Location = "회사 앞", ListId = personal });
events.SetCompleted(lunch, true);
events.Add(new CalendarEvent { Title = "디자인 리뷰", Start = today.AddDays(2).ToDateTime(new TimeOnly(11, 0)), End = today.AddDays(2).ToDateTime(new TimeOnly(12, 30)), Location = "회의실 B", ListId = work });

// 메모 (폴더와 파일)
var notes = new NoteStore(paths.ResolveNotesRoot(settings.Current.Notes.RootPath), new AppStateStore(db), TimeProvider.System);
var workFolder = notes.CreateFolder("", "업무");
var minutes = notes.CreateFolder(workFolder, "회의록");
notes.Create(minutes, $"{today:yyyy-MM-dd} 주간회의", ".md",
    $"# 주간회의 {today:yyyy-MM-dd}\n참석: 김팀장, 이대리, 박사원\n\n## 결정 사항\n- 디자인 리뷰는 금요일 11시로 확정\n\n## 할 일\n- [ ] 디자인 리뷰 자료 준비\n- [x] 일정 공유 메일 발송\n- [ ] 거래처 견적 요청\n");
var idea = notes.Create(workFolder, "아이디어", ".md", "# 아이디어\n- 알림을 소리 대신 진동으로\n- 달력 색상 테마");
notes.SetPinned(idea, true);
notes.Create(notes.CreateFolder("", "개인"), "장보기", ".txt", "우유\n계란\n사과");
notes.Create("", "빠른 메모", ".md", "전화번호 010-0000-0000");

Console.WriteLine($"seeded {paths.DataDirectory}");
return 0;

internal sealed class ShiftedTimeProvider(TimeSpan shift) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;

    public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;
}
