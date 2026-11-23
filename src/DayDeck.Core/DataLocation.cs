using DayDeck.Core.IO;
using DayDeck.Core.Settings;

namespace DayDeck.Core;

/// <summary>
/// 데이터 폴더·메모 폴더를 다른 곳으로 옮긴다.
/// 데이터 폴더를 옮기면 기본 위치(<see cref="AppPaths.HomeDirectory"/>)에 <see cref="PointerFileName"/>을 두고
/// 새 위치를 한 줄로 적는다. Store 판은 기본 위치(LocalState)를 바꿀 수 없어서 이렇게 한다.
/// 옮기기는 앱을 끈 뒤(DB 연결을 모두 닫은 뒤)에 한다: 복사 → 확인 → 위치 기록 → 원래 파일 삭제.
/// </summary>
public static class DataLocation
{
    public const string PointerFileName = "data-location.txt";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>기본 위치에 적힌 데이터 폴더. 파일이 없거나 읽을 수 없으면 null.</summary>
    public static string? ReadPointer(string homeDirectory)
    {
        var path = Path.Combine(homeDirectory, PointerFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var line = File.ReadLines(path)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (line is null)
            {
                return null;
            }
            var expanded = Environment.ExpandEnvironmentVariables(line);
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>데이터 폴더 위치를 기록한다. 기본 위치 자신(또는 null)이면 기록을 지운다.</summary>
    public static void WritePointer(string homeDirectory, string? dataDirectory)
    {
        var path = Path.Combine(homeDirectory, PointerFileName);
        if (dataDirectory is null || SamePath(dataDirectory, homeDirectory))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }
        Directory.CreateDirectory(homeDirectory);
        AtomicFile.WriteAllText(path,
            "# DayDeck 데이터 폴더 위치. 이 파일을 지우면 이 폴더(기본 위치)의 데이터를 씁니다.\n"
            + Path.GetFullPath(dataDirectory) + "\n");
    }

    /// <summary>폴더에 DayDeck 데이터(DB 또는 설정)가 이미 있다.</summary>
    public static bool ContainsData(string directory) =>
        File.Exists(Path.Combine(directory, "data.db")) || File.Exists(Path.Combine(directory, "settings.json"));

    /// <summary>
    /// 사용자가 고른 폴더에서 실제로 쓸 데이터 폴더. 비었거나 DayDeck 데이터가 있으면 그 폴더,
    /// 다른 파일이 있으면(예: D:\ 나 문서 폴더) 그 안의 <c>DayDeck</c> 폴더.
    /// </summary>
    public static string ChooseDataTarget(string picked)
    {
        var full = Path.GetFullPath(picked);
        if (!Directory.Exists(full) || ContainsData(full) || !Directory.EnumerateFileSystemEntries(full).Any())
        {
            return full;
        }
        return Path.Combine(full, AppPaths.AppName);
    }

    /// <summary>옮길 수 없는 위치면 그 이유, 괜찮으면 null.</summary>
    public static string? ValidateTarget(string source, string target)
    {
        if (SamePath(source, target))
        {
            return "지금 쓰고 있는 폴더입니다.";
        }
        if (IsInside(target, source))
        {
            return "지금 폴더 안으로는 옮길 수 없습니다.";
        }
        if (IsInside(source, target))
        {
            return "지금 폴더를 품고 있는 폴더로는 옮길 수 없습니다. 그 안에 새 폴더를 만들어 고르세요.";
        }
        return null;
    }

    /// <summary>
    /// %APPDATA%·%LOCALAPPDATA% 아래. Store(MSIX) 판은 이 아래에 쓰는 파일이 패키지 안으로 가상화되어
    /// 탐색기에서 보이는 위치와 달라지므로 데이터·메모 폴더로 쓰지 않는다.
    /// </summary>
    public static bool IsUnderAppData(string path) =>
        new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath)
            .Where(folder => folder.Length > 0)
            .Any(folder => SamePath(path, folder) || IsInside(path, folder));

