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
        _checkRulesUpdateOnStartup = SettingsStore.Instance.CheckRulesUpdateOnStartup;
        _rulesPackStatusText = BuildRulesPackStatusText();
        foreach (var rule in RulePackLoader.LoadEmbedded())
        {
            Rules.Add(new RuleRow(rule));
        }
        LoadHistory();
        LoadAutoClean();
    }

    private static string BuildRulesPackStatusText()
    {
        try
        {
            var version = Cclear.Core.Rules.RulesUpdater.GetInstalledPackVersion();
            return version > 0
                ? $"已安装在线规则包 v{version}"
                : "未安装在线规则包（使用内置规则）";
        }
        catch (Exception)
        {
            return "在线规则包状态未知（将使用内置规则）";
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

    /// <summary>最近 30 天每日清理字节数（F2 清理历史）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<double> HistoryTrendValues { get; } =
        new(System.Linq.Enumerable.Repeat(0.0, 30));

    [ObservableProperty]
    private string _historySummaryText = "暂无清理记录";

    /// <summary>读取清理历史并聚合 30 天趋势。</summary>
    public void LoadHistory()
    {
        try
        {
            var entries = Cclear.Core.History.CleanHistoryStore.Read();
            var trend = Cclear.Core.History.CleanHistoryStore.BuildDailyTrend(
                entries, 30, DateOnly.FromDateTime(DateTime.Now));
            HistoryTrendValues.Clear();
            foreach (var point in trend)
            {
                HistoryTrendValues.Add(point.Bytes);
            }
            var total = trend.Sum(p => p.Bytes);
            var activeDays = trend.Count(p => p.Bytes > 0);
            HistorySummaryText = activeDays == 0
                ? "暂无清理记录"
                : $"近 30 天清理 {Cclear.Core.ByteSizeFormatter.Format(total)}，累计 {entries.Count} 条规则记录";
        }
        catch (Exception)
        {
            HistorySummaryText = "读取清理历史失败";
        }
    }

    [RelayCommand]
    private void RefreshHistory() => LoadHistory();

    // ---------- 在线规则包（F1） ----------

    [ObservableProperty]
    private string _rulesPackStatusText = "";

    [ObservableProperty]
    private bool _checkRulesUpdateOnStartup;

    /// <summary>在线规则包状态：安装路径、版本、来源。</summary>
    public string RulesPackPath => Cclear.Core.Rules.RulesUpdater.InstalledPackPath;

    partial void OnCheckRulesUpdateOnStartupChanged(bool value)
    {
        SettingsStore.Instance.CheckRulesUpdateOnStartup = value;
        SettingsStore.Save();
        StatusText = value ? "已开启启动时自动检查规则包更新" : "已关闭启动时自动检查（仍可手动检查）";
    }

    /// <summary>手动检查并安装在线规则包更新。</summary>
    [RelayCommand]
    private async Task CheckRulesUpdateAsync()
    {
        RulesPackStatusText = "正在检查更新…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var check = await Cclear.Core.Rules.RulesUpdater.CheckLatestAsync(cts.Token);
            if (check.Error is not null)
            {
                RulesPackStatusText = "在线源没有规则包资产（继续使用当前规则）";
                return;
            }
            if (!check.UpdateAvailable)
            {
                RulesPackStatusText = $"已是最新（在线 v{check.OnlinePackVersion}，本机 v{check.InstalledPackVersion}）";
                return;
            }
            var confirmed = await UiServices.ConfirmAsync(
                "规则包更新",
                $"发现新规则包 v{check.OnlinePackVersion}（本机 v{check.InstalledPackVersion}）。\n\n"
                + "规则包经 SHA256 校验后安装；每条规则仍受硬编码黑名单约束，用户文档目录永不会被清理。\n\n现在下载并安装吗？",
                confirmText: "下载并安装");
            if (!confirmed)
            {
                RulesPackStatusText = "已取消更新";
                return;
            }
            var pack = await Cclear.Core.Rules.RulesUpdater.DownloadAndInstallAsync(
                check.AssetUrl!, check.OnlinePackVersion, CancellationToken.None);
            RulesPackStatusText = $"已更新到 v{pack.PackVersion}（{pack.Rules.Count} 条规则；下次体检生效）";
            UiServices.ToastSuccess("规则包", $"已更新到 v{pack.PackVersion}");
            ReloadRules(pack.Rules);
        }
        catch (Exception)
        {
            RulesPackStatusText = "检查/下载失败：无网络或源不可达（继续使用当前规则，不影响体检与清理）";
        }
    }

    private void ReloadRules(System.Collections.Generic.IReadOnlyList<CleanupRule> rules)
    {
        Rules.Clear();
        foreach (var rule in rules)
        {
            Rules.Add(new RuleRow(rule));
        }
    }

    // ---------- 计划任务自动清理（F3） ----------

    public string[] WeekDayOptions { get; } = { "周六", "周日", "周一", "周二", "周三", "周四", "周五" };

    public int[] HourOptions { get; } = { 2, 6, 8, 10, 12, 14, 18, 21, 23 };

    [ObservableProperty]
    private int _selectedWeekDayIndex;

    [ObservableProperty]
    private int _selectedHour;

    [ObservableProperty]
    private bool _autoCleanEnabled;

    [ObservableProperty]
    private string _autoCleanStatusText = "";

    [ObservableProperty]
    private string _autoCleanLastRunText = "";

    partial void OnAutoCleanEnabledChanged(bool value)
    {
        if (_suppressAutoCleanToggle)
        {
            return;
        }
        try
        {
            if (value)
            {
                ApplyScheduleFromSelection();
                Cclear.Core.AutoClean.AutoCleanScheduler.Register(
                    MapWeekDay(SelectedWeekDayIndex), SelectedHour, 0, ExecutablePath);
                AutoCleanStatusText = $"已注册：每{WeekDayOptions[SelectedWeekDayIndex]} {SelectedHour:00}:00 自动清理（仅 Safe 规则，进回收站）";
                UiServices.ToastSuccess("自动清理", "计划任务已注册");
            }
            else
            {
                Cclear.Core.AutoClean.AutoCleanScheduler.Unregister();
                AutoCleanStatusText = "已取消自动清理";
                UiServices.ToastInfo("自动清理", "计划任务已取消");
            }
        }
        catch (Exception ex)
        {
            AutoCleanStatusText = "操作失败：" + ex.Message;
            _suppressAutoCleanToggle = true;
            AutoCleanEnabled = !value;
            _suppressAutoCleanToggle = false;
        }
        RefreshAutoCleanLastRun();
    }

    partial void OnSelectedWeekDayIndexChanged(int value) => OnScheduleChanged();

    partial void OnSelectedHourChanged(int value) => OnScheduleChanged();

    private bool _suppressAutoCleanToggle;

    private void OnScheduleChanged()
    {
        if (_suppressAutoCleanToggle || !AutoCleanEnabled)
        {
            return;
        }
        try
        {
            ApplyScheduleFromSelection();
            Cclear.Core.AutoClean.AutoCleanScheduler.Register(
                MapWeekDay(SelectedWeekDayIndex), SelectedHour, 0, ExecutablePath);
            AutoCleanStatusText = $"已更新：每{WeekDayOptions[SelectedWeekDayIndex]} {SelectedHour:00}:00 自动清理";
        }
        catch (Exception ex)
        {
            AutoCleanStatusText = "更新计划失败：" + ex.Message;
        }
    }

    /// <summary>立即运行一次（经系统计划任务，验证全链路；日志进审计）。</summary>
    [RelayCommand]
    private void RunAutoCleanNow()
    {
        try
        {
            Cclear.Core.AutoClean.AutoCleanScheduler.RunNow();
            AutoCleanStatusText = "已触发立即运行（后台执行，可在审计日志中查看结果）";
            UiServices.ToastInfo("自动清理", "已触发，稍后可在日志页查看结果");
        }
        catch (Exception ex)
        {
            AutoCleanStatusText = "触发失败：" + ex.Message;
        }
        RefreshAutoCleanLastRun();
    }

    [RelayCommand]
    private void RefreshAutoCleanStatus() => RefreshAutoCleanLastRun();

    /// <summary>应用可执行文件路径（计划任务指向）。</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Cclear.App.exe");

    private void ApplyScheduleFromSelection()
    {
        SettingsStore.Instance.AutoCleanDayOfWeek = (int)MapWeekDay(SelectedWeekDayIndex);
        SettingsStore.Instance.AutoCleanHour = SelectedHour;
        SettingsStore.Save();
    }

    /// <summary>UI 顺序（周六在前）→ DayOfWeek。</summary>
    private DayOfWeek MapWeekDay(int uiIndex) => uiIndex switch
    {
        0 => DayOfWeek.Saturday,
        1 => DayOfWeek.Sunday,
        2 => DayOfWeek.Monday,
        3 => DayOfWeek.Tuesday,
        4 => DayOfWeek.Wednesday,
        5 => DayOfWeek.Thursday,
        _ => DayOfWeek.Friday,
    };

    /// <summary>DayOfWeek → UI 顺序。</summary>
    private static int MapWeekDayIndex(DayOfWeek day) => day switch
    {
        DayOfWeek.Saturday => 0,
        DayOfWeek.Sunday => 1,
        DayOfWeek.Monday => 2,
        DayOfWeek.Tuesday => 3,
        DayOfWeek.Wednesday => 4,
        DayOfWeek.Thursday => 5,
        _ => 6,
    };

    private void RefreshAutoCleanLastRun()
    {
        try
        {
            var status = Cclear.Core.AutoClean.AutoCleanScheduler.GetStatus();
            _suppressAutoCleanToggle = true;
            AutoCleanEnabled = status.Registered;
            _suppressAutoCleanToggle = false;
            AutoCleanLastRunText = !status.Registered
                ? "尚未注册计划任务"
                : status.LastRunTime is null
                    ? "已注册（尚未运行过）"
                    : $"上次运行：{status.LastRunTime:yyyy-MM-dd HH:mm} · {status.Detail}";
        }
        catch (Exception ex)
        {
            AutoCleanLastRunText = "查询计划任务状态失败：" + ex.Message;
        }
    }

    public void LoadAutoClean()
    {
        var settings = SettingsStore.Instance;
        SelectedWeekDayIndex = MapWeekDayIndex((DayOfWeek)Math.Clamp(settings.AutoCleanDayOfWeek, 0, 6));
        SelectedHour = Math.Clamp(settings.AutoCleanHour, 0, 23);
        _suppressAutoCleanToggle = true;
        AutoCleanEnabled = Cclear.Core.AutoClean.AutoCleanScheduler.IsRegistered();
        _suppressAutoCleanToggle = false;
        RefreshAutoCleanLastRun();
    }

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
