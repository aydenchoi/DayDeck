using System.Globalization;
using System.IO.Compression;
using DayDeck.Core.Diagnostics;

namespace DayDeck.Core.Data;

/// <summary>
/// 자동 백업 (설계 §5.6): 하루 한 번 DB, 설정, 메모를 zip 하나로 묶어 Backups 폴더에 두고 최근 14개만 남긴다.
/// DB는 실행 중에도 일관된 사본을 얻도록 SQLite의 VACUUM INTO로 복사한다.
/// zip 안의 구조는 데이터 폴더와 같아서, 앱을 끄고 데이터 폴더에 풀면 그대로 복원된다.
/// </summary>
public sealed class BackupService
{
    public const int KeepCount = 14;
    public const string LastBackupKey = "backup.last";
    private const string Prefix = "DayDeck-";

    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly AppStateStore _state;
    private readonly TimeProvider _time;
    private readonly Func<string> _notesRoot;
    private readonly Func<string> _backupsDirectory;

    public BackupService(AppPaths paths, Database db, AppStateStore state, TimeProvider time, Func<string> notesRoot,
        Func<string>? backupsDirectory = null)
    {
        _paths = paths;
        _db = db;
        _state = state;
        _time = time;
        _notesRoot = notesRoot;
        _backupsDirectory = backupsDirectory ?? (() => paths.BackupsDirectory);
    }

    /// <summary>지금 쓰는 백업 폴더 (설정에 따라).</summary>
    public string BackupDirectory => _backupsDirectory();

    public DateTime? LastBackup =>
        DateTime.TryParseExact(_state.Get(LastBackupKey), "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>마지막 백업에서 하루가 지났으면 백업한다. 만들었으면 경로.</summary>
    public string? BackupIfDue()
    {
        var now = _time.GetLocalNow().DateTime;
        if (LastBackup is { } last && now - last < TimeSpan.FromDays(1))
        {
            return null;
        }
        return CreateBackup();
    }

    /// <summary>지금 백업한다. zip 경로를 돌려준다.</summary>
    public string CreateBackup()
    {
        var now = _time.GetLocalNow().DateTime;
        var directory = BackupDirectory;
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, $"{Prefix}{now:yyyyMMdd-HHmmss}.zip");
        var tempDb = Path.Combine(Path.GetTempPath(), $"daydeck-backup-{Guid.NewGuid():N}.db");

        try
        {
            using (var connection = _db.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", tempDb);
                command.ExecuteNonQuery();
            }

            var partial = zipPath + ".partial";
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, "data.db", CompressionLevel.Optimal);
                if (File.Exists(_paths.SettingsPath))
                {
                    zip.CreateEntryFromFile(_paths.SettingsPath, "settings.json", CompressionLevel.Optimal);
                }

                var notesRoot = _notesRoot();
                if (Directory.Exists(notesRoot))
                {
                    // 데이터 폴더 밖의 메모 폴더여도 zip 안에서는 Notes/ 아래에 둔다.
                    foreach (var file in Directory.EnumerateFiles(notesRoot, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(notesRoot, file).Replace('\\', '/');
                        if (relative.StartsWith(".trash/", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        zip.CreateEntryFromFile(file, "Notes/" + relative, CompressionLevel.Optimal);
                    }
                }

                // 워크플로우 그림 (Drawings/, 휴지통 제외).
                if (Directory.Exists(_paths.DrawingsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(_paths.DrawingsDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(_paths.DrawingsDirectory, file).Replace('\\', '/');
                        if (!relative.StartsWith(".trash/", StringComparison.Ordinal))
                        {
                            zip.CreateEntryFromFile(file, "Drawings/" + relative, CompressionLevel.Optimal);
                        }
                    }
                }
            }
            File.Move(partial, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                File.Delete(tempDb);
            }
        }

        _state.Set(LastBackupKey, now.ToString("s", CultureInfo.InvariantCulture));
        Prune();
        Log.Info($"백업 완료: {zipPath}");
        return zipPath;
    }

    public IReadOnlyList<FileInfo> GetBackups() =>
        Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory).GetFiles(Prefix + "*.zip").OrderByDescending(f => f.Name).ToList()
            : [];

    private void Prune()
    {
        foreach (var old in GetBackups().Skip(KeepCount))
        {
            old.Delete();
        }
    }
}
