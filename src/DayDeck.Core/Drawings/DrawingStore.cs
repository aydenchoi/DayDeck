using DayDeck.Core.IO;

namespace DayDeck.Core.Drawings;

/// <summary>워크플로우 그림 하나 (목록용).</summary>
public sealed record DrawingInfo(string Path, string Name, DateTime Modified);

/// <summary>
/// 워크플로우 그림 파일 (<c>Data\Drawings\이름.excalidraw</c>). 파일 그대로 excalidraw.com이나 Obsidian Excalidraw에서 열 수 있다.
/// 지우면 <c>.trash</c> 폴더로 옮긴다 (메모와 같은 방식).
/// </summary>
public sealed class DrawingStore
{
    public const string Extension = ".excalidraw";
    public const string TrashFolder = ".trash";

    public DrawingStore(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<DrawingInfo> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }
        return [.. new DirectoryInfo(Root).EnumerateFiles("*" + Extension, SearchOption.TopDirectoryOnly)
            .Select(f => new DrawingInfo(f.FullName, NameOf(f.FullName), f.LastWriteTime))
            .OrderByDescending(d => d.Modified)];
    }

    public static string NameOf(string path) => Path.GetFileName(path)[..^Extension.Length];

    /// <summary>새 그림을 만든다. 같은 이름이 있으면 " (2)"를 붙인다.</summary>
    public string Create(string name)
    {
        Directory.CreateDirectory(Root);
        var path = UniquePath(Sanitize(name));
        AtomicFile.WriteAllText(path, ExcalidrawDocument.CreateEmpty().Serialize());
        Changed?.Invoke(this, EventArgs.Empty);
        return path;
    }

    public ExcalidrawDocument Load(string path) => ExcalidrawDocument.Parse(File.ReadAllText(path));

    public void Save(string path, ExcalidrawDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, document.Serialize());
    }

    /// <summary>이름을 바꾸고 새 경로를 돌려준다.</summary>
    public string Rename(string path, string name)
    {
        var sanitized = Sanitize(name);
        if (sanitized == NameOf(path))
        {
            return path;
        }
        var target = UniquePath(sanitized);
        File.Move(path, target);
        Changed?.Invoke(this, EventArgs.Empty);
        return target;
    }

    /// <summary>휴지통 폴더로 옮긴다.</summary>
    public void Delete(string path)
    {
        var trash = Path.Combine(Root, TrashFolder);
        Directory.CreateDirectory(trash);
        var target = Path.Combine(trash, $"{NameOf(path)} {DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        File.Move(path, target, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string UniquePath(string name)
    {
        var path = Path.Combine(Root, name + Extension);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(Root, $"{name} ({i}){Extension}");
        }
        return path;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string([.. name.Trim().Select(c => invalid.Contains(c) ? '_' : c)]).Trim('.', ' ');
        return cleaned.Length == 0 ? "새 그림" : cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
