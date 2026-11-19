namespace DayDeck.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Resolve_Default_UsesDataFolderNextToExe()
    {
        var paths = AppPaths.Resolve(_dir.Path);

        Assert.True(paths.IsPortable);
        Assert.Equal(_dir.Combine("Data"), paths.DataDirectory);
        Assert.Equal(_dir.Combine("Data", "data.db"), paths.DatabasePath);
        Assert.True(Directory.Exists(_dir.Combine("Data")));
        Assert.Empty(Directory.GetFiles(_dir.Combine("Data"))); // 쓰기 확인용 파일은 남기지 않음
    }

    [Fact]
    public void Resolve_ExeFolderNotWritable_FallsBackToAppData()
    {
        var paths = AppPaths.Resolve(_dir.Path, canWrite: _ => false);

        Assert.False(paths.IsPortable);
        Assert.Equal(AppPaths.AppDataDirectory, paths.DataDirectory);
        Assert.EndsWith(AppPaths.AppName, paths.DataDirectory);
    }

    [Fact]
    public void ForPackage_UsesPackageLocalState()
    {
        var paths = AppPaths.ForPackage("DayDeck_abc123", _dir.Path);

        Assert.True(paths.IsPackaged);
        Assert.False(paths.IsPortable);
        Assert.Equal(_dir.Combine("Packages", "DayDeck_abc123", "LocalState"), paths.DataDirectory);
        Assert.Equal(_dir.Combine("Packages", "DayDeck_abc123", "LocalState", "data.db"), paths.DatabasePath);
    }

    [Fact]
    public void AllUserData_LivesUnderDataDirectory()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        foreach (var path in new[] { paths.DatabasePath, paths.SettingsPath, paths.DefaultNotesRoot, paths.BackupsDirectory, paths.LogsDirectory })
        {
            Assert.StartsWith(paths.DataDirectory + Path.DirectorySeparatorChar, path);
        }
    }

    [Fact]
    public void EnsureCreated_CreatesFolders()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: true);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.DefaultNotesRoot));
        Assert.True(Directory.Exists(paths.BackupsDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveNotesRoot_Unset_UsesDefault(string? configured)
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        Assert.Equal(paths.DefaultNotesRoot, paths.ResolveNotesRoot(configured));
    }

    [Fact]
    public void ResolveNotesRoot_Relative_IsUnderDataDirectory()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        Assert.Equal(_dir.Combine("Data", "MyNotes"), paths.ResolveNotesRoot("MyNotes"));
    }

    [Fact]
    public void ResolveNotesRoot_ExistingAbsolute_IsKept()
    {
        var external = _dir.Combine("External");
        Directory.CreateDirectory(external);
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        Assert.Equal(external, paths.ResolveNotesRoot(external));
    }

    [Fact]
    public void ResolveNotesRoot_MissingAbsolute_FallsBackToDefault()
    {
        // 다른 PC의 절대 경로가 설정에 남아 있는 경우
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        Assert.Equal(paths.DefaultNotesRoot, paths.ResolveNotesRoot(_dir.Combine("NoSuchFolder")));
    }

    [Fact]
    public void ToStoredNotesPath_InsideDataDirectory_IsRelative()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);

        Assert.Equal(Path.Combine("Notes", "Work"), paths.ToStoredNotesPath(_dir.Combine("Data", "Notes", "Work")));
    }

    [Fact]
    public void ToStoredNotesPath_OutsideDataDirectory_IsAbsolute()
    {
        var paths = new AppPaths(_dir.Combine("Data"), isPortable: false);
        var outside = _dir.Combine("Elsewhere");

        Assert.Equal(outside, paths.ToStoredNotesPath(outside));
        Assert.Equal(_dir.Combine("Data2"), paths.ToStoredNotesPath(_dir.Combine("Data2")));
    }
}
