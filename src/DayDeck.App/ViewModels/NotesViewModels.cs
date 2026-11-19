using System.Collections.ObjectModel;
using System.IO;
using DayDeck.Core.AutoSave;
using DayDeck.Core.Notes;

namespace DayDeck.App.ViewModels;

/// <summary>폴더 트리의 한 노드.</summary>
public sealed class NoteFolderViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public NoteFolderViewModel(NoteFolder folder, ISet<string> expanded)
    {
        Name = folder.Name;
        RelativePath = folder.RelativePath;
        Count = folder.NoteCount;
        _isExpanded = RelativePath.Length == 0 || expanded.Contains(RelativePath);
        Children = [.. folder.Children.Select(c => new NoteFolderViewModel(c, expanded))];
    }

    public string Name { get; }
    public string RelativePath { get; }
    public int Count { get; }
    public string CountText => Count > 0 ? Count.ToString() : "";
    public bool IsRoot => RelativePath.Length == 0;
    public string Icon => IsRoot ? "🗂" : "📁";
    public ObservableCollection<NoteFolderViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public IEnumerable<NoteFolderViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    public override string ToString() => Name;
}

/// <summary>메모 목록의 한 줄 (일반, 검색 결과, 휴지통).</summary>
public sealed class NoteListItemViewModel
{
    public NoteListItemViewModel(NoteInfo note, string? snippet = null)
    {
        RelativePath = note.RelativePath;
        Title = note.Title;
        IsPinned = note.IsPinned;
        Preview = snippet ?? note.Preview;
        Detail = FormatTime(note.ModifiedAt);
        Extension = note.Extension;
        FolderPath = FolderPathOf(note.RelativePath);
    }

    public NoteListItemViewModel(TrashedNote trashed)
    {
        TrashPath = trashed.TrashPath;
        RelativePath = trashed.OriginalPath;
        Title = Path.GetFileNameWithoutExtension(trashed.OriginalPath);
        Preview = "원래 위치: " + trashed.OriginalPath;
        Detail = $"{FormatTime(trashed.DeletedAt)} 삭제 · {(trashed.DeletedAt.AddDays(30) - DateTime.Now).Days}일 후 영구 삭제";
        Extension = Path.GetExtension(trashed.OriginalPath);
        FolderPath = FolderPathOf(trashed.OriginalPath);
    }

    /// <summary>제목 위에 작게 보이는 폴더 위치: "메모 › 업무 › 회의록" (탭의 위치 표시와 같은 모양).</summary>
    public string FolderPath { get; }

    private static string FolderPathOf(string relativePath)
    {
        var folder = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";
        return folder.Length == 0 ? "메모" : "메모 › " + folder.Replace("/", " › ");
    }

    public string RelativePath { get; }
    public string? TrashPath { get; }
    public bool IsTrashed => TrashPath is not null;
    public string Title { get; }
    public string DisplayTitle => (IsPinned ? "📌 " : "") + Title + (Extension == ".txt" ? " (.txt)" : "");
    public bool IsPinned { get; }
    public string Preview { get; }
    public string Detail { get; }
    public string Extension { get; }

    private static string FormatTime(DateTime time) =>
        time.Date == DateTime.Today ? $"오늘 {time:HH:mm}" : time.Year == DateTime.Today.Year ? $"{time:M월 d일}" : $"{time:yyyy-MM-dd}";

    public override string ToString() => Title;
}

/// <summary>
/// 열린 메모 탭. 자동 저장 대상이다. 새로 만든 "새 메모"는 첫 저장 때 첫 줄로 파일 이름을 정한다.
/// </summary>
public sealed class NoteTabViewModel : ObservableObject, IAutoSaveTarget
{
    public const string NewNoteTitle = "새 메모";

    private readonly NoteStore _store;
    private readonly AutoSaveService _autoSave;
    private string _path;
    private string _content;
    private bool _isDirty;
    private bool _hasConflict;
    private string _saveStatus = "";

