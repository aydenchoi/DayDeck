using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Views;

/// <summary>
/// 바탕화면 스티커 메모 (Windows 스티커 메모 참고). 내용은 메모 폴더의 파일 그대로라서 메모 화면에서도 보이고,
/// 위치·색은 app_state에 저장되어 다음 실행 때 다시 뜬다. 편집을 멈추고 1초 뒤, 창을 떠날 때 저장한다.
/// </summary>
public partial class StickyNoteWindow : Window
{
    // (바탕, 글자) — 노랑, 초록, 분홍, 보라, 파랑, 회색. 두 테마 모두 같은 색(종이 느낌)을 쓴다.
    private static readonly (Color Back, Color Header)[] Palette =
    [
        (Color.FromRgb(0xFF, 0xF7, 0xC2), Color.FromRgb(0xFF, 0xEB, 0x80)),
        (Color.FromRgb(0xE3, 0xF6, 0xD8), Color.FromRgb(0xC5, 0xEB, 0xB0)),
        (Color.FromRgb(0xFD, 0xE2, 0xEC), Color.FromRgb(0xF8, 0xC3, 0xD6)),
        (Color.FromRgb(0xEC, 0xE3, 0xFB), Color.FromRgb(0xD6, 0xC6, 0xF6)),
        (Color.FromRgb(0xDF, 0xEE, 0xFB), Color.FromRgb(0xBF, 0xDC, 0xF6)),
        (Color.FromRgb(0xEE, 0xEE, 0xEE), Color.FromRgb(0xDA, 0xDA, 0xDA)),
    ];

    private readonly App _app = App.Current;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading = true;
    private bool _dirty;
    private int _color;

    public StickyNoteWindow(string notePath, StickyState? state)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this, resize: false); // 크기는 사용자가 정한 대로
        NotePath = notePath;
        _saveTimer.Tick += (_, _) => Flush();

        if (state is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = state.Left;
            Top = state.Top;
            Width = Math.Max(MinWidth, state.Width);
            Height = Math.Max(MinHeight, state.Height);
            _color = Math.Clamp(state.Color, 0, Palette.Length - 1);
            EnsureOnScreen();
        }
        else
        {
            var area = SystemParameters.WorkArea;
            var offset = (_app.Notes.Exists(notePath) ? notePath.Length % 6 : 0) * 24;
            Left = area.Right - Width - 40 - offset;
            Top = area.Top + 120 + offset;
        }

        ApplyColor();
        TitleText.Text = Path.GetFileNameWithoutExtension(notePath);
        Title = "스티커 메모 - " + TitleText.Text;
        try
        {
            Editor.Text = _app.Notes.Read(notePath);
        }
        catch (IOException ex)
        {
            Log.Warn($"스티커 메모를 읽지 못함: {notePath}", ex);
        }
        _loading = false;

        Deactivated += (_, _) => Flush();
        LocationChanged += (_, _) => ScheduleStateSave();
        SizeChanged += (_, _) => ScheduleStateSave();
        Closing += (_, _) => Flush();
        Loaded += (_, _) =>
        {
            if (Editor.Text.Length == 0)
            {
                Editor.Focus();
            }
        };
    }

    public string NotePath { get; }

    public StickyState State => new(NotePath, Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height, _color);

    /// <summary>편집 내용을 저장한다.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty)
        {
            return;
        }
        try
        {
            _app.Notes.Save(NotePath, Editor.Text);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다음 입력이나 창을 닫을 때 다시 저장한다 (_dirty 그대로).
            Log.Warn($"스티커 메모 저장 실패: {NotePath}", ex);
        }
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherTimer? _stateTimer;

    private void ScheduleStateSave()
    {
        if (_loading || !IsLoaded)
        {
            return;
        }
        _stateTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _stateTimer!.Stop();
            _app.SaveStickies();
        }, Dispatcher);
        _stateTimer.Stop();
        _stateTimer.Start();
    }

    private void ApplyColor()
    {
        var (back, header) = Palette[_color];
        Background = Frozen(back);
        Header.Background = Frozen(header);
        BorderBrush = Frozen(header);
        // 종이 색은 밝으므로 테마와 상관없이 어두운 글자.
        var ink = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
        Editor.Foreground = ink;
        Editor.CaretBrush = ink;
        TitleText.Foreground = ink;
        foreach (var button in new[] { PinButton, ColorButton, CloseButton })
        {
            button.Foreground = ink;
        }
    }

    private void OnColor(object sender, RoutedEventArgs e)
    {
        _color = (_color + 1) % Palette.Length;
        ApplyColor();
        _app.SaveStickies();
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Opacity = Topmost ? 1 : 0.55;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>다른 PC(화면 크기가 다른)로 옮겨 왔을 때 화면 밖에 뜨지 않게 한다.</summary>
    private void EnsureOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
