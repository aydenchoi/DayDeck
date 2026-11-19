using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using DayDeck.Core.Settings;

namespace DayDeck.App.Services;

/// <summary>
/// 글꼴 설정. 화면 글꼴은 App.xaml의 <c>UiFont</c> 리소스를 바꿔 끼워 열린 창에도 바로 적용하고,
/// 고른 글꼴에 한글이 없을 때를 대비해 맑은 고딕을 뒤에 붙인다.
/// </summary>
internal static class FontService
{
    public const string DefaultUiFont = "Segoe UI Variable Text, Segoe UI, Malgun Gothic";
    public const string DefaultEditorFont = "Cascadia Mono, Consolas, D2Coding, Malgun Gothic";
    public const int BaseFontSize = 14;

    public static void Apply(Application app, UiSettings ui)
    {
        var family = WithFallback(ui.FontFamily, DefaultUiFont);
        if (app.Resources["UiFont"] is FontFamily current && current.Source == family)
        {
            return;
        }
        app.Resources["UiFont"] = new FontFamily(family);
    }

    public static FontFamily EditorFont(NotesSettings notes) => new(WithFallback(notes.EditorFontFamily, DefaultEditorFont));

    /// <summary>창 확대 비율. 화면은 14pt 기준으로 만들었으므로 글자 크기 16pt면 약 1.14.</summary>
    public static double Scale(UiSettings ui) => Math.Clamp(ui.FontSize, 10, 24) / (double)BaseFontSize;

    // 창마다 지금 적용된 비율 (글자 크기를 바꾸면 그 차이만큼 다시 키운다).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Applied = new();

    /// <summary>
    /// 창을 글자 크기에 맞춘다: 내용을 비율만큼 확대하고, 내용에 맞춰 크기가 정해지지 않는 방향은 창 크기도 키운다.
    /// 작업 영역(작업 표시줄 제외 화면)보다 커지지 않게 한다. 각 창의 생성자에서 한 번, 설정이 바뀌면 다시 부른다.
    /// <paramref name="resize"/>가 false면 창 크기는 두고 내용만 키운다 (사용자가 크기를 정한 스티커 등).
    /// </summary>
    public static void ScaleWindow(Window window, bool resize = true)
    {
        var scale = Scale(((App)Application.Current).Settings.Current.Ui);
        var previous = Applied.TryGetValue(window, out var value) ? (double)value : 1.0;
        Applied.AddOrUpdate(window, scale);
        if (window.Content is FrameworkElement content)
        {
            content.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        }
        var ratio = scale / previous;
        if (!resize || Math.Abs(ratio - 1) < 0.01)
        {
            return;
        }
        // 사용자가 크기를 정하는 창(메인 창)은 글자 크기를 바꿔도 창 크기는 그대로 둔다 (내용만 커지고 작아짐).
        // 비율로 키웠다 줄이면 화면 크기 제한 때문에 원래 크기로 돌아오지 않는다.
        var isRescale = value is not null; // 이미 한 번 맞춘 창 (설정에서 글자 크기를 바꾼 경우)
        if (isRescale && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var sizeToWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        var sizeToHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        window.MinWidth = Math.Min(window.MinWidth * ratio, area.Width * 0.9);
        window.MinHeight = Math.Min(window.MinHeight * ratio, area.Height * 0.9);
        if (!sizeToWidth && !double.IsNaN(window.Width))
        {
            window.Width = Math.Clamp(window.Width * ratio, window.MinWidth, area.Width);
        }
        if (!sizeToHeight && !double.IsNaN(window.Height))
        {
            window.Height = Math.Clamp(window.Height * ratio, window.MinHeight, area.Height);
        }
        if (window.MaxHeight is > 0 and < double.PositiveInfinity)
        {
            window.MaxHeight = Math.Min(window.MaxHeight * ratio, area.Height);
        }
        // 이미 떠 있는 창이 화면 밖으로 나가지 않게 한다.
        if (window.IsLoaded)
        {
            window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth * ratio));
            window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.ActualHeight * ratio));
        }
    }

    /// <summary>글자 크기 설정이 바뀌었다: 열려 있는 창을 모두 다시 맞춘다.</summary>
    public static void RescaleOpenWindows(Application app)
    {
        foreach (Window window in app.Windows)
        {
            if (Applied.TryGetValue(window, out _))
            {
                ScaleWindow(window, resize: window is not Views.StickyNoteWindow);
            }
        }
    }

    /// <summary>설치된 글꼴 이름 (한국어 이름이 있으면 그 이름, 가나다순). 설정 화면에서만 읽는다.</summary>
    public static IReadOnlyList<string> InstalledFonts()
    {
        var korean = XmlLanguage.GetLanguage("ko-kr");
        var english = XmlLanguage.GetLanguage("en-us");
        return [.. Fonts.SystemFontFamilies
            .Select(f => f.FamilyNames.TryGetValue(korean, out var ko) ? ko
                : f.FamilyNames.TryGetValue(english, out var en) ? en
                : f.Source)
            .Where(n => !n.StartsWith('@'))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true))];
    }

    private static string WithFallback(string? chosen, string fallback) =>
        string.IsNullOrWhiteSpace(chosen) ? fallback : $"{chosen.Trim()}, Malgun Gothic";
}
