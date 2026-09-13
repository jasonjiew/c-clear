using System.Windows;
using Cclear.App.Services;
using Wpf.Ui.Controls;

namespace Cclear.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        ThemeService.ApplyStoredTheme(window);
        window.Show();
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
