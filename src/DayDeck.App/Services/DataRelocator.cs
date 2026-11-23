using System.Diagnostics;
using System.IO;
using System.Windows;
using DayDeck.Core;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Settings;

namespace DayDeck.App.Services;

internal enum RelocationKind
{
    /// <summary>데이터 폴더 전체 (DB, 설정, 메모, 그림, 백업, 로그).</summary>
    Data,

    /// <summary>메모 폴더만.</summary>
    Notes,
}

/// <summary>
/// 앱을 끈 뒤에 할 폴더 옮기기. <paramref name="MoveFiles"/>가 false면 파일은 그대로 두고 새 폴더를 쓰도록 기록만 바꾼다
/// (이미 DayDeck 데이터가 있는 폴더, 이미 메모가 있는 폴더를 그대로 쓸 때).
/// </summary>
internal sealed record Relocation(RelocationKind Kind, string Source, string Target, bool MoveFiles);

/// <summary>
/// <see cref="Relocation"/>을 실행한다. Program이 App을 끝낸 뒤(DB 연결을 닫은 뒤), 다시 시작하기 전에 부른다.
/// 실패하면 지금 위치를 그대로 쓰고 알린다.
/// </summary>
internal static class DataRelocator
{
    public static void Run(Relocation relocation, AppPaths paths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (relocation.Kind == RelocationKind.Data)
            {
                if (relocation.MoveFiles)
                {
                    DataLocation.MoveData(relocation.Source, relocation.Target, paths.HomeDirectory);
                }
                else
                {
                    DataLocation.WritePointer(paths.HomeDirectory, relocation.Target);
                }
                Log.Initialize(Path.Combine(relocation.Target, "Logs"));
            }
            else
            {
                if (relocation.MoveFiles)
                {
                    DataLocation.MoveContents(relocation.Source, relocation.Target);
                }
                Directory.CreateDirectory(relocation.Target);
                var store = new SettingsStore(paths.SettingsPath);
                store.Load();
                store.Current.Notes.RootPath = DataLocation.SamePath(relocation.Target, paths.DefaultNotesRoot)
                    ? null
                    : paths.ToStoredNotesPath(relocation.Target);
                store.Save();
            }
            Log.Info($"{relocation.Kind} 폴더 {(relocation.MoveFiles ? "옮김" : "바꿈")}: {relocation.Source} → {relocation.Target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Log.Error($"{relocation.Kind} 폴더를 옮기지 못함: {relocation.Source} → {relocation.Target}", ex);
            var what = relocation.Kind == RelocationKind.Data ? "데이터 폴더" : "메모 폴더";
            MessageBox.Show($"{what}를 옮기지 못했습니다. 지금 위치를 그대로 씁니다.\n\n{relocation.Target}\n\n{ex.Message}",
                "DayDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// DayDeck을 새로 띄운다. Store 판은 설치 폴더의 exe를 직접 실행하면 패키지로 인식되지 않으므로
    /// 실행 별칭(<c>%LOCALAPPDATA%\Microsoft\WindowsApps\DayDeck.exe</c>)으로, 없으면 시작 메뉴 항목으로 띄운다.
    /// </summary>
    public static void Restart(string arguments)
    {
        try
        {
            if (PackageInfo.FamilyName is { } family)
            {
                var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "DayDeck.exe");
                if (File.Exists(alias))
                {
                    Process.Start(new ProcessStartInfo(alias, arguments) { UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{family}!App") { UseShellExecute = true });
                }
                return;
            }
            if (Environment.ProcessPath is { } exe)
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("폴더를 옮긴 뒤 다시 시작하지 못함", ex);
        }
    }
}
