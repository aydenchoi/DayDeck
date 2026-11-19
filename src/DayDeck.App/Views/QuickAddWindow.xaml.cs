using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DayDeck.App.Views.Calendar;
using DayDeck.Core.Calendar;
using DayDeck.Core.QuickAdd;

namespace DayDeck.App.Views;

/// <summary>
/// 빠른 추가 (설계 §5.7). 전역 단축키(Ctrl+Alt+Space)나 트레이에서 연다.
/// 한국어 자연어를 해석해 할 일 또는 일정을 만든다. 다른 곳을 누르면 닫힌다.
/// </summary>
public partial class QuickAddWindow : Window
{
    private static readonly Dictionary<QuickAddTokenKind, (Brush Background, Brush Foreground)> LightTokenColors = new()
    {
        [QuickAddTokenKind.Date] = (Frozen("#DCEBFB"), Frozen("#0B4F97")),
        [QuickAddTokenKind.Time] = (Frozen("#DCEBFB"), Frozen("#0B4F97")),
        [QuickAddTokenKind.Recurrence] = (Frozen("#F1E8FB"), Frozen("#6B2FA8")),
        [QuickAddTokenKind.Reminder] = (Frozen("#F1E8FB"), Frozen("#6B2FA8")),
        [QuickAddTokenKind.List] = (Frozen("#E8F5E9"), Frozen("#1B6E20")),
        [QuickAddTokenKind.Priority] = (Frozen("#FDE7E9"), Frozen("#C42B1C")),
        [QuickAddTokenKind.Tag] = (Frozen("#FFF0D6"), Frozen("#8A4B00")),
    };

    private static readonly Dictionary<QuickAddTokenKind, (Brush Background, Brush Foreground)> DarkTokenColors = new()
    {
        [QuickAddTokenKind.Date] = (Frozen("#1E3550"), Frozen("#9CCBFF")),
        [QuickAddTokenKind.Time] = (Frozen("#1E3550"), Frozen("#9CCBFF")),
        [QuickAddTokenKind.Recurrence] = (Frozen("#3A2852"), Frozen("#D4B5FF")),
        [QuickAddTokenKind.Reminder] = (Frozen("#3A2852"), Frozen("#D4B5FF")),
        [QuickAddTokenKind.List] = (Frozen("#203A22"), Frozen("#9FE0A3")),
        [QuickAddTokenKind.Priority] = (Frozen("#45282A"), Frozen("#FF9C94")),
        [QuickAddTokenKind.Tag] = (Frozen("#3D2E1E"), Frozen("#FFC98A")),
    };

    private static Dictionary<QuickAddTokenKind, (Brush Background, Brush Foreground)> TokenColors =>
        Services.ThemeService.IsDark ? DarkTokenColors : LightTokenColors;

    private readonly App _app = App.Current;
    private readonly QuickAddContext _context;
    private QuickAddResult? _result;
    private bool _closing;

    public QuickAddWindow(QuickAddContext? context = null)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        _context = context ?? new QuickAddContext();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + area.Height * 0.22;
            Activate();
            Input.Focus();
        };
        Deactivated += (_, _) => SafeClose();
        UpdatePreview();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 밖에서 닫을 때(작업 표시줄, Alt+F4, 자동화)에도 닫히는 동안 비활성화 → 다시 Close 하지 않도록 표시한다.
        _closing = true;
        base.OnClosing(e);
    }

    private void SafeClose()
    {
        if (!_closing)
        {
            _closing = true;
            Close();
        }
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        var text = Input.Text;
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Highlight.Inlines.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            _result = null;
            SummaryText.Text = "할 일은 그냥 쓰고, 시간을 쓰면 일정이 됩니다. \"까지\"를 붙이면 마감입니다.";
            TitleText.Text = "날짜·시간·반복(매주 월)·목록(#업무)·우선순위(!높음)·알림(10분 전 알림)을 알아봅니다.";
            return;
        }

        _result = new QuickAddParser(DateTime.Now).Parse(text);
        var position = 0;
        foreach (var token in _result.Tokens)
        {
            if (token.Start > position)
            {
                Highlight.Inlines.Add(new Run(text[position..token.Start]));
            }
            var (background, foreground) = TokenColors[token.Kind];
            Highlight.Inlines.Add(new Run(text.Substring(token.Start, token.Length)) { Background = background, Foreground = foreground });
            position = token.Start + token.Length;
        }
        if (position < text.Length)
        {
            Highlight.Inlines.Add(new Run(text[position..]));
        }

        SummaryText.Text = _app.QuickAdd.Describe(_result);
        TitleText.Text = "제목: " + _result.Title;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                Save(keepOpen: true, openDetail: false);
                e.Handled = true;
                break;
            case Key.Enter:
                Save(keepOpen: false, openDetail: false);
                e.Handled = true;
                break;
            case Key.Tab:
                Save(keepOpen: false, openDetail: true);
                e.Handled = true;
                break;
        }
    }

    private void Save(bool keepOpen, bool openDetail)
    {
        if (_result is null || string.IsNullOrWhiteSpace(Input.Text))
        {
            return;
        }

        var created = _app.QuickAdd.Create(_result, _context);
        if (keepOpen)
        {
            ResultText.Text = $"✓ 추가됨: {created.Title}";
            Input.Clear();
            return;
        }

        if (!openDetail)
        {
            // 무엇이 추가됐는지 잠깐 보여 주고 닫는다.
            ResultText.Text = $"✓ 추가됨: {created.Summary}";
            Input.IsEnabled = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SafeClose();
            };
            timer.Start();
            return;
        }

        _closing = true;
        Close();
        {
            if (created.Kind == QuickAddKind.Event && _app.Events.Get(created.Id) is { } calendarEvent)
            {
                _app.ShowMainWindow(MainPage.Calendar);
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    EventEditorWindow.Edit(_app.MainShell,
                        new EventOccurrence(calendarEvent, calendarEvent.Start, calendarEvent.End, calendarEvent.Start, calendarEvent.Title, false, false)));
            }
            else
            {
                _app.ShowTask(created.Id);
            }
        }
    }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
