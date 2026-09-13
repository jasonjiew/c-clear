using System.Windows;
using Cclear.App.Services;
using Wpf.Ui.Controls;

namespace Cclear.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        ThemeService.ApplyStoredTheme(window);
        window.Show();
    }
}
