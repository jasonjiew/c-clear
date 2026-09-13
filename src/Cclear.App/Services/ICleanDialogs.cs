using Cclear.Core.Cleaner;
using Cclear.Core.Rules;

namespace Cclear.App.Services;

/// <summary>清理流程的模态对话框（确认/进度/结果）。</summary>
public interface ICleanDialogs
{
    bool ConfirmClean(IReadOnlyList<CleanCategory> categories, bool useRecycleBin);

    /// <summary>展示进度窗口并在后台执行 run；窗口关闭后返回结果（异常向上抛）。</summary>
    CleanResult RunWithProgress(Func<IProgress<CleanProgress>, CancellationToken, CleanResult> run);

    /// <summary>movedBytes：本次移入回收站的字节数（估算），回收站模式下空间并未真正释放。</summary>
    void ShowResult(CleanResult result, string auditLogPath, long movedBytes);
}
