using System.Windows;
using System.Windows.Controls;
using Cclear.App.ViewModels;

namespace Cclear.App.Views;

public partial class SpaceAnalysisPage : UserControl
{
    public SpaceAnalysisPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => WireTreemap(e.NewValue as ScanViewModel);
    }

    /// <summary>矩形图钻取/悬停事件接入 VM（DataContext 由 MainWindow 导航时注入）。</summary>
    private void WireTreemap(ScanViewModel? viewModel)
    {
        if (viewModel is null)
        {
            return;
        }
        Treemap.ItemDrillDown += (_, item) => viewModel.DrillDownCommand.Execute(item);
        Treemap.HoverItemChanged += (_, item) => viewModel.OnTreemapHover(item);
    }
}
