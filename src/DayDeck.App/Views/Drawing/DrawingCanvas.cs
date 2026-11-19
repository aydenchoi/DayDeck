using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DayDeck.App.Services;
using DayDeck.Core.Drawings;

namespace DayDeck.App.Views.Drawing;

public enum DrawingTool
{
    Select,
    Hand,
    Rectangle,
    Diamond,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Text,
    Eraser,
}

/// <summary>새로 그리는 요소에 쓰는 모양 (마지막으로 고른 값, Excalidraw처럼).</summary>
public sealed class DrawingStyle
{
    public string StrokeColor { get; set; } = ExcalidrawElement.DefaultStroke;
    public string BackgroundColor { get; set; } = ExcalidrawElement.Transparent;
    public double StrokeWidth { get; set; } = 2;
    public string StrokeStyle { get; set; } = "solid";
    /// <summary>손그림 정도. 기본은 반듯한 선(Excalidraw의 "건축가", 0) — 선이 삐뚤거리지 않는다.</summary>
    public double Roughness { get; set; } = 0;
    public double FontSize { get; set; } = 20;
    public string FillStyle { get; set; } = "solid";
    public double Opacity { get; set; } = 100;

    /// <summary>Excalidraw의 "모서리: 둥글게" (사각형·마름모·선).</summary>
    public bool RoundEdges { get; set; } = true;

    /// <summary>Excalidraw의 "화살표 종류: 곡선" (점을 넣으면 부드럽게 휨).</summary>
    public bool CurvedArrows { get; set; } = true;

    public string? StartArrowhead { get; set; }
    public string? EndArrowhead { get; set; } = "arrow";

    /// <summary>Excalidraw 글꼴 번호 (5 = Excalifont 손글씨, 6 = Nunito, 8 = Comic Shanns, 3 = Cascadia).</summary>
    public int FontFamily { get; set; } = 5;
}

/// <summary>
/// 워크플로우 그림판 (Excalidraw 방식). 요소는 OnRender로 직접 그리고, 글자를 고칠 때만 TextBox를 그 자리에 띄운다.
/// 화면 좌표 = 그림 좌표 × 배율 + 이동.
/// </summary>
public sealed class DrawingCanvas : Canvas
{
    private const double HandleSize = 8;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8;
    private const string ClipboardType = "excalidraw/clipboard";

    private readonly Random _random = new();
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private readonly HashSet<string> _selected = [];
    private readonly TextBox _textEditor;

    private ExcalidrawDocument _doc = ExcalidrawDocument.CreateEmpty();
    private DrawingTool _tool = DrawingTool.Select;
    private double _zoom = 1;
    private Vector _pan = new(40, 40);

    // 끌기 상태
    private enum DragMode { None, Pan, Move, Marquee, Create, Resize, LinePoint, Pen, Erase }
    private DragMode _drag;
    private Point _dragStartScreen;
    private Pt _dragStart;
    private Pt _lastWorld;
    private Vector _panStart;
    private ExcalidrawElement? _creating;
    private string? _resizeHandle;
    private Box _resizeOriginal;
    private int _linePointIndex;

    // 화살표를 그리거나 끝을 끌 때: 마우스 아래 도형의 연결점, 그중 가까워서 붙을 점.
    private ExcalidrawElement? _connectShape;
    private (ExcalidrawElement Shape, Pt Ratio, Pt World)? _snap;

    /// <summary>연결점에 달라붙는 거리 (화면 px).</summary>
    private const double SnapRadius = 14;
    private Dictionary<string, (double X, double Y)> _moveOrigins = [];
    private string? _beforeDrag;
    private bool _dragChanged;
    private bool _spaceDown;
    private ExcalidrawElement? _editingText;
    private ExcalidrawElement? _editingContainer;
    private bool _editingIsNew;

