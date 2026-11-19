using System.Globalization;
using DayDeck.Core.Data;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Reminders;

/// <summary>
/// 알림 엔진 (설계 문서 §5.4.3). 주기적으로 폴링하지 않고 "가장 먼저 울릴 시각" 하나에만 타이머를 건다.
/// 절전 중에는 타이머가 늦게 깨므로, 절전 복귀/시각 변경 때 호출자가 <see cref="Reschedule"/>을 불러야 한다.
/// 안전을 위해 타이머 간격은 최대 <see cref="MaxWait"/>로 제한한다.
/// </summary>
public sealed class ReminderEngine : IDisposable
{
    public const string BriefingStateKey = "briefing.lastDate";
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private readonly ReminderRepository _reminders;
    private readonly TaskRepository _tasks;
    private readonly AppStateStore _state;
    private readonly Func<ReminderSettings> _settings;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly ITimer _timer;
    private bool _checking;
    private bool _recheck;
    private bool _started;

    public ReminderEngine(ReminderRepository reminders, TaskRepository tasks, AppStateStore state,
        Func<ReminderSettings> settings, TimeProvider time, SynchronizationContext? context)
    {
        _reminders = reminders;
        _tasks = tasks;
        _state = state;
        _settings = settings;
        _time = time;
        _context = context;
        _timer = time.CreateTimer(_ => Post(Check), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>울릴 알림들. 이미 "울림"으로 기록된 뒤에 발생한다.</summary>
    public event EventHandler<IReadOnlyList<DueReminder>>? RemindersDue;

    public event EventHandler<Briefing>? BriefingDue;

    /// <summary>알림 일시 중지 상태가 바뀌었다.</summary>
    public event EventHandler? PauseChanged;

    /// <summary>알림 일시 중지 종료 시각. 그동안 울릴 알림은 재개 후 "놓친 알림"으로 보여 준다.</summary>
    public DateTime? PausedUntil { get; private set; }

    /// <summary>다음 타이머 시각 (진단/테스트용).</summary>
    public DateTime? NextCheckAt { get; private set; }

    private DateTime Now => _time.GetLocalNow().DateTime;

    public void Start()
    {
        _started = true;
        Check();
    }

    /// <summary>데이터가 바뀌었거나, 절전에서 깨어났거나, 시스템 시각이 바뀌었을 때 부른다.</summary>
    public void Reschedule()
    {
        if (_started)
        {
            Check();
        }
    }

    public void Pause(DateTime? until)
    {
        PausedUntil = until is { } u && u > Now ? u : null;
        PauseChanged?.Invoke(this, EventArgs.Empty);
        Reschedule();
    }

    /// <summary>오늘 브리핑을 지금 만든다 (트레이 메뉴 "오늘의 할 일").</summary>
    public Briefing CreateBriefing()
    {
        var now = Now;
        var today = _tasks.GetToday();
        var open = TaskRules.OrderOpen(today.Where(i => i.Status == ItemStatus.Open), now).ToList();
        return new Briefing(DateOnly.FromDateTime(now), open.Count, open.Count(i => TaskRules.IsOverdue(i, now)), open.Take(6).ToList());
    }

    private void Check()
    {
        // 알림 처리 중에 데이터가 바뀌면(울림 기록 등) Reschedule이 다시 불린다. 중첩 대신 한 번 더 돈다.
        if (_checking)
        {
            _recheck = true;
            return;
        }

        _checking = true;
        try
        {
            do
            {
                _recheck = false;
                CheckOnce();
            }
            while (_recheck);
        }
        catch (Exception ex)
        {
            Log.Error("알림 확인 중 오류", ex);
            ScheduleAt(Now + TimeSpan.FromMinutes(1));
        }
        finally
        {
            _checking = false;
        }
    }

    private void CheckOnce()
    {
        var now = Now;

        if (PausedUntil is { } paused)
        {
            if (now < paused)
            {
                ScheduleAt(paused);
                return;
            }
            PausedUntil = null;
            PauseChanged?.Invoke(this, EventArgs.Empty);
        }

        var due = _reminders.GetDue(now);
        if (due.Count > 0)
        {
            foreach (var reminder in due)
            {
                _reminders.MarkFired(reminder.ReminderId, now);
            }
            // 반복 일정은 다음 회차 알림을 예약한다.
            _reminders.RecomputeAll();
            Log.Info($"알림 {due.Count}개 (놓친 알림 {due.Count(d => d.IsMissed)}개)");
            RemindersDue?.Invoke(this, due);
        }

        var settings = _settings();
        var briefingTime = settings.GetDailyBriefingTime();
        var today = DateOnly.FromDateTime(now);
        if (settings.DailyBriefingEnabled && TimeOnly.FromDateTime(now) >= briefingTime && LastBriefingDate() != today)
        {
            _state.Set(BriefingStateKey, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            BriefingDue?.Invoke(this, CreateBriefing());
        }

        var candidates = new List<DateTime> { now + MaxWait };
        if (_reminders.GetNextFireTime() is { } next)
        {
            candidates.Add(next);
        }
        if (settings.DailyBriefingEnabled)
        {
            var nextBriefingDay = LastBriefingDate() == today || TimeOnly.FromDateTime(now) >= briefingTime ? today.AddDays(1) : today;
            candidates.Add(nextBriefingDay.ToDateTime(briefingTime));
        }
        ScheduleAt(candidates.Min());
    }

    private DateOnly? LastBriefingDate() =>
        DateOnly.TryParseExact(_state.Get(BriefingStateKey), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private void ScheduleAt(DateTime at)
    {
        NextCheckAt = at;
        var delay = at - Now;
        if (delay < TimeSpan.FromMilliseconds(200))
        {
            delay = TimeSpan.FromMilliseconds(200);
        }
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }

    public void Dispose() => _timer.Dispose();
}
