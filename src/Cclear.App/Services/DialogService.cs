using System.Windows;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Cclear.App.Views.Dialogs;

namespace Cclear.App.Services;

/// <summary>模态对话框实现（UI 线程调用）。</summary>
public sealed class DialogService : ICleanDialogs
{
    public bool ConfirmClean(IReadOnlyList<CleanCategory> categories, bool useRecycleBin)
    {
        var window = new ConfirmCleanWindow(categories, useRecycleBin)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        return window.ShowDialog() == true;
    }

    public CleanResult RunWithProgress(Func<IProgress<CleanProgress>, CancellationToken, CleanResult> run)
    {
        var window = new ProgressWindow("正在清理…");
        var cts = new CancellationTokenSource();
        window.CancelRequested += () => cts.Cancel();
        window.Closed += (_, _) => cts.Cancel(); // 窗口真正关闭后兜底取消
        var progress = new Progress<CleanProgress>(window.Update);

        CleanResult? result = null;
        Exception? error = null;
        var task = Task.Run(() =>
        {
            try
            {
                result = run(progress, cts.Token);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        var completion = task.ContinueWith(
            _ => window.MarkCompleted(),
            TaskScheduler.FromCurrentSynchronizationContext());

        window.ShowDialog();
        // ShowDialog 返回时任务必然已完成（窗口未完成前禁止关闭）
        completion.Wait(TimeSpan.FromSeconds(5));
        if (error is not null)
        {
            throw error;
        }
        return result ?? CleanResult.Empty;
    }

    public void ShowResult(CleanResult result, string auditLogPath, long movedBytes)
    {
        var window = new ResultWindow(result, auditLogPath, movedBytes)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };
        window.ShowDialog();
    }
}
