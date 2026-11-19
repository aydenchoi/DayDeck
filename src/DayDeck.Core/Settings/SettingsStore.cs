using System.Text.Json;
using System.Text.Json.Serialization;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.IO;

namespace DayDeck.Core.Settings;

/// <summary>settings.json을 읽고 쓴다. 파일이 없거나 깨져 있으면 기본값으로 시작한다.</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    /// <summary><see cref="Save"/>가 끝난 뒤 발생한다.</summary>
    public event EventHandler? Saved;

    public AppSettings Load()
    {
        AppSettings? loaded = null;
        if (File.Exists(_path))
        {
            try
            {
                using var stream = File.OpenRead(_path);
                loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings);
            }
            catch (JsonException ex)
            {
                // 깨진 파일은 덮어쓰기 전에 보관해 둔다. 사용자가 직접 고칠 수 있도록.
                var corruptCopy = _path + ".corrupt";
                Log.Warn($"settings.json을 읽을 수 없어 기본값을 사용합니다. 원본은 {corruptCopy}에 보관합니다.", ex);
                File.Copy(_path, corruptCopy, overwrite: true);
            }
        }

        Current = loaded ?? new AppSettings { Version = AppSettings.CurrentVersion };
        var migrated = Current.Migrate();
        Current.Normalize();
        if (migrated && loaded is not null)
        {
            // 올린 설정을 바로 파일에 남긴다 (사용자가 열어 봐도 같은 값).
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
            Log.Info($"설정을 버전 {AppSettings.CurrentVersion}으로 올림 (글자 크기 {Current.Ui.FontSize}pt)");
        }
        return Current;
    }

    public void Save()
    {
        Current.Normalize();
        var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
        AtomicFile.WriteAllText(_path, json);
        Saved?.Invoke(this, EventArgs.Empty);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
