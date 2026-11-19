using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using DayDeck.App.Views;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Updates;

namespace DayDeck.App.Services;

/// <summary>
/// 새 버전 확인과 설치. 하루 한 번(설정에서 끌 수 있음) GitHub 릴리스를 확인하고, 새 버전이 있으면 창으로 묻는다.
/// 사용자가 "지금 업데이트"를 눌러야만 내려받아 백업 → 파일 바꾸기 → 다시 시작한다.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string SkippedKey = "updates.skipped";
    private const string LastCheckKey = "updates.last-check";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    private readonly App _app;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly UpdateClient _client;
    private readonly DispatcherTimer _timer;
    private UpdateWindow? _window;
    private bool _checking;

    public UpdateService(App app)
    {
        _app = app;
        _client = new UpdateClient(_http,
            Uri.TryCreate(Environment.GetEnvironmentVariable(UpdateClient.ApiOverrideVariable), UriKind.Absolute, out var api) ? api : null);
        // 시작 직후에는 네트워크가 아직 없을 수 있고 시작을 늦추지 않도록 2분 뒤 처음 확인한다. 그 뒤로는 한 시간마다 기한만 본다.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(2) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            await CheckIfDueAsync();
        };
    }

    public static Version CurrentVersion => ReleaseFeed.Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>게시된 단일 exe로 실행 중인지. 개발 빌드는 exe 옆에 DayDeck.dll이 있으며, 그때는 파일을 바꾸지 않는다.</summary>
    public static bool CanInstall =>
        !PackageInfo.IsPackaged && Environment.ProcessPath is not null
        && !File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(App).Assembly.GetName().Name + ".dll"));

    /// <summary>
    /// .NET 런타임을 exe 안에 넣은 판인지. 런타임이 따로 설치된 판이면 런타임 폴더가 dotnet 공유 폴더라 실행 폴더와 다르다.
    /// </summary>
    public static bool SelfContained =>
        string.Equals(Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()),
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);

    public Uri ReleasesPage => _client.ReleasesPage;

    /// <summary>Microsoft Store 판은 Store가 업데이트하므로 앱이 직접 확인·설치하지 않는다 (Store 정책).</summary>
    public static bool ManagedByStore => PackageInfo.IsPackaged;

    public const string StoreManagedMessage = "Microsoft Store에서 자동으로 업데이트됩니다.";

    public void Start()
    {
        if (!ManagedByStore)
        {
            _timer.Start();
        }
    }

    private async Task CheckIfDueAsync()
    {
        if (!_app.Settings.Current.Updates.CheckAutomatically)
        {
            return;
        }
        if (DateTime.TryParse(_app.AppState.Get(LastCheckKey), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last < CheckEvery)
        {
            return;
        }
        await CheckAsync(manual: false);
    }

    /// <summary>
    /// 새 버전을 확인하고, 있으면 묻는 창을 띄운다. 결과를 한 줄로 돌려준다 (설정 화면·트레이 표시용).
    /// 자동 확인은 "이 버전 건너뛰기"한 버전을 다시 묻지 않는다.
    /// </summary>
    public async Task<string> CheckAsync(bool manual)
    {
        if (ManagedByStore)
        {
            return StoreManagedMessage;
        }
        if (_checking)
        {
            return "확인하는 중입니다…";
        }
        _checking = true;
        try
        {
            var release = await _client.GetLatestAsync();
            _app.AppState.Set(LastCheckKey, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            var current = CurrentVersion.ToString(3);
            if (release is null || !ReleaseFeed.IsNewer(release.Version, CurrentVersion))
            {
                return $"최신 버전입니다 ({current}).";
            }
            var version = release.Version.ToString(3);
            if (!manual && _app.AppState.Get(SkippedKey) == version)
            {
                Log.Info($"건너뛰기로 한 버전 {version}");
                return $"새 버전 {version}은 건너뛰기로 했습니다.";
            }
            Log.Info($"새 버전 {version} (지금 {current})");
            ShowPrompt(release);
            return $"새 버전 {version}이 있습니다.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateException or JsonException)
        {
            Log.Warn("새 버전 확인 실패", ex);
            return ex is UpdateException ? ex.Message : "새 버전을 확인하지 못했습니다. 인터넷 연결을 확인하세요.";
        }
        finally
        {
            _checking = false;
        }
    }

    private void ShowPrompt(ReleaseInfo release)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }
        _window = new UpdateWindow(this, release);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    public void Skip(ReleaseInfo release) => _app.AppState.Set(SkippedKey, release.Version.ToString(3));

    /// <summary>내려받기 → 저장·백업 → 파일 바꾸기 → 다시 시작. 실패하면 <see cref="UpdateException"/> (바꾼 파일은 되돌린 상태).</summary>
    public async Task InstallAsync(ReleaseInfo release, IProgress<double> progress, Action<string> status)
    {
        if (release.PackageFor(SelfContained) is not { } package)
        {
            throw new UpdateException("이 설치 형태에 맞는 파일이 릴리스에 없습니다. 릴리스 페이지에서 내려받아 주세요.");
        }
        var exe = Environment.ProcessPath!;
        var appDirectory = Path.GetDirectoryName(exe)!;

        status("내려받는 중…");
        var work = Path.Combine(Path.GetTempPath(), "DayDeck-update");
        string files;
        try
        {
            files = await Task.Run(() => _client.DownloadAsync(release, package, work, progress));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("내려받지 못했습니다. 인터넷 연결을 확인하고 다시 시도하세요.", ex);
        }

        status("저장하고 백업하는 중…");
        _app.AutoSave.FlushAll();
        try
        {
            await Task.Run(_app.Backup.CreateBackup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 업데이트는 Data 폴더를 건드리지 않으므로 백업이 실패해도 계속한다.
            Log.Warn("업데이트 전 백업 실패", ex);
        }

        status("설치하는 중…");
        UpdateInstaller.Apply(files, appDirectory);
        Log.Info($"업데이트 설치: {CurrentVersion.ToString(3)} → {release.Version.ToString(3)}");
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
        _app.RestartAfterUpdate(exe);
    }

    /// <summary>새 버전으로 처음 시작했을 때: 지난 버전 파일(.old)을 지운다. 이전 프로세스가 끝날 때까지 잠깐 기다린다.</summary>
    public static void CleanupAfterUpdate()
    {
        var directory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!CanInstall || directory is null)
        {
            return;
        }
        Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 10 && Directory.EnumerateFiles(directory, "*" + UpdateInstaller.OldSuffix).Any(); attempt++)
            {
                UpdateInstaller.CleanupOldFiles(directory);
                await Task.Delay(1000);
            }
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
