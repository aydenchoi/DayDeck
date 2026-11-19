using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DayDeck.Core.Calendar;

namespace DayDeck.App.Views.Calendar;

/// <summary>
/// 주/일 보기: 위에 종일 줄(여러 날 일정은 이어진 막대), 아래에 시간 격자.
/// 빈 곳을 끌면 새 일정, 블록을 끌면 이동(다른 날로도), 블록 아래 끝을 끌면 길이 조절. 슬롯 단위로 맞춘다.
/// </summary>
internal sealed class TimeGridView : Grid
{
    public const double HourHeight = 48;
    private const double GutterWidth = 56;
    private const double ResizeGrip = 6;
    private const double DragThreshold = 4;

    private readonly ICalendarHost _host;
    private readonly CalendarData _data;
    private readonly int _days;
    private readonly Canvas _canvas = new() { Background = CalendarStyle.Card, ClipToBounds = true };
    private readonly ScrollViewer _scroller;
    private readonly List<(EventOccurrence Occurrence, Border Block)> _blocks = [];

    private DragMode _drag;
    private Point _dragOrigin;
    private EventOccurrence? _dragOccurrence;
    private Border? _dragBlock;
    private Border? _ghost;
    private bool _dragStarted;

    private enum DragMode
    {
        None,
        Create,
        Move,
        Resize,
    }

    public TimeGridView(ICalendarHost host, CalendarData data, int days, double? initialOffset = null)
    {
        _host = host;
        _data = data;
        _days = days;
        Background = CalendarStyle.Card;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader();
        Children.Add(header);

        var allDay = BuildAllDayRow();
        SetRow(allDay, 1);
        Children.Add(allDay);

        _canvas.Height = HourHeight * 24;
        _canvas.SizeChanged += (_, _) => Layout();
        _canvas.MouseLeftButtonDown += OnCanvasMouseDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp += OnCanvasMouseUp;
        _canvas.LostMouseCapture += (_, _) => CancelDrag();

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gutter = new Canvas { Height = HourHeight * 24 };
        for (var hour = 1; hour < 24; hour++)
        {
            var label = CalendarStyle.Label($"{hour:00}:00", 10.5, CalendarStyle.Muted);
            Canvas.SetTop(label, hour * HourHeight - 7);
            Canvas.SetRight(label, 6);
            gutter.Children.Add(label);
        }
        body.Children.Add(gutter);
        SetColumn(_canvas, 1);
        body.Children.Add(_canvas);

        _scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(_scroller, 2);
        Children.Add(_scroller);

        Loaded += (_, _) =>
        {
            // 처음에는 현재 시각(오늘이 보일 때) 또는 오전 8시 근처를 보여 준다. 다시 그릴 때는 위치를 유지한다.
            var showsToday = _data.Today >= _data.From && _data.Today <= _data.To;
            var hour = showsToday ? Math.Max(0, _data.Now.Hour - 2) : Math.Max(0, WorkHours.Start.Hour - 1);
            _scroller.ScrollToVerticalOffset(initialOffset ?? hour * HourHeight);
        };
    }

    public double VerticalOffset => _scroller.VerticalOffset;

    private double ColumnWidth => Math.Max(1, _canvas.ActualWidth / _days);

