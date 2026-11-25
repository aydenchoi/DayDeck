namespace DayDeck.Core.Updates;

/// <summary>
/// 내려받은 새 파일을 실행 폴더에 넣는다. Windows는 실행 중인 exe를 덮어쓸 수는 없지만 이름은 바꿀 수 있으므로,
/// 지금 파일을 "이름.old"로 바꿔 두고 새 파일을 복사한다. 새 버전이 시작되면 .old를 지운다.
/// 중간에 실패하면 바꾼 파일을 모두 되돌린다. Data 폴더는 건드리지 않는다.
/// 백신 검사 등이 파일을 잠깐 잡고 있으면 공유 위반이 나므로, 그때는 몇 초 동안 다시 시도한다.
/// </summary>
public static class UpdateInstaller
{
    public const string OldSuffix = ".old";

    /// <summary>파일이 잠겨 있을 때 다시 시도하기 전에 기다리는 시간 (모두 합쳐 약 10초).</summary>
    internal static readonly TimeSpan[] RetryDelays =
        [.. new[] { 100, 250, 500, 1000, 1000, 2000, 2000, 3000 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    /// <summary>기다리기 (테스트에서 바꾼다).</summary>
    internal static Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

    public static void Apply(string newFilesDirectory, string appDirectory)
    {
        var sources = Directory.GetFiles(newFilesDirectory);
        if (sources.Length == 0)
        {
            throw new UpdateException("설치할 파일이 없습니다.");
        }
        var done = new List<(string Target, string? Backup)>();
        var current = "";
        try
        {
            foreach (var source in sources)
            {
                var target = Path.Combine(appDirectory, Path.GetFileName(source));
                string? backup = null;
                current = Path.GetFileName(target);
                if (File.Exists(target))
                {
                    backup = target + OldSuffix;
                    var oldBackup = backup;
                    if (File.Exists(oldBackup))
                    {
                        RetryWhileLocked(() => File.Delete(oldBackup));
                    }
                    var movedTarget = target;
                    RetryWhileLocked(() => File.Move(movedTarget, oldBackup));
                }
                done.Add((target, backup));
                var copyTarget = target;
                RetryWhileLocked(() => File.Copy(source, copyTarget));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(done);
            throw new UpdateException(ex is UnauthorizedAccessException
                ? "프로그램 폴더에 쓸 권한이 없습니다. 릴리스 페이지에서 내려받아 직접 바꿔 주세요."
                : $"새 파일을 넣지 못했습니다 ({current}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 다른 프로세스가 파일을 잠깐 잡고 있어(공유 위반·잠금 위반) 실패하면 <see cref="RetryDelays"/>만큼 기다렸다 다시 한다.
    /// 다른 오류나 시간을 다 쓴 뒤의 실패는 그대로 던진다.
    /// </summary>
    internal static void RetryWhileLocked(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < RetryDelays.Length && IsLocked(ex))
            {
                Diagnostics.Log.Info($"파일이 잠겨 있어 다시 시도 ({attempt + 1}/{RetryDelays.Length}): {ex.Message}");
                Sleep(RetryDelays[attempt]);
            }
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION(32), ERROR_LOCK_VIOLATION(33).</summary>
    internal static bool IsLocked(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    /// <summary>지난 업데이트에서 남은 .old 파일을 지운다 (아직 잠겨 있으면 다음에). 지운 개수.</summary>
    public static int CleanupOldFiles(string appDirectory)
    {
        var removed = 0;
        foreach (var old in Directory.EnumerateFiles(appDirectory, "*" + OldSuffix))
        {
            try
            {
                File.Delete(old);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 이전 프로세스가 아직 끝나는 중이면 잠겨 있다.
            }
        }
        return removed;
    }

    private static void Rollback(List<(string Target, string? Backup)> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (target, backup) = done[i];
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                if (backup is not null && File.Exists(backup))
                {
                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log.Warn($"업데이트 되돌리기 실패: {target}", ex);
            }
        }
    }
}
