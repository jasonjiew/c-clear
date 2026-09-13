using Cclear.Core.Settings;

namespace Cclear.App.Services;

/// <summary>进程内设置单例（启动加载，保存落盘）。</summary>
public static class SettingsStore
{
    public static AppSettings Instance { get; private set; } = AppSettings.Load();

    public static void Save() => Instance.Save();
}
