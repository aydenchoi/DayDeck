using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using DayDeck.Core.Settings;

namespace DayDeck.App.Services;

/// <summary>
/// 밝은/어두운 테마. 색 토큰(App.xaml의 *Brush)을 통째로 바꿔 끼운다.
/// XAML은 DynamicResource로 토큰을 참조하므로 열려 있는 창도 바로 바뀐다.
/// </summary>
internal static class ThemeService
{
    // 키: (밝은 색, 어두운 색). App.xaml의 기본값은 밝은 색과 같다.
    private static readonly (string Key, uint Light, uint Dark)[] Tokens =
    [
        ("BgBrush", 0xFFF3F3F3, 0xFF202020),
        ("SurfaceBrush", 0xFFFBFBFB, 0xFF272727),
        ("CardBrush", 0xFFFFFFFF, 0xFF2C2C2C),
        ("LineBrush", 0xFFE5E5E5, 0xFF3D3D3D),
        ("RowLineBrush", 0xFFEDEDED, 0xFF353535),
        ("TextBrush", 0xFF1B1B1B, 0xFFF2F2F2),
        ("SubTextBrush", 0xFF5F5F5F, 0xFFC8C8C8),
        ("MutedBrush", 0xFF9A9A9A, 0xFF8C8C8C),
        ("AccentBrush", 0xFF0067C0, 0xFF3A8EE6),
        ("NavHoverBrush", 0xFFEDEDED, 0xFF2F2F2F),
        ("NavSelectedBrush", 0xFFE6E6E6, 0xFF383838),
        ("SuccessBrush", 0xFF0F7B0F, 0xFF6CCB5F),
        ("BorderStrongBrush", 0xFFB8B8B8, 0xFF5C5C5C),
        ("DangerBrush", 0xFFC42B1C, 0xFFE5534B),
        ("DangerSoftBrush", 0xFFFDE7E9, 0xFF45282A),
        ("WarnBrush", 0xFFC75B00, 0xFFE8913A),
        ("WarnTextBrush", 0xFF7A3A00, 0xFFFFB870),
        ("InfoBrush", 0xFF2B6CD4, 0xFF6AA8F0),
        ("NoticeBrush", 0xFFFFF4CE, 0xFF4A4020),
        ("ChipBrush", 0xFFF0F0F0, 0xFF3A3A3A),
        ("InputBrush", 0xFFF3F3F3, 0xFF333333),
        ("SubtleBrush", 0xFFF7F7F7, 0xFF2A2A2A),
        ("SelectionBrush", 0x140067C0, 0x333A8EE6),
        ("ToastBrush", 0xFF2B2B2B, 0xFF454545),
        ("PopupBrush", 0xFFF9F9F9, 0xFF262626),
        ("PopupHeaderBrush", 0xFFF0F0F0, 0xFF303030),
        ("TodayBrush", 0xFFE6F0FA, 0xFF1E3550),
        ("DoneBgBrush", 0xFFF4F4F4, 0xFF333333),
        ("DoneBorderBrush", 0xFFBDBDBD, 0xFF5A5A5A),
        ("HatchLightBrush", 0xFFFFF3E6, 0xFF3D2E1E),
        ("HatchDarkBrush", 0xFFFFE2C2, 0xFF4D3820),
        ("TrackBrush", 0xFFE3E3E3, 0xFF3A3A3A),
        ("ControlBrush", 0xFFFDFDFD, 0xFF373737),
        ("ControlHoverBrush", 0xFFF0F0F0, 0xFF424242),
        ("ControlPressedBrush", 0xFFE5E5E5, 0xFF2F2F2F),
        ("ControlBorderBrush", 0xFFD0D0D0, 0xFF4A4A4A),
    ];

    /// <summary>지금 어두운 테마가 적용되어 있는가.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>테마가 바뀌었다. 코드로 색을 계산해 그리는 화면(달력)은 다시 그린다.</summary>
    public static event EventHandler? Changed;

    public static bool ResolveDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemPrefersDark(),
    };

    private static bool s_applied;

    /// <summary>테마를 적용한다. 밝기/어둠이 그대로면 아무것도 하지 않는다.</summary>
    public static void Apply(Application app, AppTheme theme)
    {
        var dark = ResolveDark(theme);
        if (s_applied && dark == IsDark)
        {
            return;
        }
        var changed = s_applied;
        s_applied = true;
        IsDark = dark;
        foreach (var (key, light, darkColor) in Tokens)
        {
            var brush = new SolidColorBrush(ToColor(dark ? darkColor : light));
            brush.Freeze();
            app.Resources[key] = brush;
        }
        var overdue = new LinearGradientBrush(ToColor(dark ? 0xFF45282Au : 0xFFFDE7E9u), ToColor(dark ? 0xFF2C2C2Cu : 0xFFFFFFFFu), new Point(0, 0), new Point(1, 0));
        overdue.GradientStops[1].Offset = 0.45;
        overdue.Freeze();
        app.Resources["OverdueRowBrush"] = overdue;
        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
        }
        if (changed)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Color Color(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    /// <summary>창 제목 표시줄도 테마에 맞춘다 (Windows 11의 DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        var value = IsDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    /// <summary>
    /// 모든 창에 테마를 입힌다: 제목 표시줄 색, 그리고 따로 정하지 않은 글자색(기본은 시스템 검정)을 토큰에 묶는다.
    /// </summary>
    public static void RegisterWindowHook()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            var window = (Window)sender;
            if (window.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
            {
                window.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            }
            ApplyTitleBar(window);
        }));
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Color ToColor(uint argb) =>
        System.Windows.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private const int DwmUseImmersiveDarkMode = 20;
}
