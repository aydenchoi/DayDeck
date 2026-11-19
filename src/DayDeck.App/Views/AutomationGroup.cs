using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DayDeck.App.Views;

/// <summary>
/// 내용을 감싸서 UI 자동화/화면 읽기 프로그램에 이름 있는 "그룹"으로 노출한다.
/// Border나 Panel은 자동화 트리에 나타나지 않아 카드 단위로 찾을 수 없기 때문에 쓴다.
/// </summary>
public sealed class AutomationGroup : ContentControl
{
    static AutomationGroup()
    {
        FocusableProperty.OverrideMetadata(typeof(AutomationGroup), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private sealed class GroupPeer(AutomationGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(AutomationGroup);
    }
}
