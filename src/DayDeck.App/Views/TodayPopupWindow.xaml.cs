using System.Globalization;
using System.Windows;

namespace DayDeck.App.Views;

/// <summary>
/// 트레이 아이콘을 누르면 뜨는 "오늘" 팝업 (설계 §5.8): 오늘 할 일을 바로 체크하고 오늘 일정을 본다.
/// 화면 오른쪽 아래(작업 표시줄 위)에 뜨고, 다른 곳을 누르면 닫힌다.
/// </summary>
public partial class TodayPopupWindow : Window
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private readonly App _app = App.Current;
    private bool _closing;

    public TodayPopupWindow()
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        Refresh();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Bottom - ActualHeight - 12;
            _app.Tasks.Changed += OnDataChanged;
            Activate();
        };
        SizeChanged += (_, _) =>
        {
            // 할 일을 체크해 높이가 바뀌어도 아래쪽에 붙어 있게 한다.
            if (IsLoaded)
            {
                Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
            }
        };
        Deactivated += (_, _) =>
        {
            if (!_closing)
            {
                Close();
            }
        };
        Closed += (_, _) => _app.Tasks.Changed -= OnDataChanged;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void OnDataChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var now = DateTime.Now;
        DateText.Text = "오늘 · " + now.ToString("M월 d일 (ddd)", Korean);
        SummaryText.Text = TodayList.Fill(Body, _app, now);
    }

    private void OnQuickAdd(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ShowQuickAdd();
    }

    private void OnOpenMain(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ShowMainWindow();
    }
}
