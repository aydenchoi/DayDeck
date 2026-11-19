using System.Text;

namespace DayDeck.Core.IO;

/// <summary>
/// 저장 중 전원이 꺼지거나 프로세스가 죽어도 기존 파일이 깨지지 않도록
/// 임시 파일에 먼저 쓰고 디스크에 플러시한 뒤 원본과 교체한다.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>교체가 거부될 때 시도하는 횟수 (모두 합쳐 0.4초 남짓 기다린다).</summary>
    private const int MoveAttempts = 6;

    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // File.Move(overwrite)는 Windows에서 MoveFileEx(MOVEFILE_REPLACE_EXISTING)로 동작해 교체가 원자적이다.
            // 다른 곳(백신, 기록 복사, 다른 창의 다시 읽기)이 잠깐 열고 있으면 거부되므로 몇 번 다시 해 본다.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
