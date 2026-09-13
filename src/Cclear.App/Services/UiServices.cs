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
}
