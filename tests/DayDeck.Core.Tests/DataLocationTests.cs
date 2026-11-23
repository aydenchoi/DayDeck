using DayDeck.Core.Data;
using DayDeck.Core.IO;
using DayDeck.Core.Settings;

namespace DayDeck.Core.Tests;

/// <summary>데이터 폴더·메모 폴더 옮기기 (Store 판에서도 기본 위치 밖에 데이터를 둘 수 있게).</summary>
public sealed class DataLocationTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Pointer_RoundTripsAndClearsWhenPointingHome()
    {
        var home = _dir.Combine("Home");
        var target = _dir.Combine("D", "DayDeck");

        Assert.Null(DataLocation.ReadPointer(home));

        DataLocation.WritePointer(home, target);
        Assert.Equal(target, DataLocation.ReadPointer(home));

        DataLocation.WritePointer(home, home);
        Assert.False(File.Exists(Path.Combine(home, DataLocation.PointerFileName)));
        Assert.Null(DataLocation.ReadPointer(home));
    }

    [Fact]
    public void ReadPointer_IgnoresCommentsAndRelativePaths()
    {
        var home = _dir.Combine("Home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, DataLocation.PointerFileName), "# 설명\n\nrelative\\path\n");

        Assert.Null(DataLocation.ReadPointer(home));
    }

    [Fact]
    public void WithDataDirectory_KeepsHomeAndPackageFlag()
    {
        var home = AppPaths.ForPackage("DayDeck_abc", _dir.Path);

        var moved = home.WithDataDirectory(_dir.Combine("D", "DayDeck"));

        Assert.True(moved.IsPackaged);
        Assert.True(moved.IsRelocated);
        Assert.Equal(home.DataDirectory, moved.HomeDirectory);
        Assert.Equal(_dir.Combine("D", "DayDeck", "data.db"), moved.DatabasePath);
        Assert.False(home.IsRelocated);
        Assert.False(moved.WithDataDirectory(home.DataDirectory).IsRelocated);
    }

    [Fact]
    public void ChooseDataTarget_UsesSubfolderForNonEmptyFolder()
    {
        var empty = _dir.Combine("Empty");
        Directory.CreateDirectory(empty);
        var busy = _dir.Combine("Busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "other.txt"), "");
        var existing = _dir.Combine("Existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "settings.json"), "{}");

        Assert.Equal(empty, DataLocation.ChooseDataTarget(empty));
        Assert.Equal(_dir.Combine("Missing"), DataLocation.ChooseDataTarget(_dir.Combine("Missing")));
        Assert.Equal(Path.Combine(busy, "DayDeck"), DataLocation.ChooseDataTarget(busy));
        Assert.Equal(existing, DataLocation.ChooseDataTarget(existing));
    }

    [Fact]
    public void ValidateTarget_RejectsSameNestedAndParentFolders()
    {
        var source = _dir.Combine("Data");

        Assert.NotNull(DataLocation.ValidateTarget(source, source + Path.DirectorySeparatorChar));
        Assert.NotNull(DataLocation.ValidateTarget(source, Path.Combine(source, "Sub")));
        Assert.NotNull(DataLocation.ValidateTarget(source, _dir.Path));
        Assert.Null(DataLocation.ValidateTarget(source, _dir.Combine("Data2")));
    }

    [Fact]
    public void MoveData_CopiesEverythingWritesPointerAndRemovesOld()
    {
        var home = new AppPaths(_dir.Combine("Home"), isPortable: false);
        home.EnsureCreated();
        var settings = new SettingsStore(home.SettingsPath);
        settings.Load();
        settings.Current.AutoSave.IntervalMinutes = 7;
        settings.Save();
        var db = new Database(home.DatabasePath);
        db.Migrate();
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO lists(name) VALUES ('옮김')";
            command.ExecuteNonQuery();
        }
        AtomicFile.WriteAllText(Path.Combine(home.DefaultNotesRoot, "회의", "주간.md"), "# 주간");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var target = _dir.Combine("D", "DayDeck");
        DataLocation.MoveData(home.DataDirectory, target, home.HomeDirectory);

        Assert.Equal(target, DataLocation.ReadPointer(home.HomeDirectory));
        Assert.False(File.Exists(home.DatabasePath));
        Assert.False(File.Exists(home.SettingsPath));
        Assert.True(Directory.Exists(home.HomeDirectory)); // 위치 기록이 남아야 한다

        var moved = home.WithDataDirectory(target);
        Assert.Equal(7, new SettingsStore(moved.SettingsPath).Load().AutoSave.IntervalMinutes);
        Assert.Equal("# 주간", File.ReadAllText(Path.Combine(moved.DefaultNotesRoot, "회의", "주간.md")));
        var movedDb = new Database(moved.DatabasePath);
        movedDb.Migrate();
        using (var connection = movedDb.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM lists WHERE name = '옮김'";
            Assert.Equal(1L, command.ExecuteScalar());
        }
    }

    [Fact]
    public void MoveData_BackHome_RemovesPointerAndOldFolder()
    {
        var home = _dir.Combine("Home");
        var away = _dir.Combine("Away");
        Directory.CreateDirectory(home);
        AtomicFile.WriteAllText(Path.Combine(away, "settings.json"), "{}");
        DataLocation.WritePointer(home, away);

        DataLocation.MoveData(away, home, home);

        Assert.Null(DataLocation.ReadPointer(home));
        Assert.True(File.Exists(Path.Combine(home, "settings.json")));
        Assert.False(Directory.Exists(away));
    }

    [Fact]
    public void MoveData_PinsRelativePathsThatPointOutsideTheDataFolder()
    {
        var source = _dir.Combine("Old", "Data");
        var store = new SettingsStore(Path.Combine(source, "settings.json"));
        store.Load();
        store.Current.Notes.RootPath = Path.Combine("..", "MyNotes");
        store.Current.Backup.Directory = "Backups2";
        store.Save();

        var target = _dir.Combine("New");
        DataLocation.MoveData(source, target, source);

        var moved = new SettingsStore(Path.Combine(target, "settings.json")).Load();
        Assert.Equal(_dir.Combine("Old", "MyNotes"), moved.Notes.RootPath);
        Assert.Equal("Backups2", moved.Backup.Directory); // 데이터 폴더 안이면 상대 경로 그대로 따라간다
    }

    [Fact]
    public void MoveContents_MovesNotesButRefusesConflicts()
    {
        var source = _dir.Combine("Notes");
        var target = _dir.Combine("Vault");
        AtomicFile.WriteAllText(Path.Combine(source, "a.md"), "A");
        AtomicFile.WriteAllText(Path.Combine(source, ".trash", "old.md"), "old");

        DataLocation.MoveContents(source, target);

        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.md")));
        Assert.True(File.Exists(Path.Combine(target, ".trash", "old.md")));
        Assert.False(File.Exists(Path.Combine(source, "a.md")));
        Assert.True(Directory.Exists(source));

        AtomicFile.WriteAllText(Path.Combine(source, "a.md"), "new A");
        Assert.Equal(["a.md"], DataLocation.FindConflicts(source, target));
        Assert.Throws<IOException>(() => DataLocation.MoveContents(source, target));
        Assert.Equal("A", File.ReadAllText(Path.Combine(target, "a.md")));
        Assert.Equal("new A", File.ReadAllText(Path.Combine(source, "a.md")));
    }
}
