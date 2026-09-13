using System.Windows;
using Cclear.App.Services;
using Cclear.Core;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Wpf.Ui.Controls;

namespace Cclear.App.Views.Dialogs;

public sealed record ConfirmRow(bool Checked, string Name, string LevelText, SafetyLevel Level,
    string SizeText, string FileCountText);

public partial class ConfirmCleanWindow : FluentWindow
{
    private readonly bool _containsShellAction;
    private readonly bool _permanentMode;

    public ConfirmCleanWindow(IReadOnlyList<CleanCategory> categories, bool useRecycleBin)
    {
        InitializeComponent();
        _permanentMode = !useRecycleBin;
        var rows = categories.Select(c => new ConfirmRow(
            true,
            c.DisplayName,
            c.Level switch
            {
                SafetyLevel.Safe => "安全",
                SafetyLevel.Caution => "谨慎",
                _ => "手动",
            },
            c.Level,
            ByteSizeFormatter.Format(c.EstimatedBytes),
            c.IsShellAction ? "" : $"（{c.FileCount:N0} 个文件）")).ToList();
        CategoryList.ItemsSource = rows;
        _containsShellAction = categories.Any(c => c.IsShellAction);
        BinWarning.Visibility = _containsShellAction ? Visibility.Visible : Visibility.Collapsed;
        RecycleHint.Text = useRecycleBin
            ? "文件删除将进入回收站，可随时还原。"
            : "⚠ 永久删除模式：文件不进入回收站，无法还原！";
        if (!useRecycleBin)
        {
            RecycleHint.Foreground = (System.Windows.Media.Brush)FindResource("SystemFillColorCriticalBrush");
        }
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_permanentMode)
        {
            var confirmed = await UiServices.ConfirmAsync(
                "二次确认（永久删除）",
                "永久删除模式：以下文件将不进入回收站、无法还原。\n\n确定要永久删除吗？",
                confirmText: "永久删除", cancelText: "取消", danger: true);
            if (!confirmed)
            {
                return;
            }
        }
        if (_containsShellAction)
        {
            var confirmed = await UiServices.ConfirmAsync(
                "二次确认",
                "回收站清空后无法通过回收站还原，确定继续吗？",
                confirmText: "继续", cancelText: "取消", danger: true);
            if (!confirmed)
            {
                return;
            }
        }
        DialogResult = true;
    }
}
