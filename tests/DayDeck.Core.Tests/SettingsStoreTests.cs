using DayDeck.Core.Settings;

namespace DayDeck.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));

        var settings = store.Load();

        Assert.Equal(1, settings.AutoSave.IntervalMinutes);
        Assert.True(settings.Startup.RunAtLogin);
        Assert.True(settings.Tray.CloseToTray);
    }

    [Fact]
    public void Defaults_FontSize16()
    {
        var settings = new SettingsStore(_dir.Combine("settings.json")).Load();
        Assert.Equal(16, settings.Ui.FontSize);
        Assert.Equal(16, settings.Notes.EditorFontSize);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
    }

    [Fact]
    public void ShortcutToggles_DefaultOn_AndRoundTrip()
    {
        var path = _dir.Combine("settings.json");
        var store = new SettingsStore(path);
        var settings = store.Load();
        Assert.True(settings.Hotkeys.QuickAddEnabled && settings.Hotkeys.OpenMainEnabled && settings.Hotkeys.NewNoteEnabled);
        Assert.True(settings.Notes.FormatShortcuts);
        Assert.True(settings.Notes.PasteImages);

        settings.Hotkeys.OpenMainEnabled = false;
        settings.Notes.FormatShortcuts = false;
        store.Save();

        var reloaded = new SettingsStore(path).Load();
        Assert.False(reloaded.Hotkeys.OpenMainEnabled);
        Assert.Equal("Ctrl+Alt+T", reloaded.Hotkeys.OpenMain); // 꺼도 조합은 남는다
        Assert.False(reloaded.Notes.FormatShortcuts);
        Assert.True(reloaded.Notes.PasteImages);
    }

    [Fact]
    public void Load_OldFileWithOldDefault14_MovesTo16AndRewritesFile()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, """{ "ui": { "fontSize": 14 }, "notes": { "editorFontSize": 14 } }""");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(16, settings.Ui.FontSize);
        Assert.Equal(16, settings.Notes.EditorFontSize);
        Assert.Contains("\"version\": 2", File.ReadAllText(path));
        // 다시 읽어도 그대로 (한 번만 올림)
        Assert.Equal(16, new SettingsStore(path).Load().Ui.FontSize);
    }

    [Fact]
    public void Load_OldFileWithChosenSize_KeepsIt_AndNewFileWith14IsRespected()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, """{ "ui": { "fontSize": 18 } }""");
        Assert.Equal(18, new SettingsStore(path).Load().Ui.FontSize);

        // 이미 버전 2인 파일에서 사용자가 고른 14는 바꾸지 않는다.
        File.WriteAllText(path, """{ "version": 2, "ui": { "fontSize": 14 } }""");
        Assert.Equal(14, new SettingsStore(path).Load().Ui.FontSize);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = _dir.Combine("settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Current.AutoSave.IntervalMinutes = 5;
        store.Current.Startup.RunAtLogin = false;
        store.Current.Calendar.WeekStartsOn = DayOfWeek.Monday;
        store.Save();

        var reloaded = new SettingsStore(path).Load();

        Assert.Equal(5, reloaded.AutoSave.IntervalMinutes);
        Assert.False(reloaded.Startup.RunAtLogin);
        Assert.Equal(DayOfWeek.Monday, reloaded.Calendar.WeekStartsOn);
    }

    [Fact]
    public void Save_WritesCamelCaseJsonWithStringEnums()
    {
        var path = _dir.Combine("settings.json");
        var store = new SettingsStore(path);
        store.Load();
        store.Save();

        var json = File.ReadAllText(path);

        Assert.Contains("\"intervalMinutes\": 1", json);
        Assert.Contains("\"weekStartsOn\": \"Sunday\"", json);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(61, 60)]
    public void Load_ClampsAutoSaveInterval(int written, int expected)
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, $$"""{ "autoSave": { "intervalMinutes": {{written}} } }""");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(expected, settings.AutoSave.IntervalMinutes);
    }

    [Fact]
    public void Load_PartialFile_FillsMissingSectionsWithDefaults()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, """{ "tray": { "closeToTray": false }, "ui": null }""");

        var settings = new SettingsStore(path).Load();

        Assert.False(settings.Tray.CloseToTray);
        Assert.NotNull(settings.Ui);
        Assert.Equal(15, settings.Calendar.TimeSlotMinutes);
    }

    [Fact]
    public void Load_CorruptFile_UsesDefaultsAndKeepsCopy()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load();

        Assert.Equal(1, settings.AutoSave.IntervalMinutes);
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
    }

    [Fact]
    public void Load_AllowsCommentsAndTrailingCommas()
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, """
            {
              // 사용자가 직접 편집한 파일
              "autoSave": { "intervalMinutes": 10, },
            }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.Equal(10, settings.AutoSave.IntervalMinutes);
    }
}
