using System.Windows;
using System.Windows.Controls;
using Cclear.App.Views.Settings;
using Wpf.Ui.Controls;

namespace Cclear.App.Views;

/// <summary>设置页：NavigationView 六个子分组；子视图共享 SettingsViewModel（Navigated 时注入）。</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        SectionNav.Navigated += OnSectionNavigated;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        SectionNav.Navigate(typeof(SettingsGeneralView), DataContext);
    }

    private void OnSectionNavigated(NavigationView sender, RoutedEventArgs args)
    {
        if (args is NavigatedEventArgs navigated && navigated.Page is System.Windows.FrameworkElement page)
        {
            page.DataContext = DataContext;
        }
    }
}
