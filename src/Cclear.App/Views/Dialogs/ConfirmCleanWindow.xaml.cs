using System.Windows;
using Cclear.Core;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;

namespace Cclear.App.Views.Dialogs;

public sealed record ConfirmRow(bool Checked, string Name, string LevelText, string LevelBrush,
    string SizeText, string FileCountText);

public partial class ConfirmCleanWindow : Window
{
    private readonly bool _containsShellAction;

    public ConfirmCleanWindow(IReadOnlyList<CleanCategory> categories, bool useRecycleBin)
    {
        InitializeComponent();
        var rows = categories.Select(c => new ConfirmRow(
            true,
            c.DisplayName,
            c.Level switch
            {
                SafetyLevel.Safe => "安全",
                SafetyLevel.Caution => "谨慎",
                _ => "手动",
            },
            c.Level switch
            {
                SafetyLevel.Safe => "#2E7D32",
                SafetyLevel.Caution => "#C62828",
                _ => "#6D4C41",
            },
            ByteSizeFormatter.Format(c.EstimatedBytes),
            c.IsShellAction ? "" : $"（{c.FileCount:N0} 个文件）")).ToList();
        CategoryList.ItemsSource = rows;
        _containsShellAction = categories.Any(c => c.IsShellAction);
        BinWarning.Visibility = _containsShellAction ? Visibility.Visible : Visibility.Collapsed;
        RecycleHint.Text = useRecycleBin
            ? "文件删除将进入回收站，可随时还原。"
            : "永久删除模式：文件不会进入回收站！";
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_containsShellAction)
        {
            var answer = MessageBox.Show(this,
                "回收站清空后无法通过回收站还原，确定继续吗？",
                "二次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }
        DialogResult = true;
    }
}
