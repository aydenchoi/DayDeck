using System.Windows.Media;

namespace DayDeck.App.ViewModels;

public enum NavItemKind
{
    Page,
    List,
    Tag,
    Header,
    Separator,
    Action,
}

/// <summary>왼쪽 탐색 메뉴 한 줄.</summary>
public sealed class NavItem : ObservableObject
{
    private string _title;
    private int _count;
    private bool _isAlert;

    public NavItem(string key, NavItemKind kind, string icon = "", string title = "", Brush? dot = null)
    {
        Key = key;
        Kind = kind;
        Icon = icon;
        _title = title;
        Dot = dot;
    }

    public string Key { get; }
    public NavItemKind Kind { get; }
    public string Icon { get; }
    public Brush? Dot { get; }
    public long? ListId { get; init; }

    /// <summary>목록 색 (#RRGGBB). 바뀌면 탐색 메뉴를 다시 만든다.</summary>
    public string? ListColor { get; init; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(ShowAlertBadge));
            }
        }
    }

    /// <summary>개수를 빨간 배지로 표시한다 (기한 지남).</summary>
    public bool IsAlert
    {
        get => _isAlert;
        set
        {
            if (SetProperty(ref _isAlert, value))
            {
                OnPropertyChanged(nameof(ShowAlertBadge));
            }
        }
    }

    public bool ShowAlertBadge => _isAlert && _count > 0;

    public string CountText => _count > 0 ? _count.ToString() : "";
    public bool HasDot => Dot is not null;
    public bool IsSelectable => Kind is NavItemKind.Page or NavItemKind.List or NavItemKind.Action;
    public bool IsHeader => Kind == NavItemKind.Header;
    public bool IsSeparator => Kind == NavItemKind.Separator;
}
