using System.Text;
using System.Text.Json;
using DayDeck.Core.Data;
using DayDeck.Core.IO;

namespace DayDeck.Core.Notes;

/// <summary>메모 폴더 (Notes 아래 실제 폴더).</summary>
public sealed record NoteFolder(string Name, string RelativePath, IReadOnlyList<NoteFolder> Children, int NoteCount);

/// <summary>메모 목록의 한 항목. 본문은 열 때 읽는다 (미리보기만 담음).</summary>
public sealed record NoteInfo(string RelativePath, string Title, string Extension, string Preview, DateTime ModifiedAt, bool IsPinned)
{
    public string Folder => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? "";
}

/// <summary>휴지통 항목.</summary>
public sealed record TrashedNote(string TrashPath, string OriginalPath, DateTime DeletedAt);

public sealed record NoteSearchResult(NoteInfo Note, string Snippet);

/// <summary>
/// 메모 저장소 (설계 §4.3). 메모는 데이터베이스가 아니라 폴더와 .md/.txt 파일로 저장해서
/// 다른 편집기로도 열 수 있고, 데이터 폴더를 복사하면 그대로 옮겨진다.
/// 경로는 모두 Notes 기준 상대 경로이며 '/'로 구분한다. 고정(핀) 목록은 앱 상태(DB)에 둔다.
/// </summary>
public sealed partial class NoteStore
{
    public const string TrashFolderName = ".trash";
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private const string PinnedStateKey = "notes.pinned";
    private const int PreviewLength = 120;
    private static readonly string[] Extensions = [".md", ".txt"];
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly AppStateStore? _state;
    private readonly TimeProvider _time;

