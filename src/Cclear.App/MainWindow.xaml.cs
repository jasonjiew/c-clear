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
                SettingsPage => _vm.Settings,
                _ => page.DataContext,
            };
        }
    }
}
