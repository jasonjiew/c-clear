using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
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
        // LiveCharts 画刷不走 DynamicResource，主题切换时原地重刷
        ThemeService.ThemeChanged += HistoryChart.ApplyTheme;
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
        _licenseStatusText = "";
        LoadLicenseStatus();
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

    /// <summary>最近 30 天清理趋势（LiveCharts，P1；轴与系列只创建一次，原地更新）。</summary>
    public Services.TrendChartModel HistoryChart { get; } = new("清理量");

    private double[] _historyValues = Array.Empty<double>();
    private DateOnly[] _historyDates = Array.Empty<DateOnly>();

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
            _historyDates = trend.Select(p => p.Date).ToArray();
            _historyValues = trend.Select(p => (double)p.Bytes).ToArray();
            HistoryChart.Update(_historyDates, _historyValues);
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

    // ---------- 每日空间采样（V3 P6） ----------

    [ObservableProperty]
    private bool _spaceSamplingEnabled;

    [ObservableProperty]
    private string _spaceSamplingStatusText = "";

    private bool _suppressSamplingToggle;

    partial void OnSpaceSamplingEnabledChanged(bool value)
    {
        if (_suppressSamplingToggle)
        {
            return;
        }
        try
        {
            if (value)
            {
                Cclear.Core.Trend.SpaceSnapshotScheduler.Register(9, ExecutablePath);
                SpaceSamplingStatusText = "已注册：每天 09:00 自动采样一次剩余空间（追加一行快照即退出）";
                UiServices.ToastSuccess("空间采样", "每日采样任务已注册");
            }
            else
            {
                Cclear.Core.Trend.SpaceSnapshotScheduler.Unregister();
                SpaceSamplingStatusText = "已取消每日采样任务";
                UiServices.ToastInfo("空间采样", "每日采样任务已取消");
            }
        }
        catch (Exception ex)
        {
            SpaceSamplingStatusText = "操作失败：" + ex.Message;
            _suppressSamplingToggle = true;
            SpaceSamplingEnabled = !value;
            _suppressSamplingToggle = false;
        }
    }

    [RelayCommand]
    private void RefreshSamplingStatus()
    {
        try
        {
            _suppressSamplingToggle = true;
            SpaceSamplingEnabled = Cclear.Core.Trend.SpaceSnapshotScheduler.IsRegistered();
            _suppressSamplingToggle = false;
            var snapshotCount = Cclear.Core.Trend.SpaceSnapshotStore.Read().Count;
            SpaceSamplingStatusText = Cclear.Core.Trend.SpaceSnapshotScheduler.IsRegistered()
                ? $"已注册每日采样；当前已有 {snapshotCount:N0} 条空间快照"
                : $"未注册每日采样；当前已有 {snapshotCount:N0} 条空间快照";
        }
        catch (Exception ex)
        {
            SpaceSamplingStatusText = "查询采样任务失败：" + ex.Message;
        }
    }

    // ---------- 周报导出（V3 P6，Pro 底座：未激活也可用） ----------

    [ObservableProperty]
    private bool _isExportingReport;

    [ObservableProperty]
    private string _reportStatusText = "";

    public bool ReportIsPro =>
        Cclear.Core.Licensing.LicenseService.HasFeature(Cclear.Core.Licensing.LicenseService.FeatureTrendForecast);

    public bool CanExportReport => !IsExportingReport;

    partial void OnIsExportingReportChanged(bool value) => OnPropertyChanged(nameof(CanExportReport));

    /// <summary>导出本周 HTML 周报（含 Top10 大文件扫描，约 30 秒）。</summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportWeeklyReportAsync()
    {
        IsExportingReport = true;
        var driveLetter = Cclear.Core.Win32.DriveCatalog.TryGetRoot(SettingsStore.Instance.SelectedDrive) is { } root
            ? root[..1]
            : "C";
        ReportStatusText = $"正在生成 {driveLetter} 盘周报（扫描大文件约 30 秒）…";
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var weekStart = today.AddDays(((int)today.DayOfWeek + 6) % 7 - 6); // 本周一
            var entries = Cclear.Core.History.CleanHistoryStore.Read();
            var weekEntries = entries.Where(e => e.TimeUtc.ToLocalTime().Date >= weekStart.ToDateTime(TimeOnly.MinValue)).ToList();
            var weekCleanBytes = weekEntries.Sum(e => Math.Max(0, e.Bytes));
            var trend = Cclear.Core.History.CleanHistoryStore.BuildDailyTrend(entries, 30, today, driveLetter);
            var snapshots = await Task.Run(() => Cclear.Core.Trend.SpaceSnapshotStore.Read());
            var forecast = Cclear.Core.Trend.SpaceForecaster.Forecast(snapshots, driveLetter + ":\\");
            var info = Cclear.Core.Win32.VolumeInformation.Query(driveLetter + ":\\");
            ReportStatusText = "正在扫描大文件 Top10…";
            var topFiles = await Task.Run(() =>
            {
                var scanner = new Cclear.Core.Scanner.ManagedTreeScanner();
                var result = scanner.ScanAsync(new Cclear.Core.Scanner.ScanRequest(
                    driveLetter + ":\\", SettingsStore.Instance.NormalizedExclusions()), null, CancellationToken.None).GetAwaiter().GetResult();
                return Cclear.Core.Analysis.SpaceAnalysis.FindLargeFiles(
                    result.Tree, Cclear.Core.Analysis.SpaceAnalysis.DefaultLargeFileThresholdBytes, 10)
                    .Select(f => (f.Path, f.SizeBytes))
                    .ToList();
            });
            var suggestions = new System.Collections.Generic.List<string>();
            if (weekCleanBytes == 0)
            {
                suggestions.Add("本周还没有清理记录，可到“总览”执行一次体检。");
            }
            if (forecast is { Reliable: true })
            {
                suggestions.Add($"按最近趋势，{driveLetter} 盘约 {forecast.DaysUntilFull} 天后占满，建议开启每周自动清理。");
            }
            if (topFiles.Count > 0)
            {
                suggestions.Add("大文件多为虚拟机磁盘/安装包时，建议手动确认后处理。");
            }
            var path = await Task.Run(() => Cclear.Core.Reporting.WeeklyReportGenerator.WriteHtml(
                new Cclear.Core.Reporting.WeeklyReportInput(
                    driveLetter, weekStart, DateTime.Now, trend, weekCleanBytes, weekEntries.Count,
                    info?.FreeBytes ?? 0, info?.TotalBytes ?? 0, forecast, topFiles, suggestions)));
            ReportStatusText = "已导出：" + path;
            UiServices.ToastSuccess("周报", "本周报告已生成");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportStatusText = "导出失败：" + ex.Message;
        }
        finally
        {
            IsExportingReport = false;
        }
    }

    // ---------- 许可证（F6：技术底座，本期不设功能墙） ----------

    [ObservableProperty]
    private string _licenseStatusText;

    /// <summary>当前许可证（未装/无效为 null）。</summary>
    public Cclear.Core.Licensing.LicenseInfo? CurrentLicense => Cclear.Core.Licensing.LicenseService.GetCurrent();

    public string LicensePath => Cclear.Core.Licensing.LicenseService.LicensePath;

    public void LoadLicenseStatus()
    {
        var result = Cclear.Core.Licensing.LicenseService.ValidateFile();
        LicenseStatusText = result.IsValid
            ? $"Pro 已激活：{result.License!.Name}（{result.License.LicenseId}）"
            + (result.License.ExpiresAtUtc is null ? "，永久有效" : $"，有效期至 {result.License.ExpiresAtUtc:yyyy-MM-dd}")
            : "免费版（全部现有功能可用；Pro 为可选支持项，不设功能墙）";
        OnPropertyChanged(nameof(CurrentLicense));
    }

    /// <summary>导入许可证文件（Ed25519 离线验证，通过后安装）。</summary>
    [RelayCommand]
    private void ImportLicense()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择许可证文件",
            Filter = "许可证文件 (*.dat)|*.dat|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var license = Cclear.Core.Licensing.LicenseService.Install(dialog.FileName);
            UiServices.ToastSuccess("许可证", $"已激活 Pro（{license.LicenseId}）");
        }
        catch (Exception ex)
        {
            UiServices.ToastWarning("许可证", ex.Message);
        }
        LoadLicenseStatus();
    }

    [RelayCommand]
    private void RemoveLicense()
    {
        try
        {
            Cclear.Core.Licensing.LicenseService.RemoveLicense();
            UiServices.ToastInfo("许可证", "已移除许可证");
        }
        catch (Exception ex)
        {
            UiServices.ToastWarning("许可证", ex.Message);
        }
        LoadLicenseStatus();
    }

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