    public NoteStore(string root, AppStateStore? state, TimeProvider time)
    {
        Root = Path.GetFullPath(root);
        _state = state;
        _time = time;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>이 저장소를 통해 메모/폴더가 바뀌었다 (외부 편집은 FileSystemWatcher로 따로 감지).</summary>
    public event EventHandler? Changed;

    public string TrashRoot => Path.Combine(Root, TrashFolderName);

    // ---------- 폴더 ----------

    /// <summary>루트("")를 포함한 폴더 트리.</summary>
    public NoteFolder GetFolderTree() => BuildFolder(Root, "");

    public string CreateFolder(string parent, string name)
    {
        var relative = Combine(parent, SanitizeName(name));
        var full = FullPath(relative);
        if (Directory.Exists(full))
        {
            throw new IOException($"'{name}' 폴더가 이미 있습니다.");
        }
        Directory.CreateDirectory(full);
        OnChanged();
        return relative;
    }

    public string RenameFolder(string relative, string newName)
    {
        EnsureNotRoot(relative);
        var target = Combine(ParentOf(relative), SanitizeName(newName));
        Directory.Move(FullPath(relative), FullPath(target));
        RewritePins(relative, target, isFolder: true);
        MoveHistory(relative, target);
        OnChanged();
        return target;
    }

    /// <summary>폴더를 통째로 다른 폴더 안으로 옮긴다 (끌어다 놓기). 옮긴 폴더의 새 경로를 돌려준다.</summary>
    public string MoveFolder(string relative, string parent)
    {
        EnsureNotRoot(relative);
        var source = Normalize(relative);
        var destination = Normalize(parent);
        if (string.Equals(ParentOf(source), destination, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }
        if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("폴더를 자기 안으로 옮길 수 없습니다.");
        }
        var name = Path.GetFileName(source);
        var target = Combine(destination, name);
        if (Directory.Exists(FullPath(target)))
        {
            throw new IOException($"'{FolderDisplayName(destination)}' 안에 '{name}' 폴더가 이미 있습니다.");
        }
        Directory.Move(FullPath(source), FullPath(target));
        RewritePins(source, target, isFolder: true);
        MoveHistory(source, target);
        // 폴더 안끼리의 그림 경로는 그대로 맞고, 폴더 밖 그림(../)을 가리키던 경로만 새 위치 기준으로 고친다.
        foreach (var file in Directory.EnumerateFiles(FullPath(target), "*.md", SearchOption.AllDirectories).Where(f => !IsInHiddenFolder(f)))
        {
            var moved = ToRelative(file);
            RewriteImageLinksAfterMove(moved, source + ParentOf(moved)[target.Length..]);
        }
        OnChanged();
        return target;
    }

    private static string FolderDisplayName(string folder) => folder.Length == 0 ? "메모" : Path.GetFileName(folder);

    /// <summary>폴더를 통째로 휴지통으로 보낸다.</summary>
    public void DeleteFolder(string relative)
    {
        EnsureNotRoot(relative);
        var destination = TrashDestination(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(FullPath(relative), destination);
        RewritePins(relative, null, isFolder: true);
        OnChanged();
    }

    // ---------- 메모 ----------

    /// <summary>
    /// 폴더 안의 메모. <paramref name="includeSubfolders"/>면 하위 폴더의 메모도 (점으로 시작하는 폴더 — 그림·기록·휴지통 — 은 빼고).
    /// 고정 → 최근 수정 순.
    /// </summary>
    public IReadOnlyList<NoteInfo> GetNotes(string folder, bool includeSubfolders = false)
    {
        var full = FullPath(folder);
        if (!Directory.Exists(full))
        {
            return [];
        }

        var pinned = GetPinned();
        return Directory.EnumerateFiles(full, "*", includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(path => IsNoteFile(path) && !IsInHiddenFolder(path))
            .Select(path => ToInfo(path, pinned))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.ModifiedAt)
            .ToList();
    }

    public NoteInfo GetInfo(string relative) => ToInfo(FullPath(relative), GetPinned());

    public bool Exists(string relative) => File.Exists(FullPath(relative));

    public string Read(string relative) => File.ReadAllText(FullPath(relative), Encoding.UTF8);

    public DateTime GetModifiedTime(string relative) => File.GetLastWriteTime(FullPath(relative));

    /// <summary>원자적으로 저장한다 (설계 §5.6). 원래 파일에 BOM이 있었으면 유지한다.</summary>
    public void Save(string relative, string content)
    {
        var full = FullPath(relative);
        SnapshotBeforeSave(relative, content);
        var keepBom = File.Exists(full) && HasUtf8Bom(full);
        AtomicFile.WriteAllText(full, content, keepBom ? new UTF8Encoding(true) : Utf8NoBom);
        OnChanged();
    }

    /// <summary>새 메모를 만든다. 같은 이름이 있으면 " (2)"를 붙인다. 상대 경로를 돌려준다.</summary>
    public string Create(string folder, string title, string extension = ".md", string content = "")
    {
        var ext = Extensions.Contains(extension.ToLowerInvariant()) ? extension.ToLowerInvariant() : ".md";
        Directory.CreateDirectory(FullPath(folder));
        var relative = UniquePath(folder, SanitizeName(title), ext);
        AtomicFile.WriteAllText(FullPath(relative), content, Utf8NoBom);
        OnChanged();
        return relative;
    }

    /// <summary>확장자만 바꾼다 (.txt → .md 로 바꿔 서식을 쓰는 등). 같은 이름이 있으면 " (2)"를 붙인다.</summary>
    public string ChangeExtension(string relative, string extension)
    {
        var ext = extension.ToLowerInvariant();
        if (!Extensions.Contains(ext) || string.Equals(Path.GetExtension(relative), ext, StringComparison.OrdinalIgnoreCase))
        {
            return relative;
        }
        var target = UniquePath(ParentOf(relative), Path.GetFileNameWithoutExtension(relative), ext);
        File.Move(FullPath(relative), FullPath(target));
        RewritePins(relative, target, isFolder: false);
        MoveHistory(relative, target);
        OnChanged();
        return target;
    }

    public string Rename(string relative, string newTitle)
    {
        var ext = Path.GetExtension(relative);
        var current = Path.GetFileNameWithoutExtension(relative);
        var title = SanitizeName(newTitle);
        if (title == current)
        {
            return relative;
        }
        var target = UniquePath(ParentOf(relative), title, ext);
        File.Move(FullPath(relative), FullPath(target));
        RewritePins(relative, target, isFolder: false);
        MoveHistory(relative, target);
        OnChanged();
        return target;
    }

    public string Move(string relative, string folder)
    {
        if (ParentOf(relative) == Normalize(folder))
        {
            return relative;
        }
        var target = UniquePath(folder, Path.GetFileNameWithoutExtension(relative), Path.GetExtension(relative));
        File.Move(FullPath(relative), FullPath(target));
        RewritePins(relative, target, isFolder: false);
        MoveHistory(relative, target);
        RewriteImageLinksAfterMove(target, ParentOf(relative));
        OnChanged();
        return target;
    }

    /// <summary>휴지통으로 보낸다 (30일 보관). 휴지통 안의 경로를 돌려준다.</summary>
    public string Delete(string relative)
    {
        var destination = TrashDestination(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(FullPath(relative), destination);
        RewritePins(relative, null, isFolder: false);
        OnChanged();
        return destination;
    }

    public IReadOnlyList<TrashedNote> GetTrash()
    {
        if (!Directory.Exists(TrashRoot))
        {
            return [];
        }
        return Directory.EnumerateFiles(TrashRoot, "*", SearchOption.AllDirectories)
            .Where(IsNoteFile)
            .Select(path => new TrashedNote(path, OriginalPathOf(path), BucketTime(path)))
            .OrderByDescending(t => t.DeletedAt)
            .ToList();
    }

    /// <summary>휴지통에서 원래 폴더로 되돌린다. 복원된 상대 경로를 돌려준다.</summary>
    public string Restore(string trashPath)
    {
        var original = OriginalPathOf(trashPath);
        var target = UniquePath(ParentOf(original), Path.GetFileNameWithoutExtension(original), Path.GetExtension(original));
        Directory.CreateDirectory(Path.GetDirectoryName(FullPath(target))!);
        File.Move(trashPath, FullPath(target));
        DeleteEmptyTrashFolders();
        OnChanged();
        return target;
    }

    /// <summary>보관 기간이 지난 휴지통 항목을 영구 삭제한다. 지운 개수.</summary>
    public int PurgeTrash()
    {
        if (!Directory.Exists(TrashRoot))
        {
            return 0;
        }
        var cutoff = _time.GetLocalNow().DateTime - TrashRetention;
        var removed = 0;
        foreach (var bucket in Directory.EnumerateDirectories(TrashRoot))
        {
            if (BucketTime(bucket) < cutoff)
            {
                removed += Directory.EnumerateFiles(bucket, "*", SearchOption.AllDirectories).Count();
                Directory.Delete(bucket, recursive: true);
            }
        }
        return removed;
    }

    // ---------- 고정 ----------

    public void SetPinned(string relative, bool pinned)
    {
        var set = GetPinned();
        var key = Normalize(relative);
        if (pinned ? set.Add(key) : set.Remove(key))
        {
            SavePinned(set);
            OnChanged();
        }
    }

    // ---------- 검색 ----------

    /// <summary>
    /// 제목과 본문에서 찾는다 (대소문자 무시). 폴더를 주면 그 아래만.
    /// 마크다운 메모는 "보기"에 보이는 글자에서 찾고 그 글자로 일부를 보인다 (**, &lt;span …&gt; 같은 표시는 찾지도 보이지도 않음).
    /// </summary>
    public IReadOnlyList<NoteSearchResult> Search(string query, string folder = "", int limit = 200)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var pinned = GetPinned();
        var results = new List<NoteSearchResult>();
        foreach (var path in Directory.EnumerateFiles(FullPath(folder), "*", SearchOption.AllDirectories))
        {
            if (!IsNoteFile(path) || IsInHiddenFolder(path))
            {
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(path);
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }

            if (IsMarkdownFile(path))
            {
                text = MarkdownLines.ToPlainText(text);
            }
            var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0 && !title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            results.Add(new NoteSearchResult(ToInfo(path, pinned), Snippet(text, index, query.Length)));
            if (results.Count >= limit)
            {
                break;
            }
        }
        return [.. results.OrderByDescending(r => r.Note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(r => r.Note.ModifiedAt)];
    }

    // ---------- 내부 ----------

    private NoteFolder BuildFolder(string full, string relative)
    {
        var children = Directory.EnumerateDirectories(full)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
            .Select(d => BuildFolder(d, Combine(relative, Path.GetFileName(d))))
            .ToList();
        var count = Directory.EnumerateFiles(full).Count(IsNoteFile);
        return new NoteFolder(relative.Length == 0 ? "메모" : Path.GetFileName(full), relative, children, count);
    }

    private NoteInfo ToInfo(string fullPath, HashSet<string> pinned)
    {
        var relative = ToRelative(fullPath);
        return new NoteInfo(relative, Path.GetFileNameWithoutExtension(fullPath), Path.GetExtension(fullPath).ToLowerInvariant(),
            ReadPreview(fullPath), File.GetLastWriteTime(fullPath), pinned.Contains(relative));
    }

    private static string ReadPreview(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            var buffer = new char[PreviewLength * 3];
            var read = reader.Read(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);
            // 마크다운 기호(제목, 목록, 굵게, 색 태그 …)를 빼고 한 줄로
            var lines = MarkdownDoc.Parse(text).SelectMany(b => b.PlainText.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0);
            var preview = string.Join(" · ", lines);
            return preview.Length > PreviewLength ? preview[..PreviewLength] + "…" : preview;
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>줄바꿈과 연이은 공백을 공백 하나로.</summary>
    private static string OneLine(string text) => string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string Snippet(string text, int index, int length)
    {
        if (index < 0)
        {
            var head = OneLine(text.TrimStart());
            return head.Length > 80 ? head[..80] + "…" : head;
        }
        var start = Math.Max(0, index - 30);
        var end = Math.Min(text.Length, index + length + 50);
        return (start > 0 ? "…" : "") + OneLine(text[start..end]) + (end < text.Length ? "…" : "");
    }

    private static bool IsMarkdownFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    private static bool IsNoteFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !Path.GetFileName(path).StartsWith('.');

    /// <summary>휴지통, 버전 기록, 이미지처럼 점(.)으로 시작하는 폴더 안 (메모 목록·검색에서 뺀다).</summary>
    private bool IsInHiddenFolder(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1]
            .Any(part => part.StartsWith('.'));

    private string UniquePath(string folder, string title, string extension)
    {
        var candidate = Combine(folder, title + extension);
        for (var i = 2; File.Exists(FullPath(candidate)); i++)
        {
            candidate = Combine(folder, $"{title} ({i}){extension}");
        }
        return candidate;
    }

    /// <summary>휴지통 경로: .trash/yyyyMMdd-HHmmss-fff/원래/경로 (원래 위치를 그대로 보존해서 복원한다).</summary>
    private string TrashDestination(string relative)
    {
        var bucket = _time.GetLocalNow().DateTime.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var destination = Path.Combine(TrashRoot, bucket, Normalize(relative).Replace('/', Path.DirectorySeparatorChar));
        for (var i = 2; File.Exists(destination) || Directory.Exists(destination); i++)
        {
            destination = Path.Combine(TrashRoot, $"{bucket}-{i}", Normalize(relative).Replace('/', Path.DirectorySeparatorChar));
        }
        return destination;
    }

    private string OriginalPathOf(string trashPath)
    {
        var inTrash = Path.GetRelativePath(TrashRoot, trashPath).Replace('\\', '/');
        var slash = inTrash.IndexOf('/');
        return slash < 0 ? inTrash : inTrash[(slash + 1)..];
    }

    /// <summary>휴지통 묶음 폴더 이름(yyyyMMdd-HHmmss-fff)에서 지운 시각을 읽는다.</summary>
    private DateTime BucketTime(string pathInTrash)
    {
        var bucket = Path.GetRelativePath(TrashRoot, pathInTrash).Replace('\\', '/').Split('/')[0];
        return bucket.Length >= 19 && DateTime.TryParseExact(bucket[..19], "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : Directory.GetLastWriteTime(pathInTrash);
    }

    private void DeleteEmptyTrashFolders()
    {
        foreach (var directory in Directory.EnumerateDirectories(TrashRoot, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private HashSet<string> GetPinned()
    {
        var json = _state?.Get(PinnedStateKey);
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize(json, NotesJsonContext.Default.StringArray)?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SavePinned(HashSet<string> pinned) =>
        _state?.Set(PinnedStateKey, JsonSerializer.Serialize(pinned.Order().ToArray(), NotesJsonContext.Default.StringArray));

    private void RewritePins(string from, string? to, bool isFolder)
    {
        var pinned = GetPinned();
        var source = Normalize(from);
        var changed = false;
        foreach (var path in pinned.ToList())
        {
            var matches = isFolder ? path.StartsWith(source + "/", StringComparison.Ordinal) : path == source;
            if (!matches)
            {
                continue;
            }
            pinned.Remove(path);
            if (to is not null)
            {
                pinned.Add(Normalize(to) + path[source.Length..]);
            }
            changed = true;
        }
        if (changed)
        {
            SavePinned(pinned);
        }
    }

    /// <summary>상대 경로 → 실제 경로. Notes 밖을 가리키면 거부한다.</summary>
    public string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, Normalize(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(Root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"메모 폴더 밖의 경로입니다: {relative}", nameof(relative));
        }
        return full;
    }

    public string ToRelative(string fullPath) => Normalize(Path.GetRelativePath(Root, fullPath));

    private static string Normalize(string relative)
    {
        var normalized = relative.Replace('\\', '/').Trim('/');
        return normalized == "." ? "" : normalized;
    }

    private static string Combine(string folder, string name) =>
        Normalize(folder).Length == 0 ? name : Normalize(folder) + "/" + name;

    private static string ParentOf(string relative)
    {
        var normalized = Normalize(relative);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static void EnsureNotRoot(string relative)
    {
        if (Normalize(relative).Length == 0)
        {
            throw new ArgumentException("최상위 메모 폴더는 바꿀 수 없습니다.", nameof(relative));
        }
    }

    /// <summary>파일/폴더 이름으로 쓸 수 없는 문자를 바꾸고 앞뒤 공백·마침표를 지운다.</summary>
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.').Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "제목 없음";
        }
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static bool HasUtf8Bom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        return stream.Read(bom) == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class NotesJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
