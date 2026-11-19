using DayDeck.Core.Diagnostics;
using DayDeck.Core.Settings;

namespace DayDeck.Core.AutoSave;

/// <summary>편집 중인 대상 (할 일 상세, 메모 편집기 등).</summary>
public interface IAutoSaveTarget
{
    bool IsDirty { get; }

    /// <summary>변경 내용을 저장하고 IsDirty를 false로 만든다.</summary>
    void Save();
}

/// <summary>
/// 자동 저장 (설계 문서 §5.6). 세 가지 시점에 더티 대상만 저장한다.
/// <list type="bullet">
/// <item>편집을 멈추고 <see cref="AutoSaveSettings.DebounceSeconds"/>초 뒤 (켜져 있을 때)</item>
/// <item><see cref="AutoSaveSettings.IntervalMinutes"/>분마다</item>
/// <item>창이 포커스를 잃거나 닫힐 때 등 호출자가 <see cref="FlushAll"/>을 부를 때</item>
/// </list>
/// 타이머 콜백은 <paramref name="context"/>(UI 스레드)로 넘겨 대상이 UI 스레드에서만 접근되게 한다.
/// </summary>
public sealed class AutoSaveService : IDisposable
{
    private readonly Func<AutoSaveSettings> _settings;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _context;
    private readonly List<IAutoSaveTarget> _targets = [];
    private readonly ITimer _intervalTimer;
    private readonly ITimer _debounceTimer;

    public AutoSaveService(Func<AutoSaveSettings> settings, TimeProvider time, SynchronizationContext? context)
    {
        _settings = settings;
        _time = time;
        _context = context;
        _intervalTimer = time.CreateTimer(_ => Dispatch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer = time.CreateTimer(_ => Dispatch(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ApplySettings();
    }

    /// <summary>대상이 편집되었다. 상태 표시용.</summary>
    public event EventHandler? Edited;

    /// <summary>하나 이상 저장했다. 인자는 저장 시각.</summary>
    public event EventHandler<DateTimeOffset>? Saved;

    /// <summary>설정의 저장 간격이 바뀌었으면 호출한다.</summary>
    public void ApplySettings()
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings().IntervalMinutes,
            AutoSaveSettings.MinIntervalMinutes, AutoSaveSettings.MaxIntervalMinutes));
        _intervalTimer.Change(interval, interval);
    }

    public void Register(IAutoSaveTarget target)
    {
        if (!_targets.Contains(target))
        {
            _targets.Add(target);
        }
    }

    /// <summary>대상을 저장한 뒤 목록에서 뺀다.</summary>
    public void Unregister(IAutoSaveTarget target)
    {
        if (_targets.Remove(target))
        {
            SaveTargets([target]);
        }
    }

    public void NotifyEdited(IAutoSaveTarget target)
    {
        Register(target);
        Edited?.Invoke(this, EventArgs.Empty);

        var settings = _settings();
        if (settings.SaveAfterEdit)
        {
            _debounceTimer.Change(TimeSpan.FromSeconds(settings.DebounceSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>더티 대상을 모두 저장한다. 저장한 개수를 돌려준다.</summary>
    public int FlushAll()
    {
        _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return SaveTargets([.. _targets]);
    }

    public bool HasUnsavedChanges => _targets.Any(t => t.IsDirty);

    private int SaveTargets(IEnumerable<IAutoSaveTarget> targets)
    {
        var saved = 0;
        foreach (var target in targets)
        {
            if (!target.IsDirty)
            {
                continue;
            }

            try
            {
                target.Save();
                saved++;
            }
            catch (Exception ex)
            {
                // 한 대상의 실패가 다른 대상 저장을 막지 않게 한다. 대상은 더티로 남아 다음에 다시 시도된다.
                Log.Error($"자동 저장 실패: {target}", ex);
            }
        }

        if (saved > 0)
        {
            Saved?.Invoke(this, _time.GetLocalNow());
        }
        return saved;
    }

    private void Dispatch()
    {
        if (_context is null)
        {
            FlushAll();
        }
        else
        {
            _context.Post(_ => FlushAll(), null);
        }
    }

    public void Dispose()
    {
        _intervalTimer.Dispose();
        _debounceTimer.Dispose();
    }
}
