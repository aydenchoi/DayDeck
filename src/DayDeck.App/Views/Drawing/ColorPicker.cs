using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DayDeck.App.Views.Drawing;

/// <summary>Excalidraw 0.18의 색 팔레트 (open-color 0·2·4·6·8번 명도, 갈색은 radix bronze).</summary>
internal static class ExcalidrawPalette
{
    public sealed record Family(string Name, string[] Shades);

    public const string Transparent = "transparent";

    public static readonly Family[] Families =
    [
        // 첫 줄: 투명, 흰색, 회색, 검정, 갈색
        new("투명", [Transparent]),
        new("흰색", ["#ffffff"]),
        new("회색", ["#f8f9fa", "#e9ecef", "#ced4da", "#868e96", "#343a40"]),
        new("검정", ["#1e1e1e"]),
        new("갈색", ["#f8f1ee", "#eaddd7", "#d2bab0", "#a18072", "#846358"]),
        // 나머지 (Excalidraw 순서)
        new("청록", ["#e3fafc", "#99e9f2", "#3bc9db", "#15aabf", "#0c8599"]),
        new("파랑", ["#e7f5ff", "#a5d8ff", "#4dabf7", "#228be6", "#1971c2"]),
        new("보라", ["#f3f0ff", "#d0bfff", "#9775fa", "#7950f2", "#6741d9"]),
        new("자주", ["#f8f0fc", "#eebefa", "#da77f2", "#be4bdb", "#9c36b5"]),
        new("분홍", ["#fff0f6", "#fcc2d7", "#f783ac", "#e64980", "#c2255c"]),
        new("초록", ["#ebfbee", "#b2f2bb", "#69db7c", "#40c057", "#2f9e44"]),
        new("옥색", ["#e6fcf5", "#96f2d7", "#38d9a9", "#12b886", "#099268"]),
        new("노랑", ["#fff9db", "#ffec99", "#ffd43b", "#fab005", "#f08c00"]),
        new("주황", ["#fff4e6", "#ffd8a8", "#ffa94d", "#fd7e14", "#e8590c"]),
        new("빨강", ["#fff5f5", "#ffc9c9", "#ff8787", "#fa5252", "#e03131"]),
    ];

    /// <summary>선 색 기본 5개 (검정, 빨강·초록·파랑·노랑의 4번째 명도).</summary>
    public static readonly (string Name, string Hex)[] StrokePicks =
        [("검정", "#1e1e1e"), ("빨강", "#e03131"), ("초록", "#2f9e44"), ("파랑", "#1971c2"), ("주황", "#f08c00")];

    /// <summary>채우기 기본 5개 (투명, 빨강·초록·파랑·노랑의 1번째 명도).</summary>
    public static readonly (string Name, string Hex)[] BackgroundPicks =
        [("없음", Transparent), ("빨강", "#ffc9c9"), ("초록", "#b2f2bb"), ("파랑", "#a5d8ff"), ("노랑", "#ffec99")];

    /// <summary>바탕색 기본 5개.</summary>
    public static readonly (string Name, string Hex)[] CanvasPicks =
        [("흰색", "#ffffff"), ("밝은 회색", "#f8f9fa"), ("밝은 파랑", "#f5faff"), ("밝은 노랑", "#fffce8"), ("밝은 갈색", "#fdf8f6")];

    public static Family? FamilyOf(string color) =>
        Families.FirstOrDefault(f => f.Shades.Any(s => string.Equals(s, color, StringComparison.OrdinalIgnoreCase)));

    public static bool IsPaletteColor(string color) => FamilyOf(color) is not null;

    /// <summary>"#abc", "abc", "#aabbcc", "#aabbccdd"를 "#aabbcc" 꼴(소문자)로. 아니면 null.</summary>
    public static string? NormalizeHex(string input)
    {
        var text = input.Trim().TrimStart('#').ToLowerInvariant();
        if (text == "transparent")
        {
            return Transparent;
        }
        if (!Regex.IsMatch(text, "^([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$"))
        {
            return null;
        }
        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => $"{c}{c}"));
        }
        return "#" + text;
    }
}

/// <summary>
/// Excalidraw처럼 색 고르기: 색 이름(15가지) → 명도(5단계), 그림에서 이미 쓴 색, "#" 직접 입력.
/// 고르면 바로 적용하고 창은 열어 둔다 (다른 곳을 누르면 닫힘).
/// </summary>
internal sealed class ColorPickerPopup : Popup
{
    private readonly Action<string> _apply;
    private readonly int _defaultShade;
    private readonly WrapPanel _familyPanel = new() { Width = 5 * 30 };
    private readonly WrapPanel _shadePanel = new() { Width = 5 * 30 };
    private readonly TextBlock _shadeLabel;
    private readonly TextBox _hexBox;
    private string _current;