    public DrawingCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "CardBrush");
        _textEditor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            AcceptsReturn = true,
            AcceptsTab = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
        };
        _textEditor.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(_textEditor, "그림 글자");
        _textEditor.PreviewKeyDown += OnTextEditorKeyDown;
        _textEditor.LostKeyboardFocus += (_, _) => CommitText();
        Children.Add(_textEditor);
        // 테마가 바뀌면 다시 그린다 (정적 이벤트라 화면에 있는 동안만 구독).
        Loaded += (_, _) => ThemeService.Changed += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>요소를 바꿨을 때 (자동 저장용).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>선택 또는 도구가 바뀌었을 때 (속성 창·도구 단추 갱신용).</summary>
    public event EventHandler? SelectionChanged;

    public event EventHandler? ToolChanged;

    public DrawingStyle DrawStyle { get; } = new();

    public ExcalidrawDocument Document => _doc;

    public double Zoom => _zoom;

    public DrawingTool Tool
    {
        get => _tool;
        set
        {
            CommitText();
            _tool = value;
            if (value != DrawingTool.Select)
            {
                ClearSelection();
            }
            Cursor = value switch
            {
                DrawingTool.Hand => Cursors.Hand,
                DrawingTool.Select => Cursors.Arrow,
                DrawingTool.Text => Cursors.IBeam,
                _ => Cursors.Cross,
            };
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<ExcalidrawElement> Selection => [.. _doc.Visible.Where(e => _selected.Contains(e.Id))];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Load(ExcalidrawDocument doc)
    {
        CommitText();
        _doc = doc;
        _undo.Clear();
        _redo.Clear();
        _selected.Clear();
        _zoom = 1;
        FitToContent(onlyIfOutside: false);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>그림 전체가 보이게 (그림이 작으면 100%).</summary>
    public void FitToContent(bool onlyIfOutside = false)
    {
        var visible = _doc.Visible.ToList();
        if (visible.Count == 0)
        {
            _pan = new Vector(40, 40);
            InvalidateVisual();
            return;
        }
        var bounds = Box.Union(visible.Select(DrawingGeometry.Bounds)).Inflate(40);
        var width = ActualWidth > 0 ? ActualWidth : 800;
        var height = ActualHeight > 0 ? ActualHeight : 600;
        if (!onlyIfOutside)
        {
            _zoom = Math.Clamp(Math.Min(width / Math.Max(bounds.Width, 1), height / Math.Max(bounds.Height, 1)), MinZoom, 1);
        }
        _pan = new Vector((width - bounds.Width * _zoom) / 2 - bounds.X * _zoom, (height - bounds.Height * _zoom) / 2 - bounds.Y * _zoom);
        InvalidateVisual();
    }

    public void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var screen = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var world = ToWorld(screen);
        _zoom = zoom;
        _pan = new Vector(screen.X - world.X * _zoom, screen.Y - world.Y * _zoom);
        PositionTextEditor();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 좌표 ----------

    private Pt ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    private Point ToScreen(Pt world) => new(world.X * _zoom + _pan.X, world.Y * _zoom + _pan.Y);

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ---------- 그리기 ----------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        // 바탕은 그림의 바탕색 (Excalidraw처럼 어두운 화면에서는 뒤집힌 색). 격자는 그리지 않는다 (Excalidraw 기본).
        var background = DrawingRenderer.Themed(DrawingRenderer.ParseColor(_doc.ViewBackgroundColor) ?? Colors.White, themed: true);
        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, ActualWidth, ActualHeight));
        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _pan.X, _pan.Y));
        foreach (var element in _doc.Visible)
        {
            if (element == _editingText)
            {
                continue; // 고치는 중인 글자는 TextBox가 보여 준다.
            }
            DrawingRenderer.Draw(dc, _doc, element, PixelsPerDip, themed: true);
        }
        dc.Pop();
        DrawSelection(dc);
        DrawConnectionPoints(dc);
        if (_drag == DragMode.Marquee)
        {
            var current = ToScreen(_lastWorld);
            var rect = new Rect(_dragStartScreen, current);
            var accent = ThemeService.Color("AccentBrush");
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 1), rect);
        }
    }

    private void DrawGrid(DrawingContext dc)
    {
        // 옅은 점 격자 (20px). 너무 작게 보이면 생략.
        if (_zoom < 0.4)
        {
            return;
        }
        var brush = new SolidColorBrush(Color.FromArgb(ThemeService.IsDark ? (byte)40 : (byte)50, 128, 128, 128));
        brush.Freeze();
        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(ActualWidth, ActualHeight));
        const double Step = 20;
        var radius = 1 / _zoom;
        for (var x = Math.Floor(topLeft.X / Step) * Step; x <= bottomRight.X; x += Step)
        {
            for (var y = Math.Floor(topLeft.Y / Step) * Step; y <= bottomRight.Y; y += Step)
            {
                dc.DrawRectangle(brush, null, new Rect(x - radius / 2, y - radius / 2, radius, radius));
            }
        }
    }

    private void DrawSelection(DrawingContext dc)
    {
        var selection = Selection;
        if (selection.Count == 0)
        {
            return;
        }
        var accent = new SolidColorBrush(ThemeService.Color("AccentBrush"));
        var pen = new Pen(accent, 1);
        var dashed = new Pen(accent, 1) { DashStyle = new DashStyle([4, 3], 0) };
        foreach (var e in selection)
        {
            dc.DrawRectangle(null, selection.Count > 1 ? dashed : pen, ScreenRect(DrawingGeometry.Bounds(e).Inflate(4 / _zoom)));
        }
        if (selection.Count != 1)
        {
            if (selection.Count > 1)
            {
                dc.DrawRectangle(null, pen, ScreenRect(Box.Union(selection.Select(DrawingGeometry.Bounds)).Inflate(8 / _zoom)));
            }
            return;
        }
        var fill = new SolidColorBrush(ThemeService.Color("CardBrush"));
        foreach (var (name, point) in Handles(selection[0]))
        {
            if (!selection[0].IsLinear)
            {
                dc.DrawRectangle(fill, pen, new Rect(point.X - HandleSize / 2, point.Y - HandleSize / 2, HandleSize, HandleSize));
            }
            else if (name[0] == 'p')
            {
                dc.DrawEllipse(fill, pen, point, HandleSize / 2 + 1, HandleSize / 2 + 1);
            }
            else
            {
                // 굽히기 손잡이: 작고 흐린 원.
                var soft = new SolidColorBrush(Color.FromArgb(90, accent.Color.R, accent.Color.G, accent.Color.B));
                dc.DrawEllipse(soft, null, point, HandleSize / 2 - 1, HandleSize / 2 - 1);
            }
        }
    }

    /// <summary>화살표를 그리는 중이면 도형의 연결점을 보이고, 붙을 점은 크게 칠한다.</summary>
    private void DrawConnectionPoints(DrawingContext dc)
    {
        var connecting = _tool is DrawingTool.Arrow or DrawingTool.Line
                         || (_drag == DragMode.LinePoint && _creating?.Type == ExcalidrawElement.Arrow);
        if (!connecting || _connectShape is null || _connectShape.IsDeleted)
        {
            return;
        }
        var accent = ThemeService.Color("AccentBrush");
        var ring = new Pen(new SolidColorBrush(accent), 1.5);
        var soft = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
        var strong = new SolidColorBrush(accent);
        dc.DrawRectangle(null, new Pen(soft, 6), ScreenRect(DrawingGeometry.Bounds(_connectShape).Inflate(3 / _zoom)));
        foreach (var (ratio, world) in DrawingGeometry.ConnectionPoints(_connectShape))
        {
            var p = ToScreen(world);
            var active = _snap is { } snap && snap.Shape == _connectShape && snap.Ratio == ratio;
            dc.DrawEllipse(active ? strong : Brushes.White, ring, p, active ? 6 : 4, active ? 6 : 4);
        }
    }

    private Rect ScreenRect(Box b) => new(ToScreen(new Pt(b.X, b.Y)), ToScreen(new Pt(b.Right, b.Bottom)));

    /// <summary>크기 손잡이 (화면 좌표). 선·화살표는 끝점마다.</summary>
    private List<(string Name, Point Point)> Handles(ExcalidrawElement e)
    {
        if (e.IsLinear)
        {
            var points = DrawingGeometry.AbsolutePoints(e);
            var handles = points.Select((p, i) => ("p" + i, ToScreen(p))).ToList();
            // 구간 가운데 손잡이: 끌면 점이 생겨 선이 굽는다 (곡선이면 부드럽게).
            for (var i = 0; i + 1 < points.Count; i++)
            {
                if ((points[i + 1] - points[i]).Length * _zoom >= HandleSize * 4)
                {
                    handles.Add(("m" + i, ToScreen(DrawingGeometry.SegmentMidpoint(e, i))));
                }
            }
            return handles;
        }
        if (e.Type == ExcalidrawElement.FreeDraw)
        {
            return [];
        }
        var r = ScreenRect(DrawingGeometry.Bounds(e).Inflate(4 / _zoom));
        return
        [
            ("nw", r.TopLeft), ("n", new Point(r.Left + r.Width / 2, r.Top)), ("ne", r.TopRight),
            ("e", new Point(r.Right, r.Top + r.Height / 2)), ("se", r.BottomRight),
            ("s", new Point(r.Left + r.Width / 2, r.Bottom)), ("sw", r.BottomLeft), ("w", new Point(r.Left, r.Top + r.Height / 2)),
        ];
    }

    // ---------- 마우스 ----------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_editingText is not null)
        {
            if (e.OriginalSource is DependencyObject source && (source == _textEditor || _textEditor.IsAncestorOf(source)))
            {
                return;
            }
            CommitText();
        }
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _dragStartScreen = screen;
        _dragStart = world;
        _lastWorld = world;
        _dragChanged = false;

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && (_tool == DrawingTool.Hand || _spaceDown)))
        {
            BeginDrag(DragMode.Pan);
            _panStart = _pan;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        if (e.ClickCount == 2 && _tool == DrawingTool.Select && Selection is [{ IsLinear: true } line] && RemovePointAt(line, screen))
        {
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && _tool is DrawingTool.Select or DrawingTool.Text)
        {
            StartTextAt(world);
            e.Handled = true;
            return;
        }

        switch (_tool)
        {
            case DrawingTool.Select:
                MouseDownSelect(world, screen);
                break;
            case DrawingTool.Eraser:
                _beforeDrag = _doc.SnapshotElements();
                BeginDrag(DragMode.Erase);
                EraseAt(world);
                break;
            case DrawingTool.Text:
                StartTextAt(world);
                break;
            case DrawingTool.Pen:
                _beforeDrag = _doc.SnapshotElements();
                _creating = NewElement(ExcalidrawElement.FreeDraw, world.X, world.Y, 0, 0);
                BeginDrag(DragMode.Pen);
                break;
            case DrawingTool.Arrow:
            case DrawingTool.Line:
                _beforeDrag = _doc.SnapshotElements();
                if (_tool == DrawingTool.Arrow && DrawingGeometry.FindConnectionPoint(_doc, world, SnapRadius / _zoom) is { } startSnap)
                {
                    // 연결점 가까이에서 시작하면 그 점에서 시작한다.
                    world = startSnap.World;
                    _dragStart = world;
                }
                _creating = NewElement(_tool == DrawingTool.Arrow ? ExcalidrawElement.Arrow : ExcalidrawElement.Line, world.X, world.Y, 0, 0);
                _creating.Points = [new Pt(0, 0), new Pt(0, 0)];
                BeginDrag(DragMode.Create);
                break;
            default:
                _beforeDrag = _doc.SnapshotElements();
                _creating = NewElement(_tool switch
                {
                    DrawingTool.Diamond => ExcalidrawElement.Diamond,
                    DrawingTool.Ellipse => ExcalidrawElement.Ellipse,
                    _ => ExcalidrawElement.Rectangle,
                }, world.X, world.Y, 0, 0);
                BeginDrag(DragMode.Create);
                break;
        }
        InvalidateVisual();
    }

    private void MouseDownSelect(Pt world, Point screen)
    {
        // 손잡이 먼저.
        if (Selection is [var only])
        {
            foreach (var (name, point) in Handles(only))
            {
                if (Math.Abs(point.X - screen.X) <= HandleSize && Math.Abs(point.Y - screen.Y) <= HandleSize)
                {
                    _beforeDrag = _doc.SnapshotElements();
                    _creating = only;
                    if (only.IsLinear)
                    {
                        var index = int.Parse(name[1..], System.Globalization.CultureInfo.InvariantCulture);
                        if (name[0] == 'm')
                        {
                            // 가운데 손잡이를 끌면 그 자리에 새 점을 넣고 그 점을 끈다.
                            var points = DrawingGeometry.AbsolutePoints(only);
                            points.Insert(index + 1, DrawingGeometry.SegmentMidpoint(only, index));
                            DrawingGeometry.SetAbsolutePoints(only, points);
                            index++;
                        }
                        _linePointIndex = index;
                        BeginDrag(DragMode.LinePoint);
                    }
                    else
                    {
                        _resizeHandle = name;
                        _resizeOriginal = DrawingGeometry.Bounds(only);
                        _resizeFontBase = only.FontSize;
                        BeginDrag(DragMode.Resize);
                    }
                    return;
                }
            }
        }

        var hit = DrawingGeometry.HitTopmost(_doc, world, 6 / _zoom);
        if (hit is null)
        {
            if (Keyboard.Modifiers != ModifierKeys.Shift)
            {
                ClearSelection();
            }
            BeginDrag(DragMode.Marquee);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (!_selected.Remove(hit.Id))
            {
                _selected.Add(hit.Id);
            }
        }
        else if (!_selected.Contains(hit.Id))
        {
            _selected.Clear();
            _selected.Add(hit.Id);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        _beforeDrag = _doc.SnapshotElements();
        _moveOrigins = MovingSet().ToDictionary(m => m.Id, m => (m.X, m.Y));
        BeginDrag(DragMode.Move);
    }

    /// <summary>선택 + 상자 안 글자 (같이 움직인다).</summary>
    private List<ExcalidrawElement> MovingSet()
    {
        var set = Selection.ToList();
        foreach (var container in set.ToList())
        {
            if (DrawingGeometry.BoundText(_doc, container) is { } text && !set.Contains(text))
            {
                set.Add(text);
            }
        }
        return set;
    }

    private void BeginDrag(DragMode mode)
    {
        _drag = mode;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == DragMode.None)
        {
            UpdateHoverCursor(e.GetPosition(this));
            if (_tool is DrawingTool.Arrow or DrawingTool.Line)
            {
                UpdateConnectHover(ToWorld(e.GetPosition(this)), null);
                InvalidateVisual();
            }
            return;
        }
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _lastWorld = world;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_drag)
        {
            case DragMode.Pan:
                _pan = _panStart + (screen - _dragStartScreen);
                PositionTextEditor();
                break;
            case DragMode.Move:
            {
                var delta = world - _dragStart;
                if (!_dragChanged && delta.Length * _zoom < 2)
                {
                    return;
                }
                _dragChanged = true;
                var moving = MovingSet();
                foreach (var m in moving)
                {
                    if (_moveOrigins.TryGetValue(m.Id, out var origin))
                    {
                        m.X = origin.X + delta.X;
                        m.Y = origin.Y + delta.Y;
                    }
                }
                // 화살표만 옮기면 붙어 있던 도형에서 뗀다.
                foreach (var arrow in moving.Where(m => m.IsLinear))
                {
                    if (arrow.StartBinding is { } s && !moving.Any(m => m.Id == s.ElementId))
                    {
                        DrawingGeometry.Bind(_doc, arrow, start: true, null);
                    }
                    if (arrow.EndBinding is { } en && !moving.Any(m => m.Id == en.ElementId))
                    {
                        DrawingGeometry.Bind(_doc, arrow, start: false, null);
                    }
                }
                FollowArrows(moving);
                break;
            }
            case DragMode.Marquee:
                break;
            case DragMode.Create when _creating is not null:
                _dragChanged = true;
                if (_creating.IsLinear)
                {
                    var end = world - _dragStart;
                    if (shift)
                    {
                        end = SnapAngle(end);
                    }
                    else if (UpdateConnectHover(world, _creating.Id) is { } snapEnd)
                    {
                        end = snapEnd - _dragStart;
                    }
                    _creating.Points = [new Pt(0, 0), end];
                    _creating.Width = Math.Abs(end.X);
                    _creating.Height = Math.Abs(end.Y);
                }
                else
                {
                    var box = Box.FromPoints(_dragStart, world);
                    if (shift)
                    {
                        var size = Math.Max(box.Width, box.Height);
                        box = Box.FromPoints(_dragStart, new Pt(_dragStart.X + Math.Sign(world.X - _dragStart.X) * size, _dragStart.Y + Math.Sign(world.Y - _dragStart.Y) * size));
                    }
                    SetBox(_creating, box);
                }
                break;
            case DragMode.Pen when _creating is not null:
            {
                var points = _creating.Points.ToList();
                var relative = world - new Pt(_creating.X, _creating.Y);
                if ((relative - points[^1]).Length * _zoom >= 1.5)
                {
                    points.Add(relative);
                    _creating.Points = points;
                    _creating.Width = points.Max(p => p.X) - points.Min(p => p.X);
                    _creating.Height = points.Max(p => p.Y) - points.Min(p => p.Y);
                }
                _dragChanged = true;
                break;
            }
            case DragMode.Resize when _creating is not null:
                _dragChanged = true;
                Resize(_creating, world, shift);
                break;
            case DragMode.LinePoint when _creating is not null:
            {
                _dragChanged = true;
                var points = DrawingGeometry.AbsolutePoints(_creating);
                var isEnd = _linePointIndex == 0 || _linePointIndex == points.Count - 1;
                points[_linePointIndex] = isEnd && _creating.Type == ExcalidrawElement.Arrow && UpdateConnectHover(world, _creating.Id) is { } snapped
                    ? snapped
                    : world;
                DrawingGeometry.SetAbsolutePoints(_creating, points);
                LayoutBoundText(_creating);
                break;
            }
            case DragMode.Erase:
                EraseAt(world);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.None)
        {
            return;
        }
        var mode = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        Cursor = _tool == DrawingTool.Hand ? Cursors.Hand : _tool == DrawingTool.Select ? Cursors.Arrow : _tool == DrawingTool.Text ? Cursors.IBeam : Cursors.Cross;

        switch (mode)
        {
            case DragMode.Marquee:
            {
                var box = Box.FromPoints(_dragStart, _lastWorld);
                if (box.Width * _zoom > 3 || box.Height * _zoom > 3)
                {
                    foreach (var el in _doc.Visible.Where(el => el.ContainerId is null && box.Contains(DrawingGeometry.Bounds(el))))
                    {
                        _selected.Add(el.Id);
                    }
                }
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            }
            case DragMode.Move when _dragChanged:
            case DragMode.Resize when _dragChanged:
            case DragMode.Erase when _dragChanged:
                if (mode != DragMode.Erase)
                {
                    foreach (var m in MovingSet())
                    {
                        m.Touch(_random);
                    }
                }
                Commit(_beforeDrag);
                break;
            case DragMode.LinePoint when _dragChanged && _creating is not null:
                BindLineEnd(_creating, _linePointIndex);
                _creating.Touch(_random);
                Commit(_beforeDrag);
                break;
            case DragMode.Create when _creating is not null:
                FinishCreate(_creating);
                break;
            case DragMode.Pen when _creating is not null:
                Commit(_beforeDrag);
                break;
        }
        _creating = null;
        _beforeDrag = null;
        _snap = null;
        _connectShape = null;
        InvalidateVisual();
    }

    private void FinishCreate(ExcalidrawElement created)
    {
        if (created.IsLinear)
        {
            if (DrawingGeometry.AbsolutePoints(created) is var points && (points[^1] - points[0]).Length * _zoom < 4)
            {
                // 그냥 누르기만 하면 짧은 기본 화살표.
                created.Points = [new Pt(0, 0), new Pt(120, 0)];
                created.Width = 120;
            }
            if (created.Type == ExcalidrawElement.Arrow)
            {
                BindLineEnd(created, 0);
                BindLineEnd(created, created.Points.Count - 1);
            }
        }
        else if (created.Width * _zoom < 4 && created.Height * _zoom < 4)
        {
            // 누르기만 하면 기본 크기로.
            SetBox(created, new Box(created.X - 60, created.Y - 35, 120, 70));
        }
        _selected.Clear();
        _selected.Add(created.Id);
        Commit(_beforeDrag);
        Tool = DrawingTool.Select;
        _selected.Add(created.Id);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>화살표 끝이 도형 위면 붙이고, 아니면 뗀다.</summary>
    private void BindLineEnd(ExcalidrawElement line, int index)
    {
        if (line.Type != ExcalidrawElement.Arrow)
        {
            return;
        }
        var points = DrawingGeometry.AbsolutePoints(line);
        if (index != 0 && index != points.Count - 1)
        {
            return;
        }
        var start = index == 0;
        if (DrawingGeometry.FindConnectionPoint(_doc, points[index], SnapRadius / _zoom, line.Id) is { } snap)
        {
            // 연결점에 놓으면 그 점에 붙는다 (도형을 옮겨도 그 점을 따라감).
            DrawingGeometry.Bind(_doc, line, start, snap.Shape, snap.Ratio);
        }
        else
        {
            DrawingGeometry.Bind(_doc, line, start, DrawingGeometry.FindBindTarget(_doc, points[index], line.Id));
        }
        DrawingGeometry.UpdateArrowEnds(_doc, line);
        _connectShape = null;
        _snap = null;
    }

    /// <summary>
    /// 화살표 끝 근처의 도형과 연결점을 찾아 둔다 (그리기에 씀). 연결점에 충분히 가까우면 그 점을 돌려준다.
    /// </summary>
    private Pt? UpdateConnectHover(Pt world, string? exceptId)
    {
        _snap = DrawingGeometry.FindConnectionPoint(_doc, world, SnapRadius / _zoom, exceptId);
        _connectShape = _snap?.Shape ?? _doc.Visible.LastOrDefault(e => e.IsShape && e.Id != exceptId
            && DrawingGeometry.Bounds(e).Inflate(SnapRadius * 2 / _zoom).Contains(world));
        return _snap?.World;
    }

    /// <summary>선택한 선의 가운데 점을 두 번 누르면 그 점을 지운다 (굽힌 것을 펴기).</summary>
    private bool RemovePointAt(ExcalidrawElement line, Point screen)
    {
        var points = DrawingGeometry.AbsolutePoints(line);
        for (var i = 1; i < points.Count - 1; i++)
        {
            var p = ToScreen(points[i]);
            if (Math.Abs(p.X - screen.X) <= HandleSize && Math.Abs(p.Y - screen.Y) <= HandleSize)
            {
                var before = _doc.SnapshotElements();
                points.RemoveAt(i);
                DrawingGeometry.SetAbsolutePoints(line, points);
                DrawingGeometry.UpdateArrowEnds(_doc, line);
                LayoutBoundText(line);
                line.Touch(_random);
                Commit(before);
                InvalidateVisual();
                return true;
            }
        }
        return false;
    }

    private static Pt SnapAngle(Pt v)
    {
        var angle = Math.Atan2(v.Y, v.X);
        var snapped = Math.Round(angle / (Math.PI / 12)) * (Math.PI / 12);
        var length = v.Length;
        return new Pt(Math.Cos(snapped) * length, Math.Sin(snapped) * length);
    }

    private static void SetBox(ExcalidrawElement e, Box box)
    {
        e.X = box.X;
        e.Y = box.Y;
        e.Width = box.Width;
        e.Height = box.Height;
    }

    private void Resize(ExcalidrawElement e, Pt world, bool keepRatio)
    {
        var o = _resizeOriginal;
        double left = o.X, top = o.Y, right = o.Right, bottom = o.Bottom;
        var h = _resizeHandle ?? "";
        if (h.Contains('w'))
        {
            left = world.X;
        }
        if (h.Contains('e'))
        {
            right = world.X;
        }
        if (h.Contains('n'))
        {
            top = world.Y;
        }
        if (h.Contains('s'))
        {
            bottom = world.Y;
        }
        var box = Box.FromPoints(new Pt(left, top), new Pt(right, bottom));
        if (keepRatio && o.Width > 0 && o.Height > 0 && h.Length == 2)
        {
            var scale = Math.Max(box.Width / o.Width, box.Height / o.Height);
            var w = o.Width * scale;
            var hh = o.Height * scale;
            box = new Box(h.Contains('w') ? o.Right - w : o.X, h.Contains('n') ? o.Bottom - hh : o.Y, w, hh);
        }
        if (e.Type == ExcalidrawElement.Text)
        {
            // 글자는 크기에 맞춰 글자 크기를 바꾼다.
            if (o.Height > 0)
            {
                var scale = Math.Max(box.Height / o.Height, 0.1);
                e.FontSize = Math.Clamp(Math.Round(_resizeFontBase * scale, 1), 4, 400);
                MeasureTextElement(e);
                e.X = box.X;
                e.Y = box.Y;
            }
            return;
        }
        SetBox(e, box);
        LayoutBoundText(e);
        FollowArrows([e]);
    }

    private double _resizeFontBase;

    /// <summary>움직인 도형에 붙은 화살표(와 화살표 글자)를 따라 옮긴다.</summary>
    private void FollowArrows(IEnumerable<ExcalidrawElement> moved)
    {
        foreach (var arrow in DrawingGeometry.UpdateBoundArrows(_doc, moved))
        {
            LayoutBoundText(arrow);
        }
    }

    private void UpdateHoverCursor(Point screen)
    {
        if (_tool != DrawingTool.Select || _spaceDown)
        {
            return;
        }
        if (Selection is [var only])
        {
            foreach (var (name, point) in Handles(only))
            {
                if (Math.Abs(point.X - screen.X) <= HandleSize && Math.Abs(point.Y - screen.Y) <= HandleSize)
                {
                    Cursor = name switch
                    {
                        "nw" or "se" => Cursors.SizeNWSE,
                        "ne" or "sw" => Cursors.SizeNESW,
                        "n" or "s" => Cursors.SizeNS,
                        "e" or "w" => Cursors.SizeWE,
                        _ => Cursors.Cross,
                    };
                    _resizeFontBase = only.FontSize;
                    return;
                }
            }
        }
        Cursor = DrawingGeometry.HitTopmost(_doc, ToWorld(screen), 6 / _zoom) is not null ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(this));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _pan += new Vector(e.Delta / 2.0, 0);
        }
        else
        {
            _pan += new Vector(0, e.Delta / 2.0);
        }
        PositionTextEditor();
        InvalidateVisual();
        e.Handled = true;
    }

    // ---------- 글자 ----------

    private void StartTextAt(Pt world)
    {
        var hit = DrawingGeometry.HitTopmost(_doc, world, 6 / _zoom);
        if (hit?.Type == ExcalidrawElement.Text)
        {
            BeginEditText(hit, null, isNew: false);
            return;
        }
        if (hit is { IsShape: true } || hit?.Type == ExcalidrawElement.Arrow)
        {
            if (DrawingGeometry.BoundText(_doc, hit) is { } existing)
            {
                BeginEditText(existing, hit, isNew: false);
                return;
            }
            _beforeDrag = _doc.SnapshotElements();
            var label = NewElement(ExcalidrawElement.Text, hit.X, hit.Y, 10, 10);
            label.ContainerId = hit.Id;
            label.TextAlign = "center";
            label.VerticalAlign = "middle";
            hit.AddBound(label.Id, ExcalidrawElement.Text);
            LayoutBoundText(hit);
            BeginEditText(label, hit, isNew: true);
            return;
        }
        _beforeDrag = _doc.SnapshotElements();
        var text = NewElement(ExcalidrawElement.Text, world.X, world.Y - DrawStyle.FontSize * 0.6, 10, DrawStyle.FontSize * 1.25);
        BeginEditText(text, null, isNew: true);
    }

    private void BeginEditText(ExcalidrawElement text, ExcalidrawElement? container, bool isNew)
    {
        _editingText = text;
        _editingContainer = container ?? _doc.Find(text.ContainerId);
        _editingIsNew = isNew;
        if (!isNew)
        {
            _beforeDrag = _doc.SnapshotElements();
        }
        _selected.Clear();
        _textEditor.Text = text.OriginalText;
        _textEditor.FontFamily = DrawingRenderer.FontFor(text.FontFamily);
        _textEditor.TextAlignment = _editingContainer is not null ? TextAlignment.Center : TextAlignment.Left;
        _textEditor.TextWrapping = _editingContainer is not null ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _textEditor.Foreground = new SolidColorBrush(DrawingRenderer.StrokeColor(text.StrokeColor, themed: true));
        _textEditor.Visibility = Visibility.Visible;
        PositionTextEditor();
        _textEditor.Focus();
        _textEditor.CaretIndex = _textEditor.Text.Length;
        if (!isNew)
        {
            _textEditor.SelectAll();
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PositionTextEditor()
    {
        if (_editingText is not { } text)
        {
            return;
        }
        _textEditor.FontSize = text.FontSize * _zoom;
        if (_editingContainer is { } container)
        {
            var b = DrawingGeometry.Bounds(container);
            var r = ScreenRect(b.Inflate(-8));
            SetLeft(_textEditor, r.Left);
            _textEditor.Width = Math.Max(40, r.Width);
            _textEditor.Height = double.NaN;
            _textEditor.Measure(new Size(_textEditor.Width, double.PositiveInfinity));
            SetTop(_textEditor, r.Top + Math.Max(0, (r.Height - _textEditor.DesiredSize.Height) / 2));
        }
        else
        {
            var p = ToScreen(new Pt(text.X, text.Y));
            SetLeft(_textEditor, p.X);
            SetTop(_textEditor, p.Y);
            _textEditor.Width = double.NaN; // 치는 만큼 넓어진다.
            _textEditor.MinWidth = 40;
            _textEditor.Height = double.NaN;
        }
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            CommitText();
            Focus();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_editingText is not { } text)
        {
            return;
        }
        var container = _editingContainer;
        _editingText = null;
        _editingContainer = null;
        _textEditor.Visibility = Visibility.Collapsed;
        var value = _textEditor.Text.Replace("\r\n", "\n");
        if (value.Trim().Length == 0)
        {
            text.IsDeleted = true;
            container?.RemoveBound(text.Id);
            if (_editingIsNew)
            {
                // 아무것도 안 쓴 새 글자는 기록하지 않는다.
                if (_beforeDrag is not null)
                {
                    _doc.RestoreElements(_beforeDrag);
                }
                _beforeDrag = null;
                InvalidateVisual();
                return;
            }
        }
        else
        {
            text.TextContent = value;
            text.OriginalText = value;
            if (container is null)
            {
                MeasureTextElement(text);
            }
            else
            {
                LayoutBoundText(container);
            }
        }
        text.Touch(_random);
        Commit(_beforeDrag);
        _beforeDrag = null;
        if (_tool == DrawingTool.Text)
        {
            Tool = DrawingTool.Select;
        }
        if (!text.IsDeleted)
        {
            _selected.Clear();
            _selected.Add(container?.Id ?? text.Id);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>상자 밖 글자의 크기 (Excalidraw처럼 가장 긴 줄 × 줄 수 × 줄 높이).</summary>
    private void MeasureTextElement(ExcalidrawElement text) => DrawingText.MeasureFree(text, PixelsPerDip);

    /// <summary>상자·화살표 안 글자를 Excalidraw 규칙으로 줄바꿈하고 자리를 잡는다 (글자가 넘치면 상자를 키운다).</summary>
    private void LayoutBoundText(ExcalidrawElement container)
    {
        if (DrawingGeometry.BoundText(_doc, container) is { } text)
        {
            DrawingText.LayoutBound(_doc, container, text, PixelsPerDip);
        }
    }

    // ---------- 키보드 ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_editingText is not null || e.Handled)
        {
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift:
                case Key.Y:
                    Redo();
                    break;
                case Key.Z:
                    Undo();
                    break;
                case Key.A:
                    SelectAll();
                    break;
                case Key.D:
                    Duplicate();
                    break;
                case Key.C:
                    Copy(cut: false);
                    break;
                case Key.X:
                    Copy(cut: true);
                    break;
                case Key.V:
                    Paste();
                    break;
                case Key.Right:
                    NextStep(StepDirection.Right);
                    break;
                case Key.Left:
                    NextStep(StepDirection.Left);
                    break;
                case Key.Down:
                    NextStep(StepDirection.Down);
                    break;
                case Key.Up:
                    NextStep(StepDirection.Up);
                    break;
                case Key.OemPlus:
                case Key.Add:
                    SetZoom(_zoom * 1.2);
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    SetZoom(_zoom / 1.2);
                    break;
                case Key.D0:
                case Key.NumPad0:
                    SetZoom(1);
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        else
        {
            switch (key)
            {
                case Key.Space:
                    _spaceDown = true;
                    Cursor = Cursors.Hand;
                    break;
                case Key.Delete:
                case Key.Back:
                    DeleteSelection();
                    break;
                case Key.Escape:
                    ClearSelection();
                    Tool = DrawingTool.Select;
                    break;
                case Key.Enter when Selection is [var only]:
                    StartTextAt(DrawingGeometry.Bounds(only).Center);
                    break;
                case Key.Left:
                    Nudge(shift ? -10 : -1, 0);
                    break;
                case Key.Right:
                    Nudge(shift ? 10 : 1, 0);
                    break;
                case Key.Up:
                    Nudge(0, shift ? -10 : -1);
                    break;
                case Key.Down:
                    Nudge(0, shift ? 10 : 1);
                    break;
                case Key.V or Key.D1 when !shift:
                    Tool = DrawingTool.Select;
                    break;
                case Key.H when !shift:
                    Tool = DrawingTool.Hand;
                    break;
                case Key.R or Key.D2 when !shift:
                    Tool = DrawingTool.Rectangle;
                    break;
                case Key.D or Key.D3 when !shift:
                    Tool = DrawingTool.Diamond;
                    break;
                case Key.O or Key.D4 when !shift:
                    Tool = DrawingTool.Ellipse;
                    break;
                case Key.A or Key.D5 when !shift:
                    Tool = DrawingTool.Arrow;
                    break;
                case Key.L or Key.D6 when !shift:
                    Tool = DrawingTool.Line;
                    break;
                case Key.P or Key.D7 when !shift:
                    Tool = DrawingTool.Pen;
                    break;
                case Key.T or Key.D8 when !shift:
                    Tool = DrawingTool.Text;
                    break;
                case Key.E or Key.D0 when !shift:
                    Tool = DrawingTool.Eraser;
                    break;
                case Key.D1 when shift:
                    FitToContent();
                    break;
                default:
                    handled = false;
                    break;
            }
        }
        e.Handled = handled;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space && _spaceDown)
        {
            _spaceDown = false;
            Tool = _tool;
            e.Handled = _editingText is null;
        }
    }

    // ---------- 편집 동작 ----------

    private ExcalidrawElement NewElement(string type, double x, double y, double width, double height)
    {
        var e = ExcalidrawElement.Create(type, x, y, width, height, _random);
        e.StrokeColor = DrawStyle.StrokeColor;
        e.StrokeWidth = DrawStyle.StrokeWidth;
        e.StrokeStyle = DrawStyle.StrokeStyle;
        e.Roughness = DrawStyle.Roughness;
        e.Opacity = DrawStyle.Opacity;
        if (type is not (ExcalidrawElement.Arrow or ExcalidrawElement.Line or ExcalidrawElement.FreeDraw or ExcalidrawElement.Text))
        {
            e.BackgroundColor = DrawStyle.BackgroundColor;
            e.FillStyle = DrawStyle.FillStyle;
        }
        // Excalidraw 기본값: 사각형은 고정 반지름(3), 마름모·선·화살표는 비율(2).
        e.Roundness = type switch
        {
            ExcalidrawElement.Rectangle => DrawStyle.RoundEdges ? new Roundness(3) : null,
            ExcalidrawElement.Diamond or ExcalidrawElement.Line => DrawStyle.RoundEdges ? new Roundness(2) : null,
            ExcalidrawElement.Arrow => DrawStyle.CurvedArrows ? new Roundness(2) : null,
            _ => null,
        };
        if (type == ExcalidrawElement.Arrow)
        {
            e.StartArrowhead = DrawStyle.StartArrowhead;
            e.EndArrowhead = DrawStyle.EndArrowhead;
        }
        if (type == ExcalidrawElement.Text)
        {
            e.FontSize = DrawStyle.FontSize;
            e.FontFamily = DrawStyle.FontFamily;
            e.LineHeight = DrawingRenderer.DefaultLineHeight(DrawStyle.FontFamily);
        }
        _doc.Elements.Add(e);
        return e;
    }

    /// <summary>되돌리기 기록에 남기고 저장을 알린다.</summary>
    private void Commit(string? before)
    {
        if (before is null)
        {
            return;
        }
        _undo.Add(before);
        if (_undo.Count > 200)
        {
            _undo.RemoveAt(0);
        }
        _redo.Clear();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>그림 바탕색 (appState.viewBackgroundColor).</summary>
    public void SetViewBackground(string color)
    {
        if (_doc.ViewBackgroundColor == color)
        {
            return;
        }
        _doc.ViewBackgroundColor = color;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>선택한 요소의 모양을 바꾼다 (속성 창). 고를 게 없으면 다음에 그릴 모양만 바꾼다.</summary>
    public void ApplyToSelection(Action<ExcalidrawElement> change, bool includeBoundText = false)
    {
        var targets = includeBoundText ? MovingSet() : Selection.ToList();
        if (targets.Count == 0)
        {
            return;
        }
        var before = _doc.SnapshotElements();
        foreach (var e in targets)
        {
            change(e);
            e.Touch(_random);
            if (e.Type == ExcalidrawElement.Text)
            {
                if (_doc.Find(e.ContainerId) is { } container)
                {
                    LayoutBoundText(container);
                }
                else
                {
                    MeasureTextElement(e);
                }
            }
        }
        Commit(before);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CommitText();
        if (_undo.Count == 0)
        {
            return;
        }
        _redo.Add(_doc.SnapshotElements());
        _doc.RestoreElements(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        AfterHistory();
    }

    public void Redo()
    {
        CommitText();
        if (_redo.Count == 0)
        {
            return;
        }
        _undo.Add(_doc.SnapshotElements());
        _doc.RestoreElements(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        AfterHistory();
    }

    private void AfterHistory()
    {
        _selected.RemoveWhere(id => _doc.Find(id) is null);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        _selected.Clear();
        foreach (var e in _doc.Visible.Where(e => e.ContainerId is null))
        {
            _selected.Add(e.Id);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }
        _selected.Clear();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelection()
    {
        var targets = MovingSet();
        if (targets.Count == 0)
        {
            return;
        }
        var before = _doc.SnapshotElements();
        foreach (var e in targets)
        {
            Remove(e);
        }
        _selected.Clear();
        Commit(before);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>지운 표시를 하고 연결을 정리한다 (Excalidraw처럼 isDeleted).</summary>
    private void Remove(ExcalidrawElement e)
    {
        e.IsDeleted = true;
        e.Touch(_random);
        foreach (var other in _doc.Visible)
        {
            if (other.StartBinding?.ElementId == e.Id)
            {
                other.StartBinding = null;
            }
            if (other.EndBinding?.ElementId == e.Id)
            {
                other.EndBinding = null;
            }
            other.RemoveBound(e.Id);
            if (other.ContainerId == e.Id)
            {
                other.IsDeleted = true;
            }
        }
    }

    private void EraseAt(Pt world)
    {
        if (DrawingGeometry.HitTopmost(_doc, world, 4 / _zoom) is { } hit)
        {
            Remove(hit);
            _dragChanged = true;
            InvalidateVisual();
        }
    }

    private void Nudge(double dx, double dy)
    {
        var moving = MovingSet();
        if (moving.Count == 0)
        {
            return;
        }
        var before = _doc.SnapshotElements();
        foreach (var m in moving)
        {
            m.X += dx;
            m.Y += dy;
            m.Touch(_random);
        }
        FollowArrows(moving);
        Commit(before);
        InvalidateVisual();
    }

    private void NextStep(StepDirection direction)
    {
        if (Selection is not [{ IsShape: true } from])
        {
            return;
        }
        var before = _doc.SnapshotElements();
        var (shape, _) = DrawingGeometry.AddNextStep(_doc, from, direction, _random);
        _selected.Clear();
        _selected.Add(shape.Id);
        Commit(before);
        // 새 단계가 화면 밖이면 보이게 옮긴다.
        var r = ScreenRect(DrawingGeometry.Bounds(shape));
        if (r.Left < 0 || r.Top < 0 || r.Right > ActualWidth || r.Bottom > ActualHeight)
        {
            _pan += new Vector(ActualWidth / 2 - (r.Left + r.Width / 2), ActualHeight / 2 - (r.Top + r.Height / 2));
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Duplicate()
    {
        var source = MovingSet();
        if (source.Count == 0)
        {
            return;
        }
        var before = _doc.SnapshotElements();
        var copies = CloneSet(source, new Pt(10, 10));
        _doc.Elements.AddRange(copies);
        SelectCopies(copies);
        Commit(before);
    }

    /// <summary>요소들을 새 id로 복사한다. 안에서 서로 가리키던 연결은 새 id로, 밖을 가리키던 화살표 연결은 끊는다.</summary>
    private List<ExcalidrawElement> CloneSet(IReadOnlyList<ExcalidrawElement> source, Pt offset)
    {
        var map = source.ToDictionary(e => e.Id, _ => ExcalidrawElement.NewId(_random));
        var copies = new List<ExcalidrawElement>();
        foreach (var original in source)
        {
            var copy = original.Clone();
            copy.Id = map[original.Id];
            copy.X += offset.X;
            copy.Y += offset.Y;
            copy.Seed = _random.Next(1, int.MaxValue);
            copy.Version = 1;
            copy.Touch(_random);
            copy.ContainerId = copy.ContainerId is { } c && map.TryGetValue(c, out var nc) ? nc : null;
            copy.BoundElements = [.. copy.BoundElements.Where(b => map.ContainsKey(b.Id)).Select(b => b with { Id = map[b.Id] })];
            copy.StartBinding = copy.StartBinding is { } s && map.TryGetValue(s.ElementId, out var ns) ? s with { ElementId = ns } : null;
            copy.EndBinding = copy.EndBinding is { } en && map.TryGetValue(en.ElementId, out var ne) ? en with { ElementId = ne } : null;
            copy.GroupIds = [];
            copies.Add(copy);
        }
        return copies;
    }

    private void SelectCopies(List<ExcalidrawElement> copies)
    {
        _selected.Clear();
        foreach (var c in copies.Where(c => c.ContainerId is null))
        {
            _selected.Add(c.Id);
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Excalidraw 클립보드 형식(JSON 글자)으로 복사한다. excalidraw.com에 그대로 붙여 넣을 수 있다.</summary>
    public void Copy(bool cut)
    {
        var source = MovingSet();
        if (source.Count == 0)
        {
            return;
        }
        var json = new JsonObject
        {
            ["type"] = ClipboardType,
            ["elements"] = new JsonArray([.. source.Select(e => (JsonNode?)e.Node.DeepClone())]),
            ["files"] = new JsonObject(),
        };
        try
        {
            Clipboard.SetText(json.ToJsonString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }
        if (cut)
        {
            DeleteSelection();
        }
    }

    public void Paste()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                return; // 그림 붙여 넣기는 아직 안 함.
            }
            text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        var before = _doc.SnapshotElements();
        List<ExcalidrawElement> source = [];
        try
        {
            if (JsonNode.Parse(text) is JsonObject root && (string?)root["type"] is ClipboardType or "excalidraw" && root["elements"] is JsonArray array)
            {
                source = [.. array.OfType<JsonObject>().Select(o => new ExcalidrawElement((JsonObject)o.DeepClone())).Where(e => !e.IsDeleted)];
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var mouse = Mouse.GetPosition(this);
        var at = mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= ActualWidth && mouse.Y <= ActualHeight ? ToWorld(mouse) : ToWorld(new Point(ActualWidth / 2, ActualHeight / 2));
        if (source.Count == 0)
        {
            // 일반 글자는 글자 요소로.
            if (text.Trim().Length == 0)
            {
                return;
            }
            var t = NewElement(ExcalidrawElement.Text, at.X, at.Y, 10, 10);
            t.TextContent = text.Replace("\r\n", "\n");
            t.OriginalText = t.TextContent;
            MeasureTextElement(t);
            SelectCopies([t]);
            Commit(before);
            return;
        }
        var bounds = Box.Union(source.Select(DrawingGeometry.Bounds));
        var copies = CloneSet(source, new Pt(at.X - bounds.Center.X, at.Y - bounds.Center.Y));
        _doc.Elements.AddRange(copies);
        SelectCopies(copies);
        Commit(before);
    }

    // ---------- 내보내기 ----------

    /// <summary>그림 전체(또는 선택한 것)를 PNG로. 화면 테마와 상관없이 파일의 색 그대로, 2배 크기.</summary>
    public BitmapSource? RenderPng(bool selectionOnly)
    {
        CommitText();
        var elements = (selectionOnly && _selected.Count > 0 ? MovingSet() : _doc.Visible.ToList())
            .OrderBy(e => _doc.Elements.IndexOf(e)).ToList();
        if (elements.Count == 0)
        {
            return null;
        }
        const double Padding = 20;
        var bounds = Box.Union(elements.Select(DrawingGeometry.Bounds)).Inflate(Padding);
        // 보통 2배, 아주 큰 그림은 약 4천만 화소 안으로.
        var scale = Math.Min(2, Math.Sqrt(40_000_000 / Math.Max(1, bounds.Width * bounds.Height)));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = DrawingRenderer.ParseColor(_doc.ViewBackgroundColor) ?? Colors.White;
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, bounds.Width * scale, bounds.Height * scale));
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -bounds.X * scale, -bounds.Y * scale));
            foreach (var e in elements)
            {
                DrawingRenderer.Draw(dc, _doc, e, 1, themed: false);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