    /// <summary>데이터 폴더를 옮긴다 (앱이 꺼진 상태에서). 끝나면 기본 위치의 기록이 새 위치를 가리킨다.</summary>
    public static void MoveData(string source, string target, string homeDirectory)
    {
        var copied = CopyAll(source, target, overwrite: true);

        // 데이터 폴더 기준 상대 경로인데 폴더 밖을 가리키던 값(예: ..\Notes)은 옮긴 뒤에 깨지므로 절대 경로로 고정한다.
        var settingsPath = Path.Combine(target, "settings.json");
        if (File.Exists(settingsPath))
        {
            var store = new SettingsStore(settingsPath);
            var settings = store.Load();
            var notes = PinOutsidePath(settings.Notes.RootPath, source);
            var backup = PinOutsidePath(settings.Backup.Directory, source);
            if (notes is not null || backup is not null)
            {
                settings.Notes.RootPath = notes ?? settings.Notes.RootPath;
                settings.Backup.Directory = backup ?? settings.Backup.Directory;
                store.Save();
            }
        }

        WritePointer(homeDirectory, target);
        DeleteCopied(source, copied, removeRoot: !SamePath(source, homeDirectory));
    }

    /// <summary>
    /// 폴더 안의 파일을 모두 다른 폴더로 옮긴다 (메모 폴더). 같은 이름의 파일이 있으면 아무것도 옮기지 않고 예외를 낸다
    /// (<see cref="FindConflicts"/>로 먼저 확인).
    /// </summary>
    public static void MoveContents(string source, string target)
    {
        if (FindConflicts(source, target).FirstOrDefault() is { } conflict)
        {
            throw new IOException($"같은 이름의 파일이 이미 있습니다: {Path.Combine(target, conflict)}");
        }
        var copied = CopyAll(source, target, overwrite: false);
        DeleteCopied(source, copied, removeRoot: false);
    }

    /// <summary><paramref name="source"/>의 파일 중 <paramref name="target"/>에 같은 상대 경로로 이미 있는 것.</summary>
    public static IEnumerable<string> FindConflicts(string source, string target) =>
        !Directory.Exists(source) || !Directory.Exists(target)
            ? []
            : EnumerateFiles(source).Where(relative => File.Exists(Path.Combine(target, relative)));

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary><paramref name="path"/>가 <paramref name="directory"/>의 하위 경로 (같은 경로는 아님).</summary>
    public static bool IsInside(string path, string directory)
    {
        var dir = Normalize(directory);
        var full = Normalize(path);
        if (string.Equals(full, dir, PathComparison))
        {
            return false;
        }
        var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? PinOutsidePath(string? configured, string oldDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (Path.IsPathFullyQualified(expanded))
        {
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(oldDataDirectory, expanded));
        return IsInside(full, oldDataDirectory) ? null : full;
    }

    /// <summary>기본 위치의 위치 기록 파일만 빼고 모두 복사한다. 실패하면 이번에 복사한 파일을 지우고 예외를 다시 낸다.</summary>
    private static List<string> CopyAll(string source, string target, bool overwrite)
    {
        var copied = new List<string>();
        Directory.CreateDirectory(target);
        try
        {
            if (!Directory.Exists(source))
            {
                return copied;
            }
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
            }
            foreach (var relative in EnumerateFiles(source))
            {
                var from = Path.Combine(source, relative);
                var to = Path.Combine(target, relative);
                File.Copy(from, to, overwrite);
                copied.Add(relative);
                if (new FileInfo(to).Length != new FileInfo(from).Length)
                {
                    throw new IOException($"복사한 파일의 크기가 다릅니다: {to}");
                }
            }
            return copied;
        }
        catch
        {
            foreach (var relative in copied)
            {
                TryDelete(Path.Combine(target, relative));
            }
            throw;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string source) =>
        Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file))
            .Where(relative => !string.Equals(relative, PointerFileName, PathComparison));

    /// <summary>복사를 마친 원래 파일을 지운다. 못 지운 파일은 남겨 둔다 (데이터는 이미 새 위치에 있다).</summary>
    private static void DeleteCopied(string source, IEnumerable<string> copied, bool removeRoot)
    {
        foreach (var relative in copied)
        {
            TryDelete(Path.Combine(source, relative));
        }
        if (!Directory.Exists(source))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            TryDeleteEmptyDirectory(directory);
        }
        if (removeRoot)
        {
            TryDeleteEmptyDirectory(source);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