    /// <param name="defaultShade">색 이름을 누르면 고를 명도 (선 4, 채우기 1, 바탕 0 — Excalidraw와 같음).</param>
    public ColorPickerPopup(UIElement anchor, string title, string current, int defaultShade, IEnumerable<string> usedColors, Action<string> apply,
        IReadOnlyList<(string Name, string Hex)>? picks = null)
    {
        _apply = apply;
        _defaultShade = defaultShade;
        _current = current;
        PlacementTarget = anchor;
        Placement = PlacementMode.Right;
        HorizontalOffset = 6;
        StaysOpen = false;
        AllowsTransparency = true;

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(Label(title, top: 0));
        if (picks is { Count: > 0 })
        {
            var pickPanel = new WrapPanel { Width = 5 * 30 };
            foreach (var (name, hex) in picks)
            {
                pickPanel.Children.Add(SwatchButton(hex, name, () => Pick(hex)));
            }
            panel.Children.Add(pickPanel);
        }

        var used = usedColors.Where(c => !ExcalidrawPalette.IsPaletteColor(c) && ExcalidrawPalette.NormalizeHex(c) is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if (used.Count > 0)
        {
            panel.Children.Add(Label("이 그림에서 쓴 색", top: 4));
            var usedPanel = new WrapPanel { Width = 5 * 30 };
            foreach (var color in used)
            {
                usedPanel.Children.Add(SwatchButton(color, color, () => Pick(color)));
            }
            panel.Children.Add(usedPanel);
        }

        panel.Children.Add(Label("색", top: 6));
        panel.Children.Add(_familyPanel);
        _shadeLabel = Label("명도", top: 6);
        panel.Children.Add(_shadeLabel);
        panel.Children.Add(_shadePanel);

        panel.Children.Add(Label("직접 입력 (예: #ff8800)", top: 8));
        var hexRow = new DockPanel();
        var hash = new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 13 };
        hash.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        DockPanel.SetDock(hash, Dock.Left);
        hexRow.Children.Add(hash);
        _hexBox = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 2, 3, 2), FontSize = 12.5 };
        System.Windows.Automation.AutomationProperties.SetName(_hexBox, "색 코드");
        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                IsOpen = false;
                e.Handled = true;
            }
        };
        _hexBox.LostKeyboardFocus += (_, _) => ApplyHex(silent: true);
        hexRow.Children.Add(_hexBox);
        panel.Children.Add(hexRow);

        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.18 },
            Margin = new Thickness(0, 0, 8, 8),
        };
        border.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Child = border;
        Refresh();
    }

    private static TextBlock Label(string text, double top)
    {
        var label = new TextBlock { Text = text, FontSize = 11.5, Margin = new Thickness(0, top, 0, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        return label;
    }

    private void Pick(string color)
    {
        _current = color;
        _apply(color);
        Refresh();
    }

    private void ApplyHex(bool silent = false)
    {
        if (ExcalidrawPalette.NormalizeHex(_hexBox.Text) is { } hex)
        {
            if (!string.Equals(hex, _current, StringComparison.OrdinalIgnoreCase))
            {
                Pick(hex);
            }
        }
        else if (!silent)
        {
            _hexBox.Text = Display(_current);
            _hexBox.SelectAll();
        }
    }

    private static string Display(string color) => color == ExcalidrawPalette.Transparent ? "transparent" : color.TrimStart('#');

    private void Refresh()
    {
        var selectedFamily = ExcalidrawPalette.FamilyOf(_current);
        _familyPanel.Children.Clear();
        foreach (var family in ExcalidrawPalette.Families)
        {
            var shade = family.Shades.Length == 1 ? family.Shades[0] : family.Shades[_defaultShade];
            var button = SwatchButton(shade, family.Name, () => Pick(shade), selected: family == selectedFamily);
            _familyPanel.Children.Add(button);
        }
        _shadePanel.Children.Clear();
        var showShades = selectedFamily is { Shades.Length: 5 };
        _shadeLabel.Visibility = _shadePanel.Visibility = showShades ? Visibility.Visible : Visibility.Collapsed;
        if (showShades)
        {
            for (var i = 0; i < 5; i++)
            {
                var shade = selectedFamily!.Shades[i];
                _shadePanel.Children.Add(SwatchButton(shade, $"{selectedFamily.Name} {i + 1}단계", () => Pick(shade),
                    selected: string.Equals(shade, _current, StringComparison.OrdinalIgnoreCase)));
            }
        }
        _hexBox.Text = Display(_current);
    }

    /// <summary>색 칸 단추 (투명은 빗금 칸). 화면 테마와 상관없이 실제 색으로 보인다.</summary>
    public static Button SwatchButton(string color, string name, Action click, bool selected = false, double size = 22)
    {
        var parsed = DrawingRenderer.ParseColor(color);
        UIElement content = parsed is { } c
            ? new Border
            {
                Width = size - 4,
                Height = size - 4,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
            }
            : new Border
            {
                Width = size - 4,
                Height = size - 4,
                CornerRadius = new CornerRadius(4),
                Background = TransparentPattern(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
                BorderThickness = new Thickness(1),
            };
        var button = new Button
        {
            Width = size + 4,
            Height = size + 4,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(0),
            ToolTip = color == ExcalidrawPalette.Transparent ? name : $"{name} ({color})",
            Cursor = Cursors.Hand,
            Content = content,
            BorderThickness = new Thickness(selected ? 2 : 1),
        };
        if (selected)
        {
            button.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        }
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>투명 표시: 회색·흰색 바둑판.</summary>
    public static Brush TransparentPattern()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        var gray = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd0));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(0, 0, 4, 4))));
        group.Children.Add(new GeometryDrawing(gray, null, new RectangleGeometry(new Rect(4, 4, 4, 4))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }
}
