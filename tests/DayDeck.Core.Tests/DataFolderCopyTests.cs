using DayDeck.Core.Data;
using DayDeck.Core.IO;
using DayDeck.Core.Settings;

namespace DayDeck.Core.Tests;

/// <summary>데이터 폴더를 통째로 복사하면 다른 PC(다른 경로)에서 그대로 열려야 한다.</summary>
public sealed class DataFolderCopyTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CopiedDataFolder_OpensWithAllData()
    {
        // PC A
        var pcA = new AppPaths(_dir.Combine("PC-A", "DayDeck"), isPortable: false);
        pcA.EnsureCreated();
        var settingsA = new SettingsStore(pcA.SettingsPath);
        settingsA.Load();
        settingsA.Current.AutoSave.IntervalMinutes = 5;
        settingsA.Current.Notes.RootPath = pcA.ToStoredNotesPath(Path.Combine(pcA.DataDirectory, "Notes"));
        settingsA.Save();

        var dbA = new Database(pcA.DatabasePath);
        dbA.Migrate();
        using (var connection = dbA.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO lists(name) VALUES ('공부')";
            command.ExecuteNonQuery();
        }
        AtomicFile.WriteAllText(Path.Combine(pcA.ResolveNotesRoot(settingsA.Current.Notes.RootPath), "회의록", "주간회의.md"), "# 주간회의");

        // 파일 복사만으로 PC B(다른 경로)로 이동. DB 연결이 풀에 남아 있는 상태로 복사해도 된다.
        CopyDirectory(pcA.DataDirectory, _dir.Combine("PC-B", "Users", "other", "DayDeck"));

        var pcB = new AppPaths(_dir.Combine("PC-B", "Users", "other", "DayDeck"), isPortable: false);
        var settingsB = new SettingsStore(pcB.SettingsPath).Load();
        var dbB = new Database(pcB.DatabasePath);
        dbB.Migrate();

        Assert.Equal(5, settingsB.AutoSave.IntervalMinutes);
        using (var connection = dbB.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM lists WHERE name = '공부'";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        var notesB = pcB.ResolveNotesRoot(settingsB.Notes.RootPath);
        Assert.StartsWith(pcB.DataDirectory, notesB);
        Assert.Equal("# 주간회의", File.ReadAllText(Path.Combine(notesB, "회의록", "주간회의.md")));
    }

    [Fact]
    public void SettingsFile_ContainsNoAbsolutePathsByDefault()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);
        var store = new SettingsStore(paths.SettingsPath);
        store.Load();
        store.Save();

        var json = File.ReadAllText(paths.SettingsPath);

        Assert.DoesNotContain(_dir.Path, json);
        Assert.DoesNotContain(":\\\\", json);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
