using System.Windows;
using Cclear.App.Services;
using Cclear.App.ViewModels;
using Cclear.App.Views;
using Wpf.Ui.Controls;

namespace Cclear.App;

/// <summary>
/// 主窗口：WPF-UI FluentWindow + NavigationView。
/// 页面由 NavigationView 以 TargetPageType 导航（全部启用缓存）；
/// DataContext 统一在 Navigated 事件中注入（点击导航不携带 dataContext）。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        // 体检完成后跳转清理清单页（原 CurrentPage 驱动改为 Frame 导航）
        _vm.NavigateToCleanPlan = _ => Nav.Navigate(typeof(CleanListPage), _vm.CleanList);
        Nav.Navigated += OnNavNavigated;
        Loaded += OnLoaded;
        UiServices.Configure(SnackbarPresenter);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Nav.Navigate(typeof(OverviewPage), _vm.Overview);
    }

    /// <summary>程序化导航到指定页面（供截图巡览等开发工具使用）。</summary>
    public void NavigateTo(Type pageType)
    {
        object? dataContext = pageType.Name switch
        {
            nameof(OverviewPage) => _vm.Overview,
            nameof(SpaceAnalysisPage) => _vm.SpaceAnalysis,
            nameof(CleanListPage) => _vm.CleanList,
            nameof(DuplicatesPage) => _vm.Duplicates,
            nameof(DeepCleanPage) => _vm.DeepClean,
            nameof(SettingsPage) => _vm.Settings,
            _ => null,
        };
        Nav.Navigate(pageType, dataContext);
    }

    private void OnNavNavigated(NavigationView sender, RoutedEventArgs args)
    {
        if (args is NavigatedEventArgs navigated && navigated.Page is FrameworkElement page)
        {
            page.DataContext = page switch
            {
                OverviewPage => _vm.Overview,
                SpaceAnalysisPage => _vm.SpaceAnalysis,
                CleanListPage => _vm.CleanList,
                DuplicatesPage => _vm.Duplicates,
                DeepCleanPage => _vm.DeepClean,
                SettingsPage => _vm.Settings,
                _ => page.DataContext,
            };
        }
        // 每次回到总览页刷新磁盘信息与清理趋势
        if (args is NavigatedEventArgs { Page: OverviewPage })
        {
            _vm.Overview.RefreshDashboard();
        }
        // 每次进入深度清理页刷新管理员/休眠状态
        if (args is NavigatedEventArgs { Page: DeepCleanPage })
        {
            _vm.DeepClean.OnNavigatedTo();
        }
    }
}
