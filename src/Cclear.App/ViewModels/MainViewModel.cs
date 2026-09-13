using CommunityToolkit.Mvvm.ComponentModel;

namespace Cclear.App.ViewModels;

/// <summary>主窗口外壳 VM：左侧导航 + 页面切换。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public OverviewViewModel Overview { get; }
    public ScanViewModel SpaceAnalysis { get; }
    public CleanListViewModel CleanList { get; }

    public IReadOnlyList<object> Pages { get; }

    [ObservableProperty]
    private object? _currentPage;

    public MainViewModel()
    {
        SpaceAnalysis = new ScanViewModel();
        CleanList = new CleanListViewModel();
        Overview = new OverviewViewModel(plan =>
        {
            CleanList.LoadPlan(plan);
            CurrentPage = CleanList;
        });
        Pages = new object[] { Overview, SpaceAnalysis, CleanList };
        CurrentPage = Overview;
    }
}
