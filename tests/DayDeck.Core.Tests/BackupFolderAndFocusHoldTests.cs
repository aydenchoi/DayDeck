using Microsoft.Extensions.Time.Testing;
using DayDeck.Core.Data;
using DayDeck.Core.Reminders;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Tests;

/// <summary>백업 위치 설정, 알림의 우선순위 정보 (집중 중 보류용).</summary>
public sealed class BackupFolderAndFocusHoldTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void BackupsDirectory_DefaultRelativeAbsoluteAndMissing()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: true);
        Assert.Equal(paths.BackupsDirectory, paths.ResolveBackupsDirectory(null));
        Assert.Equal(_dir.Combine("Data", "MyBackups"), paths.ResolveBackupsDirectory("MyBackups"));

        var external = _dir.Combine("External");
        Directory.CreateDirectory(external);
        Assert.Equal(external, paths.ResolveBackupsDirectory(external));
        // 다른 PC에 없는 절대 경로면 기본 폴더
        Assert.Equal(paths.BackupsDirectory, paths.ResolveBackupsDirectory(_dir.Combine("Unplugged")));

        // 데이터 폴더 안이면 상대 경로로 저장한다.
        Assert.Equal("MyBackups", paths.ToStoredPath(_dir.Combine("Data", "MyBackups")));
        Assert.Equal(external, paths.ToStoredPath(external));
    }

    [Fact]
    public void Backup_WritesToConfiguredFolder()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: true);
        paths.EnsureCreated();
        var db = new Database(paths.DatabasePath);
        db.Migrate();
        var target = _dir.Combine("Elsewhere");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var backup = new BackupService(paths, db, new AppStateStore(db), time, () => paths.DefaultNotesRoot, () => target);

        var zip = backup.CreateBackup();

        Assert.StartsWith(target, zip);
        Assert.Single(backup.GetBackups());
        Assert.Empty(Directory.GetFiles(paths.BackupsDirectory));
    }

    [Fact]
    public void DueReminder_CarriesTaskPriority()
    {
        var time = new FakeTimeProvider();
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        var tasks = new TaskRepository(db, time);
        var reminders = new ReminderRepository(db, time, () => new ReminderSettings());
        var id = tasks.Add(new TaskItem { Title = "물 주기", Priority = Priority.Low, DueDate = new DateOnly(2026, 10, 28), DueTime = new TimeOnly(9, 30) });
        reminders.AddRelative(id, 0);

        time.Advance(TimeSpan.FromMinutes(30));
        var due = Assert.Single(reminders.GetDue(time.GetLocalNow().DateTime));
        Assert.Equal(Priority.Low, due.Priority);
    }
}
