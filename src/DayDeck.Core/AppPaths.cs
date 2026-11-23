namespace DayDeck.Core;

/// <summary>
/// 앱이 사용하는 파일/폴더 경로. 사용자 데이터(설정, DB, 메모, 백업)는 모두 <see cref="DataDirectory"/>
/// 한 폴더 아래에 둔다. 이 폴더를 통째로 복사하면 다른 PC에서도 그대로 쓸 수 있다.
/// 기본 위치는 실행 파일 폴더 아래 <c>Data</c>다 (exe와 Data를 함께 옮기면 끝).
/// 실행 파일 폴더에 쓸 수 없을 때(Program Files 등)만 <c>%APPDATA%\DayDeck</c>를 쓴다.
/// Microsoft Store(MSIX) 판은 설치 폴더가 읽기 전용이라 패키지 전용 폴더(LocalState)를 쓴다.
/// 사용자가 데이터 폴더를 다른 곳으로 옮기면 기본 위치(<see cref="HomeDirectory"/>)에 새 위치만 적어 둔다
/// (<see cref="DataLocation"/>).
/// </summary>
public sealed class AppPaths
{
    public const string AppName = "DayDeck";

    /// <summary>예전 버전에서 실행 폴더를 쓰게 하던 표시 파일. 이제는 없어도 실행 폴더가 기본이다.</summary>
    public const string PortableMarkerFileName = "portable.txt";
    public const string DataFolderName = "Data";
    public const string DefaultNotesFolderName = "Notes";

    public AppPaths(string dataDirectory, bool isPortable, bool isPackaged = false, string? homeDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        IsPortable = isPortable;
        IsPackaged = isPackaged;
        HomeDirectory = homeDirectory is null ? DataDirectory : Path.GetFullPath(homeDirectory);
    }

    public string DataDirectory { get; }

    /// <summary>
    /// 기본 데이터 폴더 (exe 옆 Data, %APPDATA%\DayDeck, Store 판은 LocalState). 데이터 폴더를 옮겼으면
    /// 여기에 새 위치를 적은 <see cref="DataLocation.PointerFileName"/>이 있다. 옮기지 않았으면 <see cref="DataDirectory"/>와 같다.
    /// </summary>
    public string HomeDirectory { get; }

    /// <summary>사용자가 데이터 폴더를 기본 위치가 아닌 곳으로 옮겼다.</summary>
    public bool IsRelocated => !DataLocation.SamePath(HomeDirectory, DataDirectory);

    /// <summary>같은 기본 위치를 두고 데이터 폴더만 <paramref name="dataDirectory"/>로 바꾼 경로.</summary>
    public AppPaths WithDataDirectory(string dataDirectory) =>
        DataLocation.SamePath(dataDirectory, HomeDirectory)
            ? new AppPaths(HomeDirectory, IsPortable, IsPackaged)
            : new AppPaths(dataDirectory, isPortable: false, IsPackaged, HomeDirectory);

    /// <summary>데이터가 실행 파일 폴더에 있다 (기본). false면 %APPDATA%로 대신 쓰는 중.</summary>
    public bool IsPortable { get; }

    /// <summary>Microsoft Store(MSIX) 패키지로 설치되어 실행 중. 데이터는 패키지의 LocalState 폴더에 있다.</summary>
    public bool IsPackaged { get; }

    public string DatabasePath => Path.Combine(DataDirectory, "data.db");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string DefaultNotesRoot => Path.Combine(DataDirectory, DefaultNotesFolderName);
    public string BackupsDirectory => Path.Combine(DataDirectory, "Backups");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    /// <summary>워크플로우 그림 (.excalidraw) 폴더.</summary>
    public string DrawingsDirectory => Path.Combine(DataDirectory, "Drawings");

    /// <summary>실행 파일 폴더에 쓸 수 없을 때 쓰는 데이터 폴더 (%APPDATA%\DayDeck).</summary>
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>
    /// 데이터 폴더를 정한다: 실행 파일 폴더의 Data. 거기에 쓸 수 없으면 %APPDATA%\DayDeck.
    /// <paramref name="canWrite"/>는 테스트용 (기본: 실제로 폴더를 만들고 파일을 써 본다).
    /// </summary>
    public static AppPaths Resolve(string appBaseDirectory, Func<string, bool>? canWrite = null)
    {
        var local = Path.Combine(appBaseDirectory, DataFolderName);
        if ((canWrite ?? CanWrite)(local))
        {
            return new AppPaths(local, isPortable: true);
        }
        return new AppPaths(AppDataDirectory, isPortable: false);
    }

    /// <summary>
    /// MSIX 패키지로 실행 중일 때의 데이터 폴더: <c>%LOCALAPPDATA%\Packages\{패키지 패밀리 이름}\LocalState</c>.
    /// 가상화되지 않은 실제 경로라 탐색기에서 열 수 있고, 앱을 지우면 함께 지워진다.
    /// </summary>
    public static AppPaths ForPackage(string packageFamilyName, string? localAppData = null)
    {
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(root, "Packages", packageFamilyName, "LocalState"), isPortable: false, isPackaged: true);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 설정의 메모 폴더 값을 실제 경로로 바꾼다. 상대 경로는 데이터 폴더 기준이라 복사해도 깨지지 않는다.
    /// 절대 경로가 이 PC에 없으면(다른 PC에서 복사해 온 경우 등) 기본 메모 폴더를 쓴다.
    /// </summary>
    public string ResolveNotesRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultNotesRoot;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (!Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(Path.Combine(DataDirectory, expanded));
        }

        return Directory.Exists(expanded) ? expanded : DefaultNotesRoot;
    }

    /// <summary>메모 폴더를 설정에 저장할 값으로 바꾼다. 데이터 폴더 안이면 상대 경로로 저장한다.</summary>
    /// <summary>
    /// 백업 폴더. 설정값이 없으면 기본 Backups\, 상대 경로면 데이터 폴더 기준.
    /// 절대 경로가 이 PC에 없으면(다른 PC로 옮긴 경우, 빠진 외장 디스크) 기본 폴더를 쓴다.
    /// </summary>
    public string ResolveBackupsDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return BackupsDirectory;
        }
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        if (!Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(Path.Combine(DataDirectory, expanded));
        }
        return Directory.Exists(expanded) ? expanded : BackupsDirectory;
    }

    /// <summary>설정에 저장할 경로: 데이터 폴더 안이면 상대 경로 (폴더째 옮겨도 깨지지 않게).</summary>
    public string ToStoredPath(string fullPath) => ToStoredNotesPath(fullPath);

    public string ToStoredNotesPath(string fullPath)
    {
        var relative = Path.GetRelativePath(DataDirectory, Path.GetFullPath(fullPath));
        var isInside = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                       && !Path.IsPathFullyQualified(relative);
        return isInside ? relative : fullPath;
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DefaultNotesRoot);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DrawingsDirectory);
    }
}
