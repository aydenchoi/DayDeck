using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DayDeck.Core.IO;

namespace DayDeck.Core.Notes;

/// <summary>메모의 예전 내용 하나.</summary>
public sealed record NoteVersion(string Id, DateTime SavedAt, long Size);

/// <summary>
/// 메모 버전 기록과 이미지.
/// <list type="bullet">
/// <item>버전: 저장하기 직전 내용을 <c>.history/메모 경로/yyyyMMdd-HHmmss.확장자</c>로 남긴다. 자동 저장이 몇 초마다 일어나므로
/// <see cref="HistoryInterval"/>에 한 번만 남기고, 메모마다 최근 <see cref="HistoryKeep"/>개만 둔다.</item>
/// <item>이미지: 메모와 같은 폴더의 <c>.images</c>에 저장하고 메모에는 상대 경로 <c>![](.images/…png)</c>로 넣는다.
/// 점(.) 폴더라 메모 목록·검색에는 나오지 않고, 메모 폴더를 통째로 옮기면 함께 옮겨진다.</item>
/// </list>
/// </summary>
public sealed partial class NoteStore
{
    public const string HistoryFolderName = ".history";
    public const string ImagesFolderName = ".images";
    public const int HistoryKeep = 30;
    public static readonly TimeSpan HistoryInterval = TimeSpan.FromMinutes(10);

    private const string VersionFormat = "yyyyMMdd-HHmmss";

    public string HistoryRoot => Path.Combine(Root, HistoryFolderName);

    // ---------- 버전 기록 ----------

