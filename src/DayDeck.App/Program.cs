using System.Diagnostics;
using DayDeck.App.Services;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        if (StartupRegistration.LaunchedByStartupTask())
        {
            // Store 판의 로그인 자동 실행. 창을 띄울지는 설정을 읽은 뒤 App에서 정한다.
            options = options with { Minimized = true, LoginStartup = true };
        }

        using var instance = new SingleInstance();
        if (!instance.IsFirstInstance)
        {
            // 로그인 자동 실행이 중복으로 들어온 경우에는 조용히 끝낸다.
            if (options.QuickAdd)
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                SingleInstance.SendToFirstInstance(SingleInstance.QuickAddCommand);
            }
            else if (options.Today)
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                SingleInstance.SendToFirstInstance(SingleInstance.TodayCommand);
            }
            else if (!options.Minimized)
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                SingleInstance.SendToFirstInstance(SingleInstance.ShowCommand);
            }
            return 0;
        }

        var app = new App(options, instance);
        app.InitializeComponent();
        var exitCode = app.Run();
        if (app.RestartPath is { } exe)
        {
            // 업데이트를 설치했다: 단일 인스턴스를 먼저 놓아야 새 버전이 첫 인스턴스로 시작한다.
            instance.Dispose();
            var arguments = app.RestartShowWindow ? StartupOptions.UpdatedArg : $"{StartupOptions.UpdatedArg} {StartupOptions.MinimizedArg}";
            try
            {
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error("업데이트 후 다시 시작하지 못함", ex);
            }
        }
        return exitCode;
    }
}

internal sealed record StartupOptions(bool Minimized, bool QuickAdd, bool Today = false, bool Updated = false, bool LoginStartup = false)
{
    /// <summary>업데이트를 설치하고 다시 시작했다 (지난 버전 파일 정리, 알림).</summary>
    public const string UpdatedArg = "--updated";

    /// <summary>"오늘" 팝업만 연다 (트레이 아이콘 클릭과 같음).</summary>
    public const string TodayArg = "--today";

    public const string MinimizedArg = "--minimized";

    /// <summary>빠른 추가 창만 연다 (작업 표시줄 고정, 바로 가기 키 등에서 쓸 수 있다).</summary>
    public const string QuickAddArg = "--quick-add";

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var quickAdd = list.Any(a => string.Equals(a, QuickAddArg, StringComparison.OrdinalIgnoreCase));
        var today = list.Any(a => string.Equals(a, TodayArg, StringComparison.OrdinalIgnoreCase));
        var updated = list.Any(a => string.Equals(a, UpdatedArg, StringComparison.OrdinalIgnoreCase));
        return new(quickAdd || today || list.Any(a => string.Equals(a, MinimizedArg, StringComparison.OrdinalIgnoreCase)), quickAdd, today, updated);
    }
}
