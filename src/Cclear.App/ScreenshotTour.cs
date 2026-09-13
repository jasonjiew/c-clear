using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Cclear.App.Services;
using Cclear.App.ViewModels;
using Cclear.App.Views;
using Cclear.App.Views.Dialogs;
using Wpf.Ui.Controls;

namespace Cclear.App;

/// <summary>
/// 开发工具：--screenshot-tour 输出目录
/// 无人值守遍历全部页面（浅色 + 深色），RenderTargetBitmap 截图保存 PNG。
/// 用于 README 截图与 UI 回归对照；不随发布功能使用。
/// </summary>
public static class ScreenshotTour
{
    public static async Task RunAsync(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var vm = window.DataContext as MainViewModel ?? throw new InvalidOperationException("MainViewModel 未就绪");
        await WaitForWindowAsync(window);
        await Task.Delay(1200); // 等待总览云占位统计等异步内容

        // ---------- 浅色 ----------
        ThemeService.SetTheme(ThemeService.Light, Wpf.Ui.Controls.WindowBackdropType.None);
        await Task.Delay(600);
        await SnapAsync(window, Path.Combine(outputDir, "overview-light.png"));
        await NavigateAsync(window, typeof(SpaceAnalysisPage));
        await SnapAsync(window, Path.Combine(outputDir, "space-analysis-light.png"));
        await NavigateAsync(window, typeof(DuplicatesPage));
        await Task.Delay(400);
        await SnapAsync(window, Path.Combine(outputDir, "duplicates-light.png"));
        await NavigateAsync(window, typeof(DeepCleanPage));
        await Task.Delay(300);
        await SnapAsync(window, Path.Combine(outputDir, "deepclean-light.png"));
        await NavigateAsync(window, typeof(SettingsPage));
        await Task.Delay(300);
        await SnapAsync(window, Path.Combine(outputDir, "settings-light.png"));

        // 真实体检（只读）→ 自动跳转清理清单
        await NavigateAsync(window, typeof(OverviewPage));
        await Task.Delay(300);
        await vm.Overview.HealthCheckCommand.ExecuteAsync(null);
        await Task.Delay(500);
        await SnapAsync(window, Path.Combine(outputDir, "clean-list-light.png"));

        // ---------- 深色 ----------
        ThemeService.SetTheme(ThemeService.Dark, Wpf.Ui.Controls.WindowBackdropType.None);
        await Task.Delay(700);
        await SnapAsync(window, Path.Combine(outputDir, "clean-list-dark.png"));
        await NavigateAsync(window, typeof(OverviewPage));
        await Task.Delay(600);
        await SnapAsync(window, Path.Combine(outputDir, "overview-dark.png"));
        await NavigateAsync(window, typeof(SpaceAnalysisPage));
        await SnapAsync(window, Path.Combine(outputDir, "space-analysis-dark.png"));
        await NavigateAsync(window, typeof(DuplicatesPage));
        await Task.Delay(400);
        await SnapAsync(window, Path.Combine(outputDir, "duplicates-dark.png"));
        await NavigateAsync(window, typeof(DeepCleanPage));
        await Task.Delay(300);
        await SnapAsync(window, Path.Combine(outputDir, "deepclean-dark.png"));
        await NavigateAsync(window, typeof(SettingsPage));
        await Task.Delay(300);

        // Snackbar 提示（叠加在设置页上）
        UiServices.ToastInfo("规则包更新", "发现新版本 v2（本机 v1），可到 设置 → 规则库 更新");
        await Task.Delay(600);
        await SnapAsync(window, Path.Combine(outputDir, "settings-dark.png"));

        // 恢复用户主题
        ThemeService.SetTheme(ThemeService.System);
    }

    private static async Task NavigateAsync(MainWindow window, Type pageType)
    {
        window.NavigateTo(pageType);
        await Task.Delay(350);
    }

    private static async Task WaitForWindowAsync(Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((window.ActualWidth < 100 || !window.IsLoaded) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    private static async Task SnapAsync(Window window, string path)
    {
        await window.Dispatcher.InvokeAsync(() => { });
        await Task.Delay(250);
        await window.Dispatcher.InvokeAsync(() =>
        {
            var width = (int)Math.Ceiling(window.ActualWidth);
            var height = (int)Math.Ceiling(window.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                return;
            }
            var bitmap = new RenderTargetBitmap(
                (int)(width * GetDpiScale()), (int)(height * GetDpiScale()), 96 * GetDpiScale(), 96 * GetDpiScale(), System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        Console.WriteLine("截图已保存: " + path);
    }

    private static double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(Application.Current?.MainWindow);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }
}
