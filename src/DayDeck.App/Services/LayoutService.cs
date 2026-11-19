using System.Text.Json;
using System.Windows;
using DayDeck.Core.Data;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Services;

/// <summary>
/// 사용자가 끌어서 바꾼 화면 배치(구역 너비)와 메인 창 위치·크기를 기억한다.
/// app_state(데이터 폴더의 DB)에 저장하므로 데이터 폴더를 옮기면 함께 옮겨진다.
/// 너비는 글자 크기 확대 전의 논리 단위라 글자 크기를 바꿔도 비율이 유지된다.
/// </summary>
internal sealed class LayoutService
{
    private const string PanesKey = "layout.panes";
    private const string MainWindowKey = "layout.main-window";

    private readonly AppStateStore _state;
    private Dictionary<string, double>? _panes;

    // 초기화 직후 창을 닫을 때 지금 크기를 다시 저장하지 않게 한다.
    private bool _skipNextWindowSave;

    public LayoutService(AppStateStore state) => _state = state;

    public double? GetWidth(string key) => Panes.TryGetValue(key, out var width) ? width : null;

    public void SetWidth(string key, double width)
    {
        Panes[key] = Math.Round(width, 1);
        Save(PanesKey, JsonSerializer.Serialize(Panes));
    }

    /// <summary>모든 구역 너비를 처음 값으로 (설정 화면의 "배치 초기화").</summary>
    public void Reset()
    {
        Panes.Clear();
        Save(PanesKey, null);
        Save(MainWindowKey, null);
        _skipNextWindowSave = true;
    }

    public WindowBounds? GetMainWindow()
    {
        try
        {
            return _state.Get(MainWindowKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<WindowBounds>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SetMainWindow(WindowBounds bounds)
    {
        if (_skipNextWindowSave)
        {
            _skipNextWindowSave = false;
            return;
        }
        Save(MainWindowKey, JsonSerializer.Serialize(bounds));
    }

    private Dictionary<string, double> Panes
    {
        get
        {
            if (_panes is null)
            {
                try
                {
                    _panes = _state.Get(PanesKey) is { Length: > 0 } json
                        ? JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? []
                        : [];
                }
                catch (JsonException)
                {
                    _panes = [];
                }
            }
            return _panes;
        }
    }

    private void Save(string key, string? value)
    {
        try
        {
            _state.Set(key, value);
        }
        catch (Exception ex)
        {
            Log.Warn($"화면 배치 저장 실패 ({key})", ex);
        }
    }
}

/// <summary>창 위치·크기 (최대화였으면 복원 크기와 함께).</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized)
{
    /// <summary>다른 PC(화면이 작거나 모니터 배치가 다른)로 옮겼을 때 화면 안으로 들인다.</summary>
    public WindowBounds FitToScreen(double minWidth, double minHeight)
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        var width = Math.Clamp(Width, Math.Min(minWidth, screenWidth), screenWidth);
        var height = Math.Clamp(Height, Math.Min(minHeight, screenHeight), screenHeight);
        return this with
        {
            Width = width,
            Height = height,
            Left = Math.Clamp(Left, left, left + screenWidth - width),
            Top = Math.Clamp(Top, top, top + screenHeight - height),
        };
    }
}
