using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.App.Services;
using Cclear.Core.Rules;

namespace Cclear.App.ViewModels;

/// <summary>设置页：排除目录 / 永久删除开关 / 规则查看 / 日志位置。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public string Title => "设置";

    public SettingsViewModel()
    {
        _selectedTheme = ThemeService.StoredTheme;
        foreach (var dir in SettingsStore.Instance.ExcludedDirs)
        {
            ExcludedDirs.Add(dir);
        }
        _permanentDelete = SettingsStore.Instance.PermanentDelete;
        foreach (var rule in RulePackLoader.LoadEmbedded())
        {
            Rules.Add(new RuleRow(rule));
        }
    }

    public ObservableCollection<string> ExcludedDirs { get; } = new();

    public ObservableCollection<RuleRow> Rules { get; } = new();

    [ObservableProperty]
    private string _newExcludedDir = "";

    [ObservableProperty]
    private bool _permanentDelete;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>界面主题（System / Light / Dark），切换即生效并持久化。</summary>
    [ObservableProperty]
    private string _selectedTheme;

    public string[] ThemeOptions { get; } = { ThemeService.System, ThemeService.Light, ThemeService.Dark };

    public string AppVersion => "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

    partial void OnSelectedThemeChanged(string value)
    {
        if (value == ThemeService.StoredTheme)
        {
            return;
        }
        ThemeService.SetTheme(value);
        StatusText = value switch
        {
            ThemeService.Light => "已切换到浅色主题",
            ThemeService.Dark => "已切换到深色主题",
            _ => "已切换为跟随系统主题",
        };
    }

    public string LogsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "C-Clear", "logs");

    [RelayCommand]
    private void AddExcludedDir()
    {
        var value = NewExcludedDir.Trim();
        if (value.Length == 0)
        {
            return;
        }
        try
        {
            value = Path.GetFullPath(value);
        }
        catch (Exception)
        {
            StatusText = "路径无效：" + value;
            return;
        }
        if (ExcludedDirs.Contains(value))
        {
            StatusText = "该目录已在列表中";
            return;
        }
        ExcludedDirs.Add(value);
        NewExcludedDir = "";
        Persist();
        StatusText = "已添加排除目录（下次扫描/体检生效）";
        UiServices.ToastSuccess("排除目录", value + " 已加入排除列表");
    }

    [RelayCommand]
    private void RemoveExcludedDir(string? dir)
    {
        if (dir is null || ExcludedDirs.Remove(dir))
        {
            Persist();
            StatusText = "已移除排除目录";
            UiServices.ToastInfo("排除目录", "已移除 " + dir);
        }
    }

    private bool _suppressPermanentDeleteConfirm;

    partial void OnPermanentDeleteChanged(bool value)
    {
        if (_suppressPermanentDeleteConfirm)
        {
            return;
        }
        if (value)
        {
            _ = ConfirmPermanentDeleteAsync();
            return;
        }
        Persist();
        StatusText = "已恢复回收站模式";
    }

    /// <summary>开启永久删除前的危险操作确认（WPF-UI MessageBox，替代原生弹窗）。</summary>
    private async Task ConfirmPermanentDeleteAsync()
    {
        var confirmed = await UiServices.ConfirmAsync(
            "危险操作",
            "开启后“清理”将永久删除文件（不进回收站，无法还原）！\n\n确定要开启永久删除模式吗？",
            confirmText: "开启永久删除", cancelText: "取消", danger: true);
        if (!confirmed)
        {
            _suppressPermanentDeleteConfirm = true;
            PermanentDelete = false;
            _suppressPermanentDeleteConfirm = false;
            StatusText = "已保持回收站模式";
            return;
        }
        Persist();
        StatusText = "已开启永久删除模式（每次清理都会再次确认）";
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(LogsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsFolder}\"") { UseShellExecute = true });
    }

    private void Persist()
    {
        SettingsStore.Instance.ExcludedDirs = ExcludedDirs.ToList();
        SettingsStore.Instance.PermanentDelete = PermanentDelete;
        SettingsStore.Save();
    }
}

public sealed record RuleRow(string Id, string Name, string Level, string Paths, string Explanation)
{
    public RuleRow(CleanupRule rule) : this(
        rule.Id,
        rule.Name,
        rule.Level switch
        {
            SafetyLevel.Safe => "安全",
            SafetyLevel.Caution => "谨慎",
            _ => "手动",
        },
        rule.Paths.Length == 0 ? (rule.Cli is not null ? $"CLI：{rule.Cli.Command} {rule.Cli.Args}" : "(Shell 动作)") : string.Join("\n", rule.Paths),
        rule.Explanation)
    {
    }
}