    private Grid BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(GutterWidth, 0, SystemParameters.VerticalScrollBarWidth, 0) };
        for (var i = 0; i < _days; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var day = _data.From.AddDays(i);
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            var name = CalendarStyle.Label(CalendarStyle.DayName(day.DayOfWeek), 11, CalendarStyle.DayBrush(_data, day));
            name.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(name);

            var number = CalendarStyle.Label(day.Day.ToString(), 17, CalendarStyle.DayBrush(_data, day), FontWeights.SemiBold);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            var numberHost = new Border { Child = number, Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Cursor = Cursors.Hand };
            number.VerticalAlignment = VerticalAlignment.Center;
            if (day == _data.Today)
            {
                numberHost.Background = CalendarStyle.Accent;
                number.Foreground = Brushes.White;
            }
            if (_days > 1)
            {
                numberHost.MouseLeftButtonUp += (_, _) => _host.ShowDay(day);
            }
            panel.Children.Add(numberHost);

            if (_data.HolidayName(day) is { } holiday)
            {
                var holidayText = CalendarStyle.Label(holiday, 10, CalendarStyle.Holiday);
                holidayText.HorizontalAlignment = HorizontalAlignment.Center;
                panel.Children.Add(holidayText);
            }
            SetColumn(panel, i);
            grid.Children.Add(panel);
        }
        return grid;
    }

    /// <summary>종일 줄: 여러 날 일정은 열을 걸쳐 그리고, 겹치면 아래 줄로 내린다. 마감 있는 할 일도 여기에 둔다.</summary>
    private Border BuildAllDayRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4), MinHeight = 24 };
        for (var i = 0; i < _days; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var lanes = new List<DateOnly>(); // 각 줄에서 마지막으로 차지한 날
        void Place(FrameworkElement element, DateOnly first, DateOnly last)
        {
            var from = first < _data.From ? _data.From : first;
            var to = last > _data.To ? _data.To : last;
            var lane = lanes.FindIndex(end => end < from);
            if (lane < 0)
            {
                lanes.Add(to);
                lane = lanes.Count - 1;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            else
            {
                lanes[lane] = to;
            }
            element.Margin = new Thickness(2, 1, 2, 0);
            SetRow(element, lane);
            SetColumn(element, from.DayNumber - _data.From.DayNumber);
            SetColumnSpan(element, to.DayNumber - from.DayNumber + 1);
            grid.Children.Add(element);
        }

        foreach (var occurrence in _data.Events.Where(e => e.IsAllDay).OrderBy(e => e.FirstDay).ThenByDescending(e => e.LastDay))
        {
            var chip = CalendarStyle.EventChip(occurrence, _data.Now, showTime: false);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _host.OpenOccurrence(occurrence);
                e.Handled = true;
            };
            chip.MouseRightButtonUp += (_, e) =>
            {
                _host.ShowOccurrenceMenu(occurrence, chip);
                e.Handled = true;
            };
            Place(chip, occurrence.FirstDay, occurrence.LastDay);
        }
        foreach (var todo in _data.Todos)
        {
            var chip = CalendarStyle.TodoChip(todo, _data.Now);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                _host.OpenTodo(todo);
                e.Handled = true;
            };
            Place(chip, todo.DueDate!.Value, todo.DueDate!.Value);
        }

        var label = CalendarStyle.Label("종일", 10.5, CalendarStyle.Muted);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.Margin = new Thickness(0, 0, 8, 0);

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SystemParameters.VerticalScrollBarWidth) });
        outer.Children.Add(label);
        SetColumn(grid, 1);
        outer.Children.Add(grid);

        // 종일 줄을 두 번 누르면 종일 일정 만들기
        grid.Background = Brushes.Transparent;
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && e.OriginalSource == grid)
            {
                var index = (int)(e.GetPosition(grid).X / Math.Max(1, grid.ActualWidth / _days));
                var day = _data.From.AddDays(Math.Clamp(index, 0, _days - 1)).ToDateTime(TimeOnly.MinValue);
                _host.CreateEvent(day, day, allDay: true);
            }
        };

        return new Border
        {
            Child = outer,
            BorderBrush = CalendarStyle.Line,
            BorderThickness = new Thickness(0, 1, 0, 1),
        };
    }

    /// <summary>설정의 근무 시간 (예: 09:00–18:00). 잘못된 값이면 9–18시.</summary>
    private static (TimeOnly Start, TimeOnly End) WorkHours
    {
        get
        {
            var hours = App.Current.Settings.Current.Calendar.WorkHours;
            static TimeOnly? Parse(string? s) => TimeOnly.TryParseExact(s, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t) ? t : null;
            return hours is [var a, var b] && Parse(a) is { } start && Parse(b) is { } end && end > start
                ? (start, end)
                : (new TimeOnly(9, 0), new TimeOnly(18, 0));
        }
    }

    /// <summary>격자선, 일정 블록, 현재 시각 선을 다시 배치한다 (크기가 바뀔 때마다).</summary>
    private void Layout()
    {
        _canvas.Children.Clear();
        _blocks.Clear();
        var width = ColumnWidth;

        // 근무 시간 밖은 옅게 칠한다 (주말 제외 없이 모든 날).
        var (workStart, workEnd) = WorkHours;
        foreach (var (top, bottom) in new[] { (0.0, workStart.ToTimeSpan().TotalHours), (workEnd.ToTimeSpan().TotalHours, 24.0) })
        {
            if (bottom <= top)
            {
                continue;
            }
            var shade = new Rectangle
            {
                Width = _canvas.ActualWidth,
                Height = (bottom - top) * HourHeight,
                Fill = Services.ThemeService.Brush("SubtleBrush"),
                IsHitTestVisible = false,
            };
            Canvas.SetTop(shade, top * HourHeight);
            _canvas.Children.Add(shade);
        }

        for (var hour = 0; hour < 24; hour++)
        {
            _canvas.Children.Add(new Line
            {
                X1 = 0, X2 = _canvas.ActualWidth, Y1 = hour * HourHeight, Y2 = hour * HourHeight,
                Stroke = CalendarStyle.Line, StrokeThickness = 1, IsHitTestVisible = false,
            });
        }
        for (var i = 0; i < _days; i++)
        {
            var day = _data.From.AddDays(i);
            if (day == _data.Today && _days > 1)
            {
                var todayBackground = new Rectangle { Width = width, Height = _canvas.Height, Fill = CalendarStyle.TodayBackground, Opacity = 0.35, IsHitTestVisible = false };
                Canvas.SetLeft(todayBackground, i * width);
                _canvas.Children.Add(todayBackground);
            }
            _canvas.Children.Add(new Line
            {
                X1 = i * width, X2 = i * width, Y1 = 0, Y2 = _canvas.Height,
                Stroke = CalendarStyle.Line, StrokeThickness = 1, IsHitTestVisible = false,
            });
        }

        for (var i = 0; i < _days; i++)
        {
            var day = _data.From.AddDays(i);
            var dayStart = day.ToDateTime(TimeOnly.MinValue);
            var dayEnd = dayStart.AddDays(1);
            var segments = _data.Events
                .Where(e => !e.IsAllDay && e.Start < dayEnd && (e.End > dayStart || (e.End == e.Start && e.Start >= dayStart)))
                .Select(e => (Occurrence: e, Start: e.Start < dayStart ? dayStart : e.Start, End: e.End > dayEnd ? dayEnd : e.End))
                .OrderBy(s => s.Start).ThenByDescending(s => s.End)
                .ToList();

            foreach (var (segment, column, columns) in AssignColumns(segments))
            {
                var block = BuildBlock(segment.Occurrence, segment.Start, segment.End);
                var top = (segment.Start - dayStart).TotalMinutes / 60 * HourHeight;
                var height = Math.Max(18, (segment.End - segment.Start).TotalMinutes / 60 * HourHeight - 1);
                block.Width = Math.Max(10, width / columns - 3);
                block.Height = height;
                Canvas.SetLeft(block, i * width + column * width / columns + 1);
                Canvas.SetTop(block, top + 1);
                _canvas.Children.Add(block);
                _blocks.Add((segment.Occurrence, block));
            }
        }

        if (_data.Today >= _data.From && _data.Today <= _data.To)
        {
            var index = _data.Today.DayNumber - _data.From.DayNumber;
            var y = _data.Now.TimeOfDay.TotalMinutes / 60 * HourHeight;
            var line = new Rectangle { Width = _days > 1 ? width : _canvas.ActualWidth, Height = 2, Fill = CalendarStyle.Holiday, IsHitTestVisible = false };
            Canvas.SetLeft(line, _days > 1 ? index * width : 0);
            Canvas.SetTop(line, y - 1);
            var dot = new Ellipse { Width = 10, Height = 10, Fill = CalendarStyle.Holiday, IsHitTestVisible = false };
            Canvas.SetLeft(dot, (_days > 1 ? index * width : 0) - 5);
            Canvas.SetTop(dot, y - 5);
            _canvas.Children.Add(line);
            _canvas.Children.Add(dot);
        }
    }

    /// <summary>겹치는 일정끼리 묶어 가로로 나눈다.</summary>
    private static IEnumerable<((EventOccurrence Occurrence, DateTime Start, DateTime End) Segment, int Column, int Columns)> AssignColumns(
        List<(EventOccurrence Occurrence, DateTime Start, DateTime End)> segments)
    {
        var result = new List<((EventOccurrence, DateTime, DateTime), int, int)>();
        var cluster = new List<((EventOccurrence, DateTime Start, DateTime End) Segment, int Column)>();
        var columnEnds = new List<DateTime>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            foreach (var (segment, column) in cluster)
            {
                result.Add((segment, column, columnEnds.Count));
            }
            cluster.Clear();
            columnEnds.Clear();
        }

        foreach (var segment in segments)
        {
            var end = segment.End > segment.Start ? segment.End : segment.Start.AddMinutes(15);
            if (segment.Start >= clusterEnd)
            {
                Flush();
            }
            var column = columnEnds.FindIndex(e => e <= segment.Start);
            if (column < 0)
            {
                columnEnds.Add(end);
                column = columnEnds.Count - 1;
            }
            else
            {
                columnEnds[column] = end;
            }
            cluster.Add((segment, column));
            clusterEnd = end > clusterEnd ? end : clusterEnd;
        }
        Flush();
        return result;
    }

    private Border BuildBlock(EventOccurrence occurrence, DateTime segmentStart, DateTime segmentEnd)
    {
        var color = CalendarStyle.EventColor(occurrence);
        var pastOpen = occurrence.IsPastAndOpen(_data.Now);
        var title = CalendarStyle.Label((occurrence.IsCompleted ? "✓ " : pastOpen ? "⚠ " : "") + occurrence.Title, 12, null, FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.Wrap;
        var time = CalendarStyle.Label($"{CalendarStyle.Time(occurrence.Start)} – {CalendarStyle.Time(occurrence.End)}"
                                       + (occurrence.IsRecurring ? " ↻" : ""), 11);
        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(time);
        if (occurrence.Event.Location is { Length: > 0 } location)
        {
            panel.Children.Add(CalendarStyle.Label(location, 11));
        }

        var block = new Border
        {
            Child = panel,
            Padding = new Thickness(6, 3, 4, 2),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(3, 0, 0, 0),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            ToolTip = CalendarStyle.Tooltip(occurrence),
            Tag = occurrence,
        };

        if (occurrence.IsCompleted)
        {
            block.Background = CalendarStyle.DoneBackground;
            block.BorderBrush = CalendarStyle.DoneBorder;
            foreach (var text in new[] { title, time })
            {
                text.Foreground = CalendarStyle.Muted;
                text.TextDecorations = TextDecorations.Strikethrough;
            }
        }
        else if (pastOpen)
        {
            block.Background = CalendarStyle.Hatch;
            block.BorderBrush = CalendarStyle.PastOpenBorder;
            title.Foreground = time.Foreground = CalendarStyle.PastOpenText;
        }
        else
        {
            block.Background = CalendarStyle.Tint(color);
            block.BorderBrush = CalendarStyle.Solid(color);
            title.Foreground = time.Foreground = CalendarStyle.Dark(color);
        }

        block.MouseRightButtonUp += (_, e) =>
        {
            _host.ShowOccurrenceMenu(occurrence, block);
            e.Handled = true;
        };
        block.MouseMove += (_, e) =>
        {
            if (_drag == DragMode.None)
            {
                block.Cursor = e.GetPosition(block).Y >= block.ActualHeight - ResizeGrip ? Cursors.SizeNS : Cursors.Hand;
            }
        };
        return block;
    }

    // ---------- 끌기 ----------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(_canvas);
        _dragStarted = false;

        if (FindBlock(e.OriginalSource as DependencyObject) is { } block)
        {
            _dragBlock = block;
            _dragOccurrence = (EventOccurrence)block.Tag;
            _drag = e.GetPosition(block).Y >= block.ActualHeight - ResizeGrip ? DragMode.Resize : DragMode.Move;
        }
        else
        {
            _drag = DragMode.Create;
        }
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        if (!_dragStarted && (position - _dragOrigin).Length < DragThreshold)
        {
            return;
        }
        _dragStarted = true;

        var (start, end) = ComputeDragRange(position);
        ShowGhost(start, end);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        var mode = _drag;
        var occurrence = _dragOccurrence;
        var started = _dragStarted;
        var position = e.GetPosition(_canvas);
        var range = mode == DragMode.None ? default : ComputeDragRange(position);
        CancelDrag();

        switch (mode)
        {
            case DragMode.Create:
                if (started)
                {
                    _host.CreateEvent(range.Start, range.End, allDay: false);
                }
                else if (e.ClickCount >= 1)
                {
                    // 그냥 누르면 그 자리에서 1시간 일정
                    var start = Snap(PointToTime(_dragOrigin));
                    _host.CreateEvent(start, start.AddHours(1), allDay: false);
                }
                break;
            case DragMode.Move or DragMode.Resize when occurrence is not null:
                if (started)
                {
                    _host.MoveOccurrence(occurrence, range.Start, range.End);
                }
                else
                {
                    _host.OpenOccurrence(occurrence);
                }
                break;
        }
        e.Handled = true;
    }

    private (DateTime Start, DateTime End) ComputeDragRange(Point position)
    {
        var slot = TimeSpan.FromMinutes(Math.Max(5, _data.SlotMinutes));
        switch (_drag)
        {
            case DragMode.Create:
            {
                var a = Snap(PointToTime(_dragOrigin));
                var b = Snap(PointToTime(new Point(_dragOrigin.X, position.Y)));
                var start = a < b ? a : b;
                var end = a < b ? b : a;
                return (start, end > start ? end : start + slot);
            }
            case DragMode.Move:
            {
                var occurrence = _dragOccurrence!;
                var dayShift = (int)Math.Floor(position.X / ColumnWidth) - (int)Math.Floor(_dragOrigin.X / ColumnWidth);
                var minutes = (position.Y - _dragOrigin.Y) / HourHeight * 60;
                var shifted = occurrence.Start.AddDays(_days > 1 ? dayShift : 0).AddMinutes(minutes);
                var start = Snap(shifted);
                return (start, start + (occurrence.End - occurrence.Start));
            }
            case DragMode.Resize:
            {
                var occurrence = _dragOccurrence!;
                var end = Snap(occurrence.End.AddMinutes((position.Y - _dragOrigin.Y) / HourHeight * 60));
                return (occurrence.Start, end > occurrence.Start ? end : occurrence.Start + slot);
            }
            default:
                return default;
        }
    }

    private void ShowGhost(DateTime start, DateTime end)
    {
        if (_ghost is null)
        {
            _ghost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x67, 0xC0)),
                BorderBrush = CalendarStyle.Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                IsHitTestVisible = false,
                Child = CalendarStyle.Label("", 11, CalendarStyle.Accent, FontWeights.SemiBold),
                Padding = new Thickness(6, 2, 4, 2),
            };
            _canvas.Children.Add(_ghost);
        }
        if (_dragBlock is not null)
        {
            _dragBlock.Opacity = 0.4;
        }

        var dayIndex = Math.Clamp(DateOnly.FromDateTime(start).DayNumber - _data.From.DayNumber, 0, _days - 1);
        var dayStart = _data.From.AddDays(dayIndex).ToDateTime(TimeOnly.MinValue);
        var top = Math.Max(0, (start - dayStart).TotalMinutes / 60 * HourHeight);
        var bottom = Math.Min(_canvas.Height, (end - dayStart).TotalMinutes / 60 * HourHeight);
        Canvas.SetLeft(_ghost, dayIndex * ColumnWidth + 1);
        Canvas.SetTop(_ghost, top);
        _ghost.Width = ColumnWidth - 3;
        _ghost.Height = Math.Max(16, bottom - top);
        ((TextBlock)_ghost.Child).Text = $"{CalendarStyle.Time(start)} – {CalendarStyle.Time(end)}";
    }

    private void CancelDrag()
    {
        if (_ghost is not null)
        {
            _canvas.Children.Remove(_ghost);
            _ghost = null;
        }
        if (_dragBlock is not null)
        {
            _dragBlock.Opacity = 1;
        }
        _drag = DragMode.None;
        _dragBlock = null;
        _dragOccurrence = null;
        if (_canvas.IsMouseCaptured)
        {
            _canvas.ReleaseMouseCapture();
        }
    }

    private DateTime PointToTime(Point point)
    {
        var index = Math.Clamp((int)Math.Floor(point.X / ColumnWidth), 0, _days - 1);
        var minutes = Math.Clamp(point.Y / HourHeight * 60, 0, 24 * 60 - 1);
        return _data.From.AddDays(index).ToDateTime(TimeOnly.MinValue).AddMinutes(minutes);
    }

    private DateTime Snap(DateTime time)
    {
        var slot = Math.Max(5, _data.SlotMinutes);
        var minutes = Math.Round(time.TimeOfDay.TotalMinutes / slot) * slot;
        return time.Date.AddMinutes(minutes);
    }

    private Border? FindBlock(DependencyObject? element)
    {
        while (element is not null && element != _canvas)
        {
            if (element is Border { Tag: EventOccurrence } border)
            {
                return border;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