    /// <summary>예전 버전들 (최근 것부터).</summary>
    public IReadOnlyList<NoteVersion> GetHistory(string relative)
    {
        var folder = HistoryFolder(relative);
        if (!Directory.Exists(folder))
        {
            return [];
        }
        return [.. new DirectoryInfo(folder).GetFiles()
            .Select(f => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), VersionFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? new NoteVersion(f.Name, at, f.Length)
                : null)
            .OfType<NoteVersion>()
            .OrderByDescending(v => v.SavedAt)];
    }

    public string ReadVersion(string relative, string id) =>
        File.ReadAllText(Path.Combine(HistoryFolder(relative), Path.GetFileName(id)), Encoding.UTF8);

    /// <summary>예전 버전으로 되돌린다. 지금 내용은 먼저 버전으로 남겨서 되돌리기를 다시 되돌릴 수 있다.</summary>
    public void RestoreVersion(string relative, string id)
    {
        var content = ReadVersion(relative, id);
        AddVersion(relative, force: true);
        AtomicFile.WriteAllText(FullPath(relative), content, Utf8NoBom);
        OnChanged();
    }

    /// <summary>저장 직전: 내용이 바뀌고 마지막 버전에서 일정 시간이 지났으면 지금 파일을 버전으로 남긴다.</summary>
    private void SnapshotBeforeSave(string relative, string newContent)
    {
        var full = FullPath(relative);
        if (!File.Exists(full))
        {
            return;
        }
        try
        {
            if (File.ReadAllText(full, Encoding.UTF8) == newContent)
            {
                return;
            }
            AddVersion(relative, force: false);
        }
        catch (IOException)
        {
            // 기록은 부가 기능이다. 실패해도 저장은 계속한다.
        }
    }

    private void AddVersion(string relative, bool force)
    {
        var full = FullPath(relative);
        if (!File.Exists(full) || new FileInfo(full).Length == 0)
        {
            return; // 빈 메모는 남길 필요가 없다.
        }
        var now = _time.GetLocalNow().DateTime;
        var history = GetHistory(relative);
        if (!force && history.Count > 0 && now - history[0].SavedAt < HistoryInterval)
        {
            return;
        }
        var folder = HistoryFolder(relative);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, now.ToString(VersionFormat, CultureInfo.InvariantCulture) + Path.GetExtension(relative));
        File.Copy(full, target, overwrite: true);
        foreach (var old in GetHistory(relative).Skip(HistoryKeep))
        {
            File.Delete(Path.Combine(folder, old.Id));
        }
    }

    private string HistoryFolder(string relative) =>
        Path.Combine(HistoryRoot, Normalize(relative).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>이름 바꾸기·이동 때 기록도 따라 옮긴다 (메모 하나 또는 폴더 전체).</summary>
    private void MoveHistory(string from, string to)
    {
        try
        {
            var source = HistoryFolder(from);
            if (!Directory.Exists(source))
            {
                return;
            }
            var target = HistoryFolder(to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // 기록을 못 옮겨도 메모 자체는 이미 옮겨졌다.
        }
    }

    // ---------- 이미지 ----------

    /// <summary>
    /// 메모에 넣을 이미지를 저장하고, 메모에서 쓸 상대 경로(예: ".images/회의-20261028-090000.png")를 돌려준다.
    /// </summary>
    public string SaveImage(string noteRelative, byte[] data, string extension = ".png")
    {
        var noteFolder = ParentOf(noteRelative);
        var imagesFolder = Combine(noteFolder, ImagesFolderName);
        Directory.CreateDirectory(FullPath(imagesFolder));
        var stamp = _time.GetLocalNow().DateTime.ToString(VersionFormat, CultureInfo.InvariantCulture);
        // 공백이 있으면 마크다운 이미지 문법이 깨지므로 '-'로 바꾼다.
        var baseName = SanitizeName(Path.GetFileNameWithoutExtension(noteRelative)).Replace(' ', '-');
        var name = $"{baseName}-{stamp}";
        var file = Combine(imagesFolder, name + extension);
        for (var i = 2; File.Exists(FullPath(file)); i++)
        {
            file = Combine(imagesFolder, $"{name}-{i}{extension}");
        }
        File.WriteAllBytes(FullPath(file), data);
        return ImagesFolderName + "/" + Path.GetFileName(file);
    }

    /// <summary>이미지 파일을 메모 폴더의 .images로 복사한다 (끌어다 놓기, 파일 고르기).</summary>
    public string ImportImage(string noteRelative, string sourcePath) =>
        SaveImage(noteRelative, File.ReadAllBytes(sourcePath), Path.GetExtension(sourcePath).ToLowerInvariant());

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    };

    /// <summary>메모 안의 상대 경로를 실제 파일 경로로 (미리보기에서 이미지를 읽을 때). 메모 폴더 밖이면 null.</summary>
    public string? ResolveRelative(string noteRelative, string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out _) || link.Length == 0)
        {
            return null;
        }
        var decoded = Uri.UnescapeDataString(link);
        var full = Path.GetFullPath(Path.Combine(FullPath(ParentOf(noteRelative)), decoded.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>메모를 다른 폴더로 옮긴 뒤, 메모 안 이미지의 상대 경로를 새 위치 기준으로 고친다.</summary>
    private void RewriteImageLinksAfterMove(string movedRelative, string oldFolder)
    {
        if (!string.Equals(Path.GetExtension(movedRelative), ".md", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var full = FullPath(movedRelative);
        var content = File.ReadAllText(full, Encoding.UTF8);
        var newFolder = FullPath(ParentOf(movedRelative));
        var oldFolderFull = FullPath(oldFolder);
        var changed = false;
        var rewritten = ImageLink().Replace(content, match =>
        {
            var link = match.Groups["src"].Value;
            if (Uri.TryCreate(link, UriKind.Absolute, out _))
            {
                return match.Value;
            }
            var target = Path.GetFullPath(Path.Combine(oldFolderFull, Uri.UnescapeDataString(link).Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(target))
            {
                return match.Value;
            }
            changed = true;
            var relative = Path.GetRelativePath(newFolder, target).Replace(Path.DirectorySeparatorChar, '/').Replace(" ", "%20");
            return $"![{match.Groups["alt"].Value}]({relative})";
        });
        if (changed)
        {
            AtomicFile.WriteAllText(full, rewritten, Utf8NoBom);
        }
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)\)")]
    private static partial Regex ImageLink();
}
