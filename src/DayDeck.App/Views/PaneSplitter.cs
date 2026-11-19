using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DayDeck.App.Views;

/// <summary>
/// 끌어서 구역 너비를 바꾸는 세로 경계선. 그리드의 열(<see cref="TargetColumn"/>) 또는 요소(<see cref="TargetElement"/>)의
/// 너비를 바꾸고, 놓으면 <see cref="Key"/>로 저장한다. 다음에 화면을 열면 저장한 너비로 시작한다. 두 번 누르면 처음 너비로.
/// <see cref="Edge"/>는 경계선이 대상의 어느 쪽에 있는지 (Right: 오른쪽으로 끌면 넓어짐, Left: 왼쪽으로 끌면 넓어짐).
/// </summary>
public sealed class PaneSplitter : Thumb
{
    public static readonly DependencyProperty TargetColumnProperty =
        DependencyProperty.Register(nameof(TargetColumn), typeof(ColumnDefinition), typeof(PaneSplitter));

    public static readonly DependencyProperty TargetElementProperty =
        DependencyProperty.Register(nameof(TargetElement), typeof(FrameworkElement), typeof(PaneSplitter));

    private double? _defaultWidth;

    public PaneSplitter()
    {
        Width = 6;
        Cursor = Cursors.SizeWE;
        Focusable = false;
        Background = Brushes.Transparent;
        ToolTip = "끌어서 너비 조절 · 두 번 누르면 처음 너비로";
        Template = CreateTemplate();
        DragDelta += OnDragDelta;
        DragCompleted += (_, _) => SaveWidth();
        MouseDoubleClick += (_, e) =>
        {
            ResetWidth();
            e.Handled = true;
        };
        Loaded += (_, _) => RestoreWidth();
    }

    public ColumnDefinition? TargetColumn
    {
        get => (ColumnDefinition?)GetValue(TargetColumnProperty);
        set => SetValue(TargetColumnProperty, value);
    }

    public FrameworkElement? TargetElement
    {
        get => (FrameworkElement?)GetValue(TargetElementProperty);
        set => SetValue(TargetElementProperty, value);
    }

    /// <summary>저장 이름 (예: "main.nav").</summary>
    public string Key { get; set; } = "";

    public HorizontalAlignment Edge { get; set; } = HorizontalAlignment.Right;

    public double MinSize { get; set; } = 120;

    public double MaxSize { get; set; } = 900;

    /// <summary>나머지(남는 공간을 차지하는) 구역이 최소 이만큼은 남게 한다.</summary>
    public double KeepRest { get; set; } = 260;

    /// <summary>화면이 열릴 때 저장한 너비를 적용할지 (보기 방식에 따라 화면이 직접 적용하는 경우 false).</summary>
    public bool RestoreOnLoad { get; set; } = true;

    /// <summary>대상 너비가 사용자에 의해 바뀌었다 (저장한 너비를 다시 적용할 때도).</summary>
    public event EventHandler? Resized;

    private double CurrentWidth => TargetColumn?.ActualWidth ?? TargetElement?.ActualWidth ?? 0;

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var change = Edge == HorizontalAlignment.Right ? e.HorizontalChange : -e.HorizontalChange;
        SetWidth(CurrentWidth + change);
    }

    private void SetWidth(double width)
    {
        _defaultWidth ??= DeclaredWidth();
        width = Math.Clamp(width, MinSize, Math.Max(MinSize, Math.Min(MaxSize, CurrentWidth + RestSlack())));
        if (TargetColumn is { } column)
        {
            column.Width = new GridLength(width);
        }
        else if (TargetElement is { } element)
        {
            element.Width = width;
        }
        Resized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>같은 그리드에서 남는 공간(별표 열)이 <see cref="KeepRest"/>보다 넓은 만큼만 더 넓힐 수 있다.</summary>
    private double RestSlack()
    {
        if (TargetColumn is { } column && column.Parent is Grid grid)
        {
            var star = grid.ColumnDefinitions.Where(c => c != column && c.Width.IsStar).Sum(c => c.ActualWidth);
            return Math.Max(0, star - KeepRest);
        }
        if (TargetElement?.Parent is FrameworkElement parent)
        {
            return Math.Max(0, parent.ActualWidth - CurrentWidth - KeepRest);
        }
        return double.MaxValue;
    }

    private double? DeclaredWidth() =>
        TargetColumn is { } column ? (column.Width.IsAbsolute ? column.Width.Value : null)
        : TargetElement is { } element && !double.IsNaN(element.Width) ? element.Width : null;

    private void SaveWidth()
    {
        if (Key.Length > 0)
        {
            App.Current.Layout.SetWidth(Key, CurrentWidth);
        }
    }

    private void RestoreWidth()
    {
        _defaultWidth ??= DeclaredWidth();
        if (RestoreOnLoad && Key.Length > 0 && App.Current.Layout.GetWidth(Key) is { } saved)
        {
            var width = Math.Clamp(saved, MinSize, MaxSize);
            if (TargetColumn is { } column)
            {
                column.Width = new GridLength(width);
            }
            else if (TargetElement is { } element)
            {
                element.Width = width;
            }
            Resized?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetWidth()
    {
        if (_defaultWidth is { } width)
        {
            if (TargetColumn is { } column)
            {
                column.Width = new GridLength(width);
            }
            else if (TargetElement is { } element)
            {
                element.Width = width;
            }
        }
        else if (TargetColumn is { } column)
        {
            column.Width = new GridLength(1, GridUnitType.Star);
        }
        if (Key.Length > 0)
        {
            App.Current.Layout.SetWidth(Key, _defaultWidth ?? CurrentWidth);
        }
        Resized?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>평소에는 보이지 않고, 마우스를 올리거나 끄는 동안 가운데에 얇은 선이 보인다.</summary>
    private static ControlTemplate CreateTemplate()
    {
        var template = new ControlTemplate(typeof(PaneSplitter));
        var root = new FrameworkElementFactory(typeof(Grid));
        root.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
        var line = new FrameworkElementFactory(typeof(Border), "Line");
        line.SetValue(FrameworkElement.WidthProperty, 2.0);
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        line.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        line.SetValue(UIElement.OpacityProperty, 0.0);
        root.AppendChild(line);
        template.VisualTree = root;

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6, "Line"));
        var dragging = new Trigger { Property = IsDraggingProperty, Value = true };
        dragging.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0, "Line"));
        template.Triggers.Add(hover);
        template.Triggers.Add(dragging);
        return template;
    }
}
