using Cclear.App.Services;
using Cclear.Core.Cleaner;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cclear.App.ViewModels;

/// <summary>主窗口外壳 VM：左侧导航 + 页面切换。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public OverviewViewModel Overview { get; }
    public ScanViewModel SpaceAnalysis { get; }
    public CleanListViewModel CleanList { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<object> Pages { get; }

    [ObservableProperty]
    private object? _currentPage;

    public MainViewModel()
        : this(new ShellCleaner(), new DialogService())
    {
    }

    public MainViewModel(ICleaner cleaner, ICleanDialogs dialogs)
    {
        SpaceAnalysis = new ScanViewModel();
        CleanList = new CleanListViewModel(cleaner, dialogs);
        Settings = new SettingsViewModel();
        Overview = new OverviewViewModel(plan =>
        {
            CleanList.LoadPlan(plan);
            CurrentPage = CleanList;
        });
        Pages = new object[] { Overview, SpaceAnalysis, CleanList, Settings };
        CurrentPage = Overview;
    }
}
