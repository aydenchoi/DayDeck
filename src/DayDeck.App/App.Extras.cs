using System.IO;
using System.Text.Json;
using System.Windows;
using DayDeck.App.Views;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App;

/// <summary>M8 확장 기능: 바탕화면 위젯, 스티커 메모, 트레이 "오늘" 팝업.</summary>
public partial class App
{
    private const string StickiesKey = "stickies";
    private const string StickyFolder = "스티커";

    private readonly List<StickyNoteWindow> _stickies = [];
    private DesktopWidgetWindow? _widget;
    private bool _restoringStickies;

    internal bool IsWidgetVisible => _widget is not null;

    private void InitializeExtras()
    {
        Settings.Saved += (_, _) => ApplyWidgetSetting();
    }

    // ---------- 트레이 "오늘" 팝업 ----------

    private TodayPopupWindow? _todayPopup;
    private DateTime _todayPopupClosedAt;

    /// <summary>트레이 아이콘 왼쪽 클릭 (설정에 따라).</summary>
    internal void OnTrayClick()
    {
        switch (Settings.Current.Tray.LeftClick)
        {
            case Core.Settings.TrayClickAction.OpenToday:
                ShowTodayPopup();
                break;
            case Core.Settings.TrayClickAction.QuickAdd:
                ShowQuickAdd();
                break;
            default:
                ShowMainWindow();
                break;
        }
    }

    /// <summary>오늘 할 일과 일정 팝업. 이미 떠 있으면 닫는다 (아이콘을 다시 누르면 토글).</summary>
    public void ShowTodayPopup()
    {
        if (!_ready)
        {
            ShowMainWindow();
            return;
        }
        if (_todayPopup is not null)
        {
            _todayPopup.Close();
            return;
        }
        // 떠 있을 때 아이콘을 누르면 먼저 팝업이 포커스를 잃어 닫힌다. 그 직후의 클릭은 "닫기"로 본다.
        if (DateTime.Now - _todayPopupClosedAt < TimeSpan.FromMilliseconds(400))
        {
            return;
        }
        _todayPopup = new TodayPopupWindow();
        _todayPopup.Closed += (_, _) =>
        {
            _todayPopup = null;
            _todayPopupClosedAt = DateTime.Now;
        };
        _todayPopup.Show();
    }

    /// <summary>오늘 남은 시간 일정 중 가장 가까운 것 (트레이 도구 설명용).</summary>
    internal Core.Calendar.EventOccurrence? NextEventToday()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        return Events.GetOccurrences(today, today)
            .Where(o => !o.IsAllDay && !o.IsCompleted && o.Start > now && o.FirstDay == today)
            .MinBy(o => o.Start);
    }

    // ---------- 바탕화면 위젯 ----------

    public void ToggleWidget()
    {
        Settings.Current.Widget.Enabled = !Settings.Current.Widget.Enabled;
        Settings.Save();
    }

    private void ApplyWidgetSetting()
    {
        if (!_ready || _exiting)
        {
            return;
        }
        var enabled = Settings.Current.Widget.Enabled;
        if (enabled && _widget is null)
        {
            _widget = new DesktopWidgetWindow();
            _widget.Closed += (_, _) => _widget = null;
            _widget.Show();
        }
        else if (!enabled && _widget is not null)
        {
            _widget.Close();
        }
        _widget?.ApplySettings();
    }

    // ---------- 스티커 메모 ----------

    /// <summary>"스티커" 폴더에 새 메모를 만들고 스티커로 띄운다.</summary>
    public void NewSticky()
    {
        if (!_ready)
        {
            return;
        }
        var path = Notes.Create(StickyFolder, $"스티커 {DateTime.Now:yyyy-MM-dd HHmm}", ".txt");
        OpenSticky(path);
    }

    /// <summary>메모를 바탕화면 스티커로 띄운다. 이미 떠 있으면 앞으로 가져온다.</summary>
    public void OpenSticky(string notePath, StickyState? state = null)
    {
        if (_stickies.FirstOrDefault(s => s.NotePath == notePath) is { } open)
        {
            open.Activate();
            return;
        }
        var window = new StickyNoteWindow(notePath, state);
        window.Closed += (_, _) =>
        {
            _stickies.Remove(window);
            if (!_exiting)
            {
                SaveStickies();
            }
        };
        _stickies.Add(window);
        window.Show();
        SaveStickies();
    }

    /// <summary>스티커 위치/색이 바뀌었다.</summary>
    internal void SaveStickies()
    {
        if (_restoringStickies || !_ready)
        {
            return;
        }
        try
        {
            AppState.Set(StickiesKey, JsonSerializer.Serialize(_stickies.Select(s => s.State).ToList()));
        }
        catch (Exception ex)
        {
            Log.Warn("스티커 상태 저장 실패", ex);
        }
    }

    private void RestoreDesktopWindows()
    {
        ApplyWidgetSetting();
        if (AppState.Get(StickiesKey) is not { Length: > 0 } json)
        {
            return;
        }
        _restoringStickies = true;
        try
        {
            foreach (var state in JsonSerializer.Deserialize<List<StickyState>>(json) ?? [])
            {
                if (Notes.Exists(state.Path))
                {
                    OpenSticky(state.Path, state);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn("스티커를 다시 띄우지 못함", ex);
        }
        finally
        {
            _restoringStickies = false;
        }
        SaveStickies();
    }

    /// <summary>종료할 때: 떠 있던 스티커를 기억하고 편집 내용을 저장한다 (다음 실행 때 다시 띄움).</summary>
    private void SaveDesktopWindows()
    {
        if (!_ready)
        {
            return;
        }
        SaveStickies();
        foreach (var sticky in _stickies.ToList())
        {
            sticky.Flush();
        }
        _widget?.SaveBounds();
    }
}

/// <summary>스티커 하나의 위치, 크기, 색 (app_state에 JSON으로 저장 → 데이터 폴더와 함께 옮겨짐).</summary>
public sealed record StickyState(string Path, double Left, double Top, double Width, double Height, int Color);
