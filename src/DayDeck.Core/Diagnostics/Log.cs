using System.Text;

namespace DayDeck.Core.Diagnostics;

/// <summary>
/// 의존성 없는 최소 파일 로거. 트레이 상주 앱이라 콘솔이 없으므로 오류 추적용으로만 쓴다.
/// 로그 파일이 1MB를 넘으면 <c>.old</c>로 한 번 돌려 놓는다.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _path = Path.Combine(logsDirectory, "app.log");
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var path = _path;
        if (path is null)
        {
            return;
        }

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(' ').Append(level).Append(' ').Append(message);
        if (ex is not null)
        {
            line.AppendLine().Append(ex);
        }
        line.AppendLine();

        try
        {
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, line.ToString());
            }
        }
        catch (IOException)
        {
            // 로그 실패로 앱이 죽으면 안 된다.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
