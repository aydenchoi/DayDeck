using System.IO;
using Microsoft.Win32;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Services;

/// <summary>
/// Windows 로그인 시 자동 실행. 일반 판은 HKCU\...\Run (관리자 권한 필요 없음),
/// Microsoft Store(MSIX) 판은 패키지 매니페스트의 StartupTask를 켜고 끈다 (MSIX에서는 Run 키가 가상화되어 듣지 않는다).
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DayDeck";

    /// <summary>packaging/msix/AppxManifest.xml의 StartupTask TaskId와 같아야 한다.</summary>
    public const string StartupTaskId = "DayDeckStartup";

    public static string BuildCommand(string exePath, bool minimized) =>
        minimized ? $"\"{exePath}\" {StartupOptions.MinimizedArg}" : $"\"{exePath}\"";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// 설정에 맞춰 등록/해제한다. 실행 파일 위치가 바뀌었으면 경로도 갱신한다.
    /// </summary>
    public static void Apply(bool enabled, bool minimized)
    {
        if (PackageInfo.IsPackaged)
        {
            ApplyStartupTask(enabled);
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (exePath is null)
            {
                return;
            }

            var command = BuildCommand(exePath, minimized);
            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command);
                Log.Info($"자동 실행 등록: {command}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("자동 실행 설정 실패", ex);
        }
    }

    /// <summary>
    /// StartupTask로 시작되었는지 (MSIX 판의 로그인 자동 실행). StartupTask는 명령줄 인수를 넘기지 못해서
    /// "트레이에서 시작"은 이 값과 설정으로 정한다.
    /// </summary>
    public static bool LaunchedByStartupTask()
    {
#if STORE_BUILD
        if (!PackageInfo.IsPackaged)
        {
            return false;
        }
        try
        {
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind
                == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log.Warn("시작 방식 확인 실패", ex);
            return false;
        }
#else
        return false;
#endif
    }

    private static async void ApplyStartupTask(bool enabled)
    {
#if STORE_BUILD
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            if (enabled && task.State == Windows.ApplicationModel.StartupTaskState.Disabled)
            {
                var state = await task.RequestEnableAsync();
                Log.Info($"자동 실행(StartupTask) 켬: {state}");
            }
            else if (!enabled && task.State == Windows.ApplicationModel.StartupTaskState.Enabled)
            {
                task.Disable();
                Log.Info("자동 실행(StartupTask) 끔");
            }
            else if (enabled && task.State is Windows.ApplicationModel.StartupTaskState.DisabledByUser
                         or Windows.ApplicationModel.StartupTaskState.DisabledByPolicy)
            {
                // 작업 관리자 → 시작 앱에서 사용자가 끈 경우는 앱이 다시 켤 수 없다.
                Log.Warn($"자동 실행을 켤 수 없음: {task.State} (작업 관리자의 시작 앱에서 켜야 함)");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("자동 실행(StartupTask) 설정 실패", ex);
        }
#else
        // 일반 판으로 빌드된 exe를 MSIX에 넣은 경우: StartupTask API가 없어 아무것도 하지 않는다.
        Log.Warn($"MSIX 패키지지만 Store 빌드가 아니라 자동 실행을 설정하지 않음 (enabled={enabled})");
        await Task.CompletedTask;
#endif
    }
}
