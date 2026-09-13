using System.Windows;
using Cclear.App.ViewModels;

namespace Cclear.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