    public NoteTabViewModel(NoteStore store, AutoSaveService autoSave, string path, bool isNew = false)
    {
        _store = store;
        _autoSave = autoSave;
        _path = path;
        _content = store.Read(path);
        KnownModified = store.GetModifiedTime(path);
        IsAutoTitle = isNew;
    }

    /// <summary>파일 이름이 바뀌었다 (자동 제목, 이름 바꾸기). 세션 저장용.</summary>
    public event EventHandler? PathChanged;

    public string Path
    {
        get => _path;
        private set
        {
            if (SetProperty(ref _path, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Breadcrumb));
                PathChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Title => System.IO.Path.GetFileName(_path);

    public string Breadcrumb
    {
        get
        {
            var folder = System.IO.Path.GetDirectoryName(_path)?.Replace('\\', '/') ?? "";
            return folder.Length == 0 ? "메모" : "메모 › " + folder.Replace("/", " › ");
        }
    }

    public bool IsAutoTitle { get; private set; }

    /// <summary>마지막으로 읽거나 저장한 파일 수정 시각 (외부 변경 감지용).</summary>
    public DateTime KnownModified { get; private set; }

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                IsDirty = true;
                _autoSave.NotifyEdited(this);
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DirtyMark));
                SaveStatus = value ? "● 수정됨" : SaveStatus;
            }
        }
    }

    public string DirtyMark => _isDirty ? "●" : "";

    /// <summary>다른 프로그램이 파일을 바꿨는데 이 탭에도 저장 안 한 내용이 있다.</summary>
    public bool HasConflict
    {
        get => _hasConflict;
        set => SetProperty(ref _hasConflict, value);
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set => SetProperty(ref _saveStatus, value);
    }

    public int CaretIndex { get; set; }

    public void Save()
    {
        if (!_isDirty)
        {
            return;
        }

        try
        {
            _store.Save(_path, _content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다른 프로그램이 잠깐 잡고 있는 등: 수정됨 상태로 두고 다음 저장 때 다시 한다.
            DayDeck.Core.Diagnostics.Log.Warn($"메모 저장 실패: {_path}", ex);
            SaveStatus = "⚠ 저장 실패 — 잠시 후 다시 저장합니다";
            return;
        }
        KnownModified = _store.GetModifiedTime(_path);
        HasConflict = false;
        IsDirty = false;
        SaveStatus = $"✓ 저장됨 {DateTime.Now:HH:mm:ss}";

        if (IsAutoTitle && NoteText.SuggestTitle(_content) is { Length: > 0 } title)
        {
            IsAutoTitle = false;
            Path = _store.Rename(_path, title);
            KnownModified = _store.GetModifiedTime(_path);
        }
    }

    /// <summary>파일에서 다시 읽는다 (외부 변경 반영).</summary>
    public void Reload()
    {
        _content = _store.Read(_path);
        KnownModified = _store.GetModifiedTime(_path);
        HasConflict = false;
        IsDirty = false;
        SaveStatus = "↻ 다시 읽음";
        OnPropertyChanged(nameof(Content));
    }

    /// <summary>파일 이름이 바뀌었을 때 (목록에서 이름 바꾸기/이동).</summary>
    public void Retarget(string newPath)
    {
        IsAutoTitle = false;
        Path = newPath;
    }

    /// <summary>외부 변경을 확인한다. 저장 안 한 내용이 없으면 조용히 다시 읽는다.</summary>
    public void CheckExternalChange()
    {
        if (!_store.Exists(_path))
        {
            return;
        }
        var modified = _store.GetModifiedTime(_path);
        if (modified <= KnownModified)
        {
            return;
        }
        if (_isDirty)
        {
            HasConflict = true;
        }
        else
        {
            Reload();
        }
    }

    /// <summary>충돌 시 "내 내용 유지": 다음 저장에서 덮어쓴다.</summary>
    public void KeepMine()
    {
        KnownModified = _store.GetModifiedTime(_path);
        HasConflict = false;
    }

    public override string ToString() => $"메모 {_path}";
}
