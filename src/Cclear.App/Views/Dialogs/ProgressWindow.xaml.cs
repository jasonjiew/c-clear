using System.ComponentModel;
using System.Windows;
using Cclear.Core;
using Cclear.Core.Cleaner;
using Wpf.Ui.Controls;

namespace Cclear.App.Views.Dialogs;

/// <summary>清理进度窗口：完成时由外部 Close；未完成时关闭=请求取消。</summary>
public partial class ProgressWindow : FluentWindow
{
    private readonly TaskCompletionSource<bool> _completedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _cancelled;

    public ProgressWindow(string title)
    {
        InitializeComponent();
        Title = title;
        CurrentFileText.Text = "准备中…";
        Bar.IsIndeterminate = true;
    }

    /// <summary>用户点击“取消”时触发（由宿主接入 CancellationTokenSource）。</summary>
    public event Action? CancelRequested;

    /// <summary>任务完成后续关窗口；返回的 Task 在窗口可以关闭时完成。</summary>
    public Task WhenCompleted => _completedTcs.Task;

    public void MarkCompleted()
    {
        _completedTcs.TrySetResult(true);
        Close(); // 完成后自动关闭（本方法在 UI 线程的 continuation 中调用）
    }

    public void Update(CleanProgress progress)
    {
        if (progress.FilesTotal > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Min(100.0, progress.FilesDone * 100.0 / progress.FilesTotal);
        }
        CurrentFileText.Text = string.IsNullOrEmpty(progress.CurrentFile) ? "处理中…" : progress.CurrentFile;
        CountText.Text = $"{progress.FilesDone:N0} / {progress.FilesTotal:N0} 项 · 已处理 {ByteSizeFormatter.Format(progress.BytesDone)}"
            + (_cancelled ? "（取消中，等待当前批次结束…）" : "");
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancelled = true;
        CountText.Text = "正在取消（等待当前批次结束…）";
        CancelRequested?.Invoke();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 任务未完成时禁止关闭窗口（只能通过“取消”触发协作式停止）
        if (!_completedTcs.Task.IsCompleted)
        {
            _cancelled = true;
            e.Cancel = true;
        }
    }
}
