using System.Collections.ObjectModel;
using System.IO;
using System.Media;
using DayDeck.App.ViewModels;
using DayDeck.App.Views;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Reminders;

namespace DayDeck.App.Services;

/// <summary>
/// 알림 엔진의 결과를 화면에 보여 준다: 팝업 카드, 소리, 트레이 아이콘 강조.
/// 팝업 창은 카드가 있을 때만 만들고, 모두 처리하면 닫는다.
/// </summary>
internal sealed class ReminderPresenter
{
    private readonly App _app;
    private readonly ObservableCollection<ReminderCardViewModel> _cards = [];
    private ReminderWindow? _window;
    private SoundPlayer? _player;

    public ReminderPresenter(App app)
    {
        _app = app;
    }

    /// <summary>보이는 카드 수가 바뀌었다 (트레이 아이콘 강조용).</summary>
    public event EventHandler? PendingChanged;

    public int PendingCount => _cards.Count;

    public void ShowReminders(IReadOnlyList<DueReminder> due)
    {
        var now = DateTime.Now;
        var missed = due.Where(d => d.IsMissed).ToList();
        var onTime = due.Where(d => !d.IsMissed).ToList();

        // 놓친 알림이 여럿이면 한 카드로 묶는다.
        if (missed.Count > 1)
        {
            RemoveCardsFor(missed);
            AddCard(ReminderCardViewModel.ForMissed(missed, now));
        }
        else
        {
            onTime.AddRange(missed);
        }

        foreach (var reminder in onTime)
        {
            // 끈질긴 알림이 다시 울리면 같은 카드를 맨 위로 올린다.
            RemoveCardsFor([reminder]);
            AddCard(ReminderCardViewModel.ForReminder(reminder, now));
        }

        PlaySound();
    }

    public void ShowBriefing(Briefing briefing)
    {
        foreach (var old in _cards.Where(c => c.IsBriefing).ToList())
        {
            _cards.Remove(old);
        }
        AddCard(ReminderCardViewModel.ForBriefing(briefing, DateTime.Now));
        PlaySound();
    }

    /// <summary>다른 곳에서 완료/삭제한 할 일의 카드를 치운다.</summary>
    public void PruneResolved()
    {
        foreach (var card in _cards.Where(c => c.ItemId is { } id && !c.Reminders[0].IsEvent
                                               && _app.Tasks.Get(id) is not { Status: Core.Todo.ItemStatus.Open, DeletedAt: null }).ToList())
        {
            RemoveCard(card);
        }
    }

    public void Complete(ReminderCardViewModel card)
    {
        foreach (var reminder in card.Reminders.DistinctBy(r => r.ItemId))
        {
            if (!reminder.IsEvent)
            {
                _app.Tasks.SetCompleted(reminder.ItemId, true);
            }
            else if (_app.Events.Get(reminder.ItemId) is { IsRecurring: true } && OccurrenceStart(reminder) is { } start)
            {
                // 반복 일정은 이번 회차만 완료한다.
                _app.Events.SetOccurrenceCompleted(reminder.ItemId, start, true);
            }
            else
            {
                _app.Events.SetCompleted(reminder.ItemId, true);
            }
        }
        Dismiss(card);
    }

    private static DateTime? OccurrenceStart(DueReminder reminder) =>
        reminder.DueDate?.ToDateTime(reminder.DueTime ?? TimeOnly.MinValue);

    public void Snooze(ReminderCardViewModel card, DateTime until)
    {
        foreach (var reminder in card.Reminders)
        {
            _app.Reminders.Snooze(reminder.ReminderId, until);
        }
        RemoveCard(card);
    }

    /// <summary>확인: 이 알림은 다시 울리지 않는다 (끈질긴 알림도 멈춘다).</summary>
    public void Dismiss(ReminderCardViewModel card)
    {
        foreach (var reminder in card.Reminders)
        {
            _app.Reminders.Dismiss(reminder.ReminderId);
        }
        RemoveCard(card);
    }

    public void DismissAll()
    {
        foreach (var card in _cards.ToList())
        {
            Dismiss(card);
        }
    }

    public void Open(ReminderCardViewModel card)
    {
        if (card.ItemId is { } itemId)
        {
            _app.ShowTask(itemId, card.Reminders[0].IsEvent ? OccurrenceStart(card.Reminders[0]) : null);
        }
        Dismiss(card);
    }

    public void OpenToday(ReminderCardViewModel card)
    {
        _app.ShowMainWindow(MainPage.Today);
        Dismiss(card);
    }

    /// <summary>설정 화면의 "소리 테스트".</summary>
    public void PlaySound()
    {
        var sound = _app.Settings.Current.Reminders.Sound;
        try
        {
            if (string.Equals(sound, "None", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var path = string.Equals(sound, "Default", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify Calendar.wav")
                : sound;
            if (File.Exists(path))
            {
                // Play()는 비동기로 재생하므로 재생이 끝나기 전에 Dispose하지 않도록 들고 있는다.
                if (_player?.SoundLocation != path)
                {
                    _player?.Dispose();
                    _player = new SoundPlayer(path);
                }
                _player.Play();
            }
            else
            {
                SystemSounds.Exclamation.Play();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn($"알림 소리 재생 실패: {sound}", ex);
            SystemSounds.Exclamation.Play();
        }
    }

    private void AddCard(ReminderCardViewModel card)
    {
        _cards.Insert(0, card);
        if (_window is null)
        {
            _window = new ReminderWindow(this, _cards);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveCardsFor(IEnumerable<DueReminder> reminders)
    {
        var ids = reminders.Select(r => r.ReminderId).ToHashSet();
        foreach (var card in _cards.Where(c => c.Reminders.Any(r => ids.Contains(r.ReminderId))).ToList())
        {
            _cards.Remove(card);
        }
    }

    private void RemoveCard(ReminderCardViewModel card)
    {
        _cards.Remove(card);
        if (_cards.Count == 0)
        {
            _window?.Close();
        }
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }
}
