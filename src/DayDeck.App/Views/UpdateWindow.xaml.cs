using System.Diagnostics;
using System.Globalization;
using System.Windows;
using DayDeck.App.Services;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Updates;

namespace DayDeck.App.Views;

/// <summary>새 버전 알림. 설치는 사용자가 "지금 업데이트"를 눌렀을 때만 한다.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly ReleaseInfo _release;
    private bool _installing;

    internal UpdateWindow(UpdateService updates, ReleaseInfo release)
    {
        InitializeComponent();
        FontService.ScaleWindow(this);
        _updates = updates;
        _release = release;

        var date = release.PublishedAt is { } at ? " · " + at.ToLocalTime().ToString("yyyy년 M월 d일", CultureInfo.GetCultureInfo("ko-KR")) : "";
        VersionText.Text = $"{release.Title} (지금 {UpdateService.CurrentVersion.ToString(3)}){date}";
        Notes.Document = MarkdownRenderer.Render(release.Notes.Length > 0 ? release.Notes : "바뀐 점 설명이 없습니다.", toggleTask: null, compact: true);

        if (!UpdateService.CanInstall)
        {
            UpdateButton.Content = "내려받기 페이지 열기";
            InfoText.Text = "지금 실행 방식(개발 빌드 등)에서는 자동으로 바꿀 수 없습니다. 릴리스 페이지에서 내려받아 exe를 바꿔 주세요.";
        }
        Closing += (_, e) => e.Cancel = _installing; // 설치 중에는 닫지 않는다.
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.CanInstall)
        {
            OpenReleasePage();
            Close();
            return;
        }
        _installing = true;
        UpdateButton.IsEnabled = SkipButton.IsEnabled = LaterButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.ClearValue(ForegroundProperty);
        var progress = new Progress<double>(value =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = value;
        });
        try
        {
            await _updates.InstallAsync(_release, progress, text =>
            {
                StatusText.Text = text;
                Progress.IsIndeterminate = text != "내려받는 중…";
            });
            // 성공하면 앱이 끝나고 새 버전이 시작된다.
        }
        catch (UpdateException ex)
        {
            Log.Warn("업데이트 실패", ex);
            _installing = false;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            StatusText.Text = "⚠ " + ex.Message;
            StatusText.Foreground = ThemeService.Brush("DangerBrush");
            UpdateButton.Content = "다시 시도";
            UpdateButton.IsEnabled = SkipButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _updates.Skip(_release);
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_release.PageUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("릴리스 페이지를 열 수 없음", ex);
        }
    }
}
