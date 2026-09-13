using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Cclear.App.Services;

/// <summary>
/// 界面主题三态切换（浅色 / 深色 / 跟随系统），持久化到 AppSettings.Theme。
/// System 模式挂接 SystemThemeWatcher 跟随系统实时变化。
/// </summary>
public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";

    private static Window? _watchedWindow;

    /// <summary>当前存储的主题（非法值按 System 处理）。</summary>
    public static string StoredTheme =>
        SettingsStore.Instance.Theme is Light or Dark or System ? SettingsStore.Instance.Theme : System;

    /// <summary>启动时应用存储的主题并挂接监听。</summary>
    public static void ApplyStoredTheme(Window window)
    {
        _watchedWindow = window;
        ApplyInternal(StoredTheme);
        if (StoredTheme == System)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica);
        }
    }

    /// <summary>运行时切换主题并持久化（切换后重新挂接/解除系统监听）。</summary>
    public static void SetTheme(string theme)
    {
        if (_watchedWindow is not null)
        {
            SystemThemeWatcher.UnWatch(_watchedWindow);
        }
        ApplyInternal(theme);
        SettingsStore.Instance.Theme = theme;
        SettingsStore.Save();
        if (theme == System && _watchedWindow is not null)
        {
            SystemThemeWatcher.Watch(_watchedWindow, WindowBackdropType.Mica);
        }
    }

    private static void ApplyInternal(string theme)
    {
        var appTheme = theme switch
        {
            Dark => ApplicationTheme.Dark,
            Light => ApplicationTheme.Light,
            _ => ToApplicationTheme(ApplicationThemeManager.GetSystemTheme()),
        };
        ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica);
    }

    private static ApplicationTheme ToApplicationTheme(SystemTheme system) => system switch
    {
        SystemTheme.Dark => ApplicationTheme.Dark,
        SystemTheme.HCBlack or SystemTheme.HCWhite or SystemTheme.HC1 or SystemTheme.HC2
            => ApplicationTheme.HighContrast,
        _ => ApplicationTheme.Light,
    };
}
