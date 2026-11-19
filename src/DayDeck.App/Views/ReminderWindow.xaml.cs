using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.App.Services;
using DayDeck.App.ViewModels;

namespace DayDeck.App.Views;

/// <summary>
/// 화면 오른쪽 아래에 뜨는 알림 팝업. 포커스를 빼앗지 않고(ShowActivated=False) 항상 위에 있다.
/// 집중 모드나 Windows 알림 설정과 상관없이 보이고, 사용자가 처리할 때까지 남아 있다.
/// </summary>
public partial class ReminderWindow : Window
{
    private readonly ReminderPresenter _presenter;
    private bool _userMoved;

    internal ReminderWindow(ReminderPresenter presenter, ObservableCollection<ReminderCardViewModel> cards)
    {
        InitializeComponent();
        Services.FontService.ScaleWindow(this);
        _presenter = presenter;
        DataContext = this;
        Cards.ItemsSource = cards;
        Scroller.MaxHeight = SystemParameters.WorkArea.Height * 0.8;
        cards.CollectionChanged += (_, _) => CloseAllButton.Visibility = cards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CloseAllButton.Visibility = cards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SizeChanged += (_, _) => PlaceAtCorner();
    }

    public string SnoozeLabel => $"⏰ {FormatMinutes(App.Current.Settings.Current.Reminders.DefaultSnoozeMinutes)} 후";

    private static string FormatMinutes(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes}분";

    private void PlaceAtCorner()
    {
        if (_userMoved)
        {
            return;
        }
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _userMoved = true;
            DragMove();
        }
    }

    private static ReminderCardViewModel Card(object sender) => (ReminderCardViewModel)((FrameworkElement)sender).Tag;

    private void OnComplete(object sender, RoutedEventArgs e) => _presenter.Complete(Card(sender));

    private void OnSnoozeDefault(object sender, RoutedEventArgs e) =>
        _presenter.Snooze(Card(sender), DateTime.Now.AddMinutes(App.Current.Settings.Current.Reminders.DefaultSnoozeMinutes));

    private void OnSnoozeHour(object sender, RoutedEventArgs e) => _presenter.Snooze(Card(sender), DateTime.Now.AddHours(1));

    private void OnSnoozeMenu(object sender, RoutedEventArgs e)
    {
        var card = Card(sender);
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var tomorrowMorning = DateOnly.FromDateTime(DateTime.Now).AddDays(1)
            .ToDateTime(App.Current.Settings.Current.Reminders.GetDateOnlyReminderTime());
        foreach (var (label, until) in new (string, DateTime)[]
                 {
                     ("5분 후", DateTime.Now.AddMinutes(5)),
                     ("10분 후", DateTime.Now.AddMinutes(10)),
                     ("30분 후", DateTime.Now.AddMinutes(30)),
                     ("1시간 후", DateTime.Now.AddHours(1)),
                     ("3시간 후", DateTime.Now.AddHours(3)),
                     ($"내일 {tomorrowMorning:HH:mm}", tomorrowMorning),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _presenter.Snooze(card, until);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnOpen(object sender, RoutedEventArgs e) => _presenter.Open(Card(sender));

    private void OnOpenToday(object sender, RoutedEventArgs e) => _presenter.OpenToday(Card(sender));

    private void OnDismiss(object sender, RoutedEventArgs e) => _presenter.Dismiss(Card(sender));

    private void OnCloseAll(object sender, RoutedEventArgs e) => _presenter.DismissAll();
}
