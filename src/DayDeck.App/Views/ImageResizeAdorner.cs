using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DayDeck.App.Services;

namespace DayDeck.App.Views;

/// <summary>
/// 편집기에서 누른 그림의 테두리와 네 모서리 손잡이. 손잡이를 끌면 그림 너비만 바꾸고 높이는 비율대로 따라간다
/// (Image.Stretch = Uniform). 끌기를 마치면 <see cref="Resized"/>로 새 너비를 알린다.
/// </summary>
internal sealed class ImageResizeAdorner : Adorner
{
    private const double HandleSize = 9;
    private const double SmallestWidth = 24;

    private readonly Image _image;
    private readonly VisualCollection _visuals;
    private readonly Thumb[] _handles;
    private double _startWidth;
    private Point _startPoint;
    private bool _dragging;

    public ImageResizeAdorner(Image image) : base(image)
    {
        _image = image;
        _visuals = new VisualCollection(this);
        // 왼쪽 위, 오른쪽 위, 왼쪽 아래, 오른쪽 아래: (가로 방향, 세로 방향)
        _handles =
        [
            CreateHandle(-1, -1, Cursors.SizeNWSE),
            CreateHandle(1, -1, Cursors.SizeNESW),
            CreateHandle(-1, 1, Cursors.SizeNESW),
            CreateHandle(1, 1, Cursors.SizeNWSE),
        ];
    }

    /// <summary>끌기를 마쳤다. 새 너비(px).</summary>
    public event Action<int>? Resized;

    public Image Image => _image;

    private IInputElement DragReference => VisualParent as IInputElement ?? this;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    private Thumb CreateHandle(int horizontal, int vertical, Cursor cursor)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        var thumb = new Thumb
        {
            Width = HandleSize,
            Height = HandleSize,
            Cursor = cursor,
            Focusable = false, // 편집기의 키보드 포커스를 뺏지 않도록
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border },
            ToolTip = "끌어서 크기 조절 (비율 유지)",
        };
        thumb.SetResourceReference(Control.BackgroundProperty, "CardBrush");
        thumb.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        thumb.DragStarted += (_, _) =>
        {
            _startWidth = _image.ActualWidth;
            _startPoint = Mouse.GetPosition(DragReference);
            _dragging = true;
        };
        thumb.DragDelta += (_, _) =>
        {
            // 손잡이도 그림을 따라 움직이므로 DragDelta 값 대신 처음 누른 곳에서 지금까지 움직인 거리를 쓴다
            // (그림이 커져 다음 줄로 넘어가도 흔들리지 않게 그림이 아닌 adorner 층 기준 좌표).
            var now = Mouse.GetPosition(DragReference);
            var ratio = _image.ActualHeight > 0 ? _image.ActualWidth / _image.ActualHeight : 1;
            var byX = _startWidth + horizontal * (now.X - _startPoint.X);
            var byY = _startWidth + vertical * (now.Y - _startPoint.Y) * ratio;
            var width = Math.Abs(byX - _startWidth) >= Math.Abs(byY - _startWidth) ? byX : byY;
            _image.StretchDirection = StretchDirection.Both;
            _image.Width = Math.Round(Math.Clamp(width, SmallestWidth, _image.MaxWidth));
            InvalidateVisual();
        };
        thumb.DragCompleted += (_, _) =>
        {
            _dragging = false;
            InvalidateVisual();
            if (Math.Abs(_image.Width - _startWidth) >= 1)
            {
                Resized?.Invoke((int)_image.Width);
            }
        };
        _visuals.Add(thumb);
        return thumb;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = AdornedElement.RenderSize;
        var half = HandleSize / 2;
        Point[] corners = [new(0, 0), new(size.Width, 0), new(0, size.Height), new(size.Width, size.Height)];
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i].Arrange(new Rect(corners[i].X - half, corners[i].Y - half, HandleSize, HandleSize));
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = AdornedElement.RenderSize;
        var accent = ThemeService.Brush("AccentBrush");
        drawingContext.DrawRectangle(null, new Pen(accent, 1.5), new Rect(size));
        if (_dragging)
        {
            // 끄는 동안 지금 크기를 보여 준다.
            var label = new FormattedText($"{size.Width:0} × {size.Height:0}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var box = new Rect(4, 4, label.Width + 8, label.Height + 4);
            drawingContext.DrawRoundedRectangle(accent, null, box, 3, 3);
            drawingContext.DrawText(label, new Point(box.X + 4, box.Y + 2));
        }
    }
}
