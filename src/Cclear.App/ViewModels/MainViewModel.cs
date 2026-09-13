using Cclear.App.Services;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cclear.App.ViewModels;

/// <summary>主窗口外壳 VM：持有 6 个页面 VM；页面切换由 MainWindow 的 NavigationView 驱动。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public OverviewViewModel Overview { get; }
    public ScanViewModel SpaceAnalysis { get; }
    public CleanListViewModel CleanList { get; }
    public DuplicatesViewModel Duplicates { get; }
    public DeepCleanViewModel DeepClean { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>体检完成后由 MainWindow 挂接：导航到清理清单页。</summary>
    public Action<CleanPlan>? NavigateToCleanPlan { get; set; }

    public MainViewModel()
        : this(new ShellCleaner(), new DialogService())
    {
    }

    public MainViewModel(ICleaner cleaner, ICleanDialogs dialogs)
    {
        SpaceAnalysis = new ScanViewModel();
        CleanList = new CleanListViewModel(cleaner, dialogs);
        Duplicates = new DuplicatesViewModel(cleaner, dialogs);
        DeepClean = new DeepCleanViewModel();
        Settings = new SettingsViewModel();
        Overview = new OverviewViewModel(plan =>
        {
            CleanList.LoadPlan(plan);
            NavigateToCleanPlan?.Invoke(plan);
        });
    }
}
