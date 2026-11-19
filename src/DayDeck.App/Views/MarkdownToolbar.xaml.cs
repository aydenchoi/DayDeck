using System.Windows;
using System.Windows.Controls;
using DayDeck.App.ViewModels;
using DayDeck.Core.Notes;

namespace DayDeck.App.Views;

/// <summary>
/// 작은 입력란(할 일 메모 등)에 붙이는 서식 도구 모음. <see cref="Target"/> 입력란에 마크다운 서식을 넣고,
/// 입력란에서 서식 단축키(Ctrl+B 등)도 받게 한다. "미리보기"를 누르면 <see cref="PreviewChanged"/>로 알린다.
/// <see cref="RichTarget"/>(보기 편집기)을 주면 서식은 보기 편집기에 적용하고, 오른쪽 단추는 "원문"(마크다운 입력란으로 바꾸기)이 된다.
/// </summary>
public partial class MarkdownToolbar : UserControl
{
    private TextBox? _target;
    private MarkdownRichEditor? _richTarget;

    public MarkdownToolbar()
    {
        InitializeComponent();
    }

    /// <summary>미리보기를 켜거나 껐다 (true = 켬).</summary>
    public event EventHandler<bool>? PreviewChanged;

    public bool IsPreviewing => PreviewToggle.IsChecked == true;

    public TextBox? Target
    {
        get => _target;
        set
        {
            if (_target is not null)
            {
                _target.PreviewKeyDown -= OnTargetKeyDown;
            }
            _target = value;
            if (_target is not null)
            {
                _target.Tag = MarkdownCommands.Tag;
                _target.PreviewKeyDown += OnTargetKeyDown;
            }
        }
    }

    /// <summary>보기 편집기. 있으면 기본으로 여기에 서식을 넣고, 토글을 켜면(원문) <see cref="Target"/> 입력란에 넣는다.</summary>
    internal MarkdownRichEditor? RichTarget
    {
        get => _richTarget;
        set
        {
            _richTarget = value;
            PreviewToggle.Content = value is null ? "미리보기" : "원문";
            PreviewToggle.ToolTip = value is null ? "서식이 적용된 모양 보기 (링크 열기, 체크 상자 체크)" : "마크다운 원문으로 고치기 (다시 누르면 보기)";
            System.Windows.Automation.AutomationProperties.SetName(PreviewToggle, value is null ? "메모 미리보기" : "메모 원문");
        }
    }

    /// <summary>보기 편집기를 쓰는 중에 원문(입력란)으로 바꿔 둔 상태인지.</summary>
    public bool IsShowingRaw => _richTarget is not null && PreviewToggle.IsChecked == true;

    /// <summary>미리보기(보기 편집기를 쓰면 원문)를 끈다 (다른 할 일로 바뀔 때 등).</summary>
    public void ShowEditor() => PreviewToggle.IsChecked = false;

    private void OnTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_target is not null)
        {
            MarkdownCommands.HandleShortcut(_target, e);
        }
    }

    private void Run(Action<IMarkdownTarget> action, Action<MarkdownRichEditor> rich)
    {
        if (_richTarget is { } editor && !IsShowingRaw)
        {
            rich(editor);
            return;
        }
        if (_target is null)
        {
            return;
        }
        if (_richTarget is null)
        {
            // 미리보기 중에 서식을 누르면 입력란으로 돌아간다.
            PreviewToggle.IsChecked = false;
        }
        action(_target.AsTarget());
    }

    private void OnPreviewChanged(object sender, RoutedEventArgs e) => PreviewChanged?.Invoke(this, IsPreviewing);

    private void OnBold(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Bold, r => r.ToggleBold());

    private void OnItalic(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Italic, r => r.ToggleItalic());

    private void OnUnderline(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Underline, r => r.ToggleUnderline());

    private void OnStrike(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Strike, r => r.ToggleStrike());

    private void OnHighlight(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Highlight, r => r.ToggleHighlight());

    private void OnColor(object sender, RoutedEventArgs e) =>
        Run(editor => MarkdownCommands.ShowColorMenu(editor, ColorButton, PickedColor),
            r => MarkdownCommands.ShowColorMenu(ColorButton, r.SetColor, PickedColor));

    private void PickedColor(string hex) => ColorSwatch.Background = TaskRowViewModel.TryParseBrush(hex);

    private void OnLink(object sender, RoutedEventArgs e) => Run(MarkdownCommands.Link, MarkdownCommands.RichLink);

    private void OnBullet(object sender, RoutedEventArgs e) =>
        Run(editor => MarkdownCommands.Line(editor, LineStyle.Bullet), r => r.ToggleLineKind(LineKind.Bullet));

    private void OnNumbered(object sender, RoutedEventArgs e) =>
        Run(editor => MarkdownCommands.Line(editor, LineStyle.Numbered), r => r.ToggleLineKind(LineKind.Numbered));

    private void OnTask(object sender, RoutedEventArgs e) =>
        Run(editor => MarkdownCommands.Line(editor, LineStyle.Task), r => r.ToggleLineKind(LineKind.Task));
}
