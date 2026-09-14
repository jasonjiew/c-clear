using System.Windows.Controls;

namespace Cclear.App.Views;

public partial class DuplicatesPage : UserControl
{
    public DuplicatesPage()
    {
        InitializeComponent();
    }
    /// <summary>点击 Pro 徽标：展示 Pro 引导卡（仅点击时展示，绝无主动弹窗）。</summary>
    private void OnProBadgeClick(object sender, System.Windows.RoutedEventArgs e)
    {
        _ = Cclear.App.Services.UiServices.ShowProGuide();
    }
}
