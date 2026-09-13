using System.Windows;
using Cclear.Core;
using Cclear.Core.Cleaner;

namespace Cclear.App.Views.Dialogs;

public partial class ResultWindow : Window
{
    public ResultWindow(CleanResult result, string auditLogPath, long movedBytes)
    {
        InitializeComponent();
        FreedText.Text = result.FreedBytes > 0
            ? $"实际释放 {ByteSizeFormatter.Format(result.FreedBytes)}"
            : $"已移入回收站 {ByteSizeFormatter.Format(movedBytes)}（清空回收站后空间才真正释放）";
        DetailText.Text = $"成功删除 {result.DeletedFiles:N0} 项，跳过 {result.SkippedFiles:N0} 项，用时 {result.Elapsed:hh\\:mm\\:ss}。"
            + "（释放量按磁盘空闲空间变化统计）";
        if (result.SkippedPaths.Count > 0)
        {
            SkippedHeader.Visibility = Visibility.Visible;
            SkippedList.Visibility = Visibility.Visible;
            SkippedList.ItemsSource = result.SkippedPaths;
        }
        LogPathText.Text = "审计日志：" + auditLogPath;
    }
}
