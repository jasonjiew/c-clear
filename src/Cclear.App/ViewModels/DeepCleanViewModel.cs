using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.App.Services;
using Cclear.Core;
using Cclear.Core.DeepClean;
using Cclear.Core.Win32;

namespace Cclear.App.ViewModels;

/// <summary>深度清理引导页（V2 F4）：只引导不代删；执行项均有管理员检测与二次确认。</summary>
public sealed partial class DeepCleanViewModel : ObservableObject
{
    public string Title => "深度清理";

    public DeepCleanViewModel()
    {
        IsAdmin = SystemCheck.IsAdministrator();
        RefreshHibernation();
    }

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private string _adminStatusText = "";

    partial void OnIsAdminChanged(bool value) => RefreshAdminText();

    [ObservableProperty]
    private string _hibernationText = "";

    [ObservableProperty]
    private long? _hiberfilBytes;

    [ObservableProperty]
    private bool _isWinSxsBusy;

    [ObservableProperty]
    private string _winSxsResultText = "";

    [ObservableProperty]
    private string _winSxsSizeText = "";

    [ObservableProperty]
    private bool _isCleanupBusy;

    [ObservableProperty]
    private string _cleanupResultText = "";

    [ObservableProperty]
    private bool _isShadowBusy;

    [ObservableProperty]
    private string _shadowResultText = "";

    public bool CanRunDeepClean => IsAdmin && !IsWinSxsBusy && !IsCleanupBusy;

    partial void OnIsWinSxsBusyChanged(bool value) => NotifyAll();
    partial void OnIsCleanupBusyChanged(bool value) => NotifyAll();

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(CanRunDeepClean));
        AnalyzeWinSxsCommand.NotifyCanExecuteChanged();
        CleanupComponentsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>页面每次展示时刷新管理员与休眠状态。</summary>
    public void OnNavigatedTo()
    {
        IsAdmin = SystemCheck.IsAdministrator();
        RefreshHibernation();
        RefreshAdminText();
    }

    private void RefreshAdminText()
    {
        AdminStatusText = IsAdmin
            ? "当前以管理员运行，全部深度清理项可用"
            : "当前非管理员：执行类操作会失败；以管理员身份运行本程序可解锁全部项（右键应用图标 → 以管理员身份运行）";
    }

    private void RefreshHibernation()
    {
        HiberfilBytes = DeepCleanService.GetHiberfilBytes();
        HibernationText = HiberfilBytes is null
            ? "休眠已关闭（未发现 hiberfil.sys）"
            : $"休眠文件 hiberfil.sys 当前占用 {ByteSizeFormatter.Format(HiberfilBytes.Value)}";
    }

    [RelayCommand(CanExecute = nameof(CanRunDeepClean))]
    private async Task AnalyzeWinSxsAsync()
    {
        IsWinSxsBusy = true;
        WinSxsResultText = "正在分析组件存储（约 1–5 分钟，只读不删）…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var result = await DeepCleanService.AnalyzeComponentStoreAsync(cts.Token);
            WinSxsSizeText = result.SizeGb is null ? "" : $"估算占用约 {result.SizeGb:F2} GB";
            WinSxsResultText = result.Success
                ? $"分析完成。{WinSxsSizeText}详细输出见下。"
                : "分析失败：" + (result.Error ?? "未知错误");
            if (result.Output.Length > 0)
            {
                WinSxsResultText += Environment.NewLine + Environment.NewLine + result.Output;
            }
        }
        catch (Exception ex)
        {
            WinSxsResultText = "分析失败：" + ex.Message;
        }
        finally
        {
            IsWinSxsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunDeepClean))]
    private async Task CleanupComponentsAsync()
    {
        var confirmed = await UiServices.ConfirmAsync(
            "Windows 更新备份清理（DISM /StartComponentCleanup）",
            "是什么：清理组件存储中被新版本取代的旧更新文件。\n\n"
            + "代价（请确认）：\n"
            + "· 已安装的更新将无法卸载回滚\n"
            + "· 过程通常 10–30 分钟，期间不可中断、不要关机\n\n"
            + "确定要执行吗？（需要管理员权限，本会话"
            + (IsAdmin ? "已具备）" : "未具备，执行会失败）"),
            confirmText: "开始清理", danger: true);
        if (!confirmed)
        {
            return;
        }
        IsCleanupBusy = true;
        CleanupResultText = "正在清理被取代的更新组件（可能 10–30 分钟，请勿关机）…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
            var result = await DeepCleanService.StartComponentCleanupAsync(cts.Token);
            CleanupResultText = result.Success
                ? "清理完成。组件存储中被取代的更新文件已由系统回收。"
                : "清理失败：" + (result.Error ?? "未知错误");
            if (result.Output.Length > 0)
            {
                CleanupResultText += Environment.NewLine + Environment.NewLine + result.Output;
            }
        }
        catch (Exception ex)
        {
            CleanupResultText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsCleanupBusy = false;
        }
    }

    /// <summary>关闭休眠（需管理员；二次确认在 UI 层）。</summary>
    [RelayCommand]
    private async Task DisableHibernationAsync()
    {
        if (HiberfilBytes is null)
        {
            UiServices.ToastInfo("休眠文件", "休眠已处于关闭状态");
            return;
        }
        var confirmed = await UiServices.ConfirmAsync(
            "关闭休眠功能",
            $"将执行 powercfg /h off，删除 {ByteSizeFormatter.Format(HiberfilBytes.Value)} 的休眠文件。\n\n"
            + "代价：关闭后无法休眠，Windows 快速启动失效；可随时以 powercfg /h on 恢复。\n\n"
            + (IsAdmin ? "确定要关闭吗？" : "当前不是管理员，执行会失败。请以管理员身份运行本程序后再试。"),
            confirmText: "关闭休眠", danger: true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            DeepCleanService.DisableHibernation();
            UiServices.ToastSuccess("休眠文件", "休眠已关闭，空间已释放");
        }
        catch (Exception ex)
        {
            UiServices.ToastWarning("休眠文件", "关闭失败：" + ex.Message);
        }
        RefreshHibernation();
    }

    [RelayCommand]
    private async Task AnalyzeShadowStorageAsync()
    {
        IsShadowBusy = true;
        ShadowResultText = "正在查询系统还原点占用…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (success, output) = await DeepCleanService.ListShadowStorageAsync(cts.Token);
            var used = DeepCleanService.ParseShadowStorageUsedGb(output);
            ShadowResultText = !success
                ? "查询失败（vssadmin 需要管理员权限）。可到系统设置手动查看。"
                : used is null
                    ? "查询完成，未能解析出占用数值。原始输出：\n" + output
                    : $"系统还原与卷影副本已用空间约 {used:F2} GB。\n\n" + output;
        }
        catch (Exception ex)
        {
            ShadowResultText = "查询失败：" + ex.Message;
        }
        finally
        {
            IsShadowBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSystemProtection()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("SystemPropertiesProtection.exe")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            UiServices.ToastWarning("系统还原", "打开系统设置失败：" + ex.Message);
        }
    }
}
