using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.App.Services;
using DayDeck.Core.Todo;

namespace DayDeck.App.Views;

/// <summary>
/// 바탕화면 위젯 (Rainlendar 참고): 오늘 할 일과 오늘 일정을 늘 보이게 둔다. 항상 위가 아니라 다른 창 뒤에 깔린다.
/// 여기서 바로 완료 체크를 할 수 있다. 위치·크기는 app_state에 저장한다.
/// </summary>
public partial class DesktopWidgetWindow : Window
{
    private const string BoundsKey = "widget.bounds";
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private readonly App _app = App.Current;

    public DesktopWidgetWindow()
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        LoadBounds();
        Loaded += (_, _) =>
        {
            _app.Tasks.Changed += OnDataChanged;
            _app.Events.Changed += OnDataChanged;
            _app.MinuteTick += OnDataChanged;
            ThemeService.Changed += OnDataChanged;
            Refresh();
        };
        Closed += (_, _) =>
        {
            SaveBounds();
            _app.Tasks.Changed -= OnDataChanged;
            _app.Events.Changed -= OnDataChanged;
            _app.MinuteTick -= OnDataChanged;
            ThemeService.Changed -= OnDataChanged;
        };
    }

    public void ApplySettings() => Opacity = _app.Settings.Current.Widget.Opacity;

    public void SaveBounds()
    {
        try
        {
            _app.AppState.Set(BoundsKey, JsonSerializer.Serialize(new[] { Left, Top, ActualWidth, ActualHeight }));
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("위젯 위치 저장 실패", ex);
        }
    }

    private void LoadBounds()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
        try
        {
            if (_app.AppState.Get(BoundsKey) is { Length: > 0 } json && JsonSerializer.Deserialize<double[]>(json) is [var left, var top, var width, var height])
            {
                // 다른 PC로 옮겨 화면이 작아졌으면 화면 안에 들어오게 줄이고 당긴다.
                var screenLeft = SystemParameters.VirtualScreenLeft;
                var screenTop = SystemParameters.VirtualScreenTop;
                Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
                Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
                Left = Math.Clamp(left, screenLeft, Math.Max(screenLeft, screenLeft + SystemParameters.VirtualScreenWidth - Width));
                Top = Math.Clamp(top, screenTop, Math.Max(screenTop, screenTop + SystemParameters.VirtualScreenHeight - Height));
            }
        }
        catch (JsonException)
        {
        }
    }

    private void OnDataChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var now = DateTime.Now;
        DateText.Text = now.ToString("M월 d일 (ddd)", Korean);

        SummaryText.Text = TodayList.Fill(Body, _app, now);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            SaveBounds();
        }
    }

    private void OnOpenMain(object sender, RoutedEventArgs e) => _app.ShowMainWindow();

    private void OnQuickAdd(object sender, RoutedEventArgs e) => _app.ShowQuickAdd();
}
