using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Services;

/// <summary>
/// 전역 단축키 (RegisterHotKey). 메시지 전용 창 하나로 받는다.
/// 다른 프로그램이 이미 쓰는 조합이면 등록에 실패하고, 그 결과를 <see cref="Failures"/>로 알려 준다.
/// </summary>
internal sealed partial class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("DayDeckHotkeys") { ParentWindow = new IntPtr(-3) }); // HWND_MESSAGE
        _window.AddHook(WndProc);
    }

    /// <summary>등록에 실패한 단축키 (예: "Ctrl+Alt+Space"). 설정 화면에 표시한다.</summary>
    public IReadOnlyList<string> Failures { get; private set; } = [];

    /// <summary>모든 단축키를 다시 등록한다.</summary>
    public void Register(IEnumerable<(string Gesture, Action Action)> hotkeys)
    {
        UnregisterAll();
        var failures = new List<string>();
        foreach (var (gesture, action) in hotkeys)
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue;
            }
            if (!TryParse(gesture, out var modifiers, out var key))
            {
                failures.Add(gesture);
                Log.Warn($"단축키 형식 오류: {gesture}");
                continue;
            }

            var id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key)))
            {
                _actions[id] = action;
            }
            else
            {
                failures.Add(gesture);
                Log.Warn($"단축키 등록 실패 (다른 프로그램이 사용 중일 수 있음): {gesture}");
            }
        }
        Failures = failures;
    }

    /// <summary>"Ctrl+Alt+Space" 형식을 해석한다.</summary>
    public static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key) && !(raw.Length == 1 && char.IsDigit(raw[0]) && Enum.TryParse("D" + raw, out key)))
                    {
                        return false;
                    }
                    break;
            }
        }
        // 수정 키 없이 단독 키를 전역으로 잡으면 일반 입력을 방해하므로 막는다.
        return key != Key.None && modifiers != 0;
    }

    /// <summary>키 입력을 "Ctrl+Alt+Space" 형식 문자열로 만든다 (설정 화면 녹화용).</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None || modifiers == ModifierKeys.None)
        {
            return null;
        }
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join('+', parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _actions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
