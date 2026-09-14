using System;
using System.Linq;
using System.Windows;
using Cclear.App.Services;
using Wpf.Ui.Controls;

namespace Cclear.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => string.Equals(a, "--sample-space", StringComparison.OrdinalIgnoreCase)))
        {
            // 每日空间采样（V3 P6 计划任务无头模式）：追加一行快照即退出
            var driveRoot = Cclear.Core.Win32.DriveCatalog.TryGetRoot(Services.SettingsStore.Instance.SelectedDrive) ?? @"C:\";
            try
            {
                Cclear.Core.Trend.SpaceSnapshotStore.AppendForDrive(driveRoot);
                Shutdown(0);
            }
            catch (Exception)
            {
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Any(a => string.Equals(a, "--autoclean", StringComparison.OrdinalIgnoreCase)))
        {
            // 计划任务无头模式（F3）：仅 Safe 规则、回收站模式、写审计与历史，跑完即退出
            var exitCode = await Cclear.Core.AutoClean.AutoCleanRunner.RunAsync();
            Shutdown(exitCode);
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        ThemeService.ApplyStoredTheme(window);
        window.Show();

        var tourIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--screenshot-tour", StringComparison.OrdinalIgnoreCase));
        if (tourIndex >= 0 && e.Args.Length > tourIndex + 1)
        {
            // 开发工具：无人值守页面截图（README 文档与 UI 回归用）
            await ScreenshotTour.RunAsync(window, e.Args[tourIndex + 1]);
            Shutdown();
            return;
        }
        _ = CheckRulesUpdateOnStartupAsync();
    }

    /// <summary>启动时异步检查在线规则包（F1）：失败/无网/校验不过一律静默使用内置包。</summary>
    private static async System.Threading.Tasks.Task CheckRulesUpdateOnStartupAsync()
    {
        if (!Services.SettingsStore.Instance.CheckRulesUpdateOnStartup)
        {
            return;
        }
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var check = await Cclear.Core.Rules.RulesUpdater.CheckLatestAsync(cts.Token);
            if (check.UpdateAvailable)
            {
                UiServices.ToastInfo("规则包更新",
                    $"发现新版本 v{check.OnlinePackVersion}（本机 v{check.InstalledPackVersion}），可到 设置 → 规则库 更新");
            }
        }
        catch (Exception)
        {
            // 静默：无网络或源不可达
        }
    }
}
