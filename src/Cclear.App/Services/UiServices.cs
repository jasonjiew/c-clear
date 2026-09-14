using System.Threading.Tasks;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Cclear.App.Services;

/// <summary>
/// 全局 UI 通知：Snackbar 提示 + WPF-UI MessageBox 确认框（替代原生 System.Windows.MessageBox）。
/// </summary>
public static class UiServices
{
    public static SnackbarService Snackbar { get; } = new();

    /// <summary>MainWindow 加载后挂接 Snackbar 呈现器。</summary>
    public static void Configure(SnackbarPresenter presenter)
    {
        Snackbar.SetSnackbarPresenter(presenter);
    }

    public static void ToastSuccess(string title, string message)
    {
        Snackbar.Show(title, message, ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.CheckmarkCircle24), TimeSpan.FromSeconds(4));
    }

    public static void ToastInfo(string title, string message)
    {
        Snackbar.Show(title, message, ControlAppearance.Info,
            new SymbolIcon(SymbolRegular.Info24), TimeSpan.FromSeconds(4));
    }

    public static void ToastWarning(string title, string message)
    {
        Snackbar.Show(title, message, ControlAppearance.Caution,
            new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(6));
    }

    /// <summary>模态确认框：返回 true 表示用户点击了确认按钮。</summary>
    public static async Task<bool> ConfirmAsync(
        string title, string message, string confirmText = "确定", string cancelText = "取消", bool danger = false)
    {
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
        };
        if (danger)
        {
            messageBox.PrimaryButtonAppearance = ControlAppearance.Danger;
        }
        var result = await messageBox.ShowDialogAsync();
        return result == MessageBoxResult.Primary;
    }

    /// <summary>
    /// Pro 引导卡（V3 P9）：仅点击 Pro 徽标/功能入口时展示，绝无主动弹窗。
    /// 信息 = 定价（¥49 买断）+ 开源仓库 + 激活方式；不设联网 DRM。
    /// </summary>
    public static Task ShowProGuide()
    {
        var messageBox = new MessageBox
        {
            Title = "C-Clear Pro（¥49 买断）",
            Content = "Pro 功能（永久买断，无订阅、无广告、无推广弹窗）：\n\n"
                + "· 智能清理建议（一键勾选推荐项）\n"
                + "· 卸载残留扫描（只引导，回收站可还原）\n"
                + "· 趋势预测 + HTML 周报导出\n"
                + "· 自动清理高级触发（登录后 / 空间阈值）\n"
                + "· 下载重复检测（保留最新）\n\n"
                + "免费核心永不缩水：体检、清理、空间矩形图、多盘支持、每周自动清理全部免费。\n\n"
                + "购买与激活：在 GitHub 仓库（github.com/wangjie0721666-web/c-clear）README 获取购买链接；"
                + "收到 license.dat 后到 设置 → 关于 → 导入许可证 即刻解锁（离线验证，无联网 DRM）。",
            PrimaryButtonText = "知道了",
        };
        return messageBox.ShowDialogAsync();
    }
}
