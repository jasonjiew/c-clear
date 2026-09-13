using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.Core;
using Cclear.Core.Analysis;
using Cclear.Core.Analyzer;
using Cclear.Core.History;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Cclear.Core.Win32;

namespace Cclear.App.ViewModels;

/// <summary>总览页：健康分 + 磁盘环形图 + 一键体检 + 深度分析（大文件/类型统计）。</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly Action<CleanPlan> _onPlanReady;
    private readonly CleanPlanAnalyzer _analyzer = new();
    private readonly ManagedTreeScanner _scanner = new();
    private long _usedBytes;
    private long _totalBytes;

    public string Title => "总览";

    public OverviewViewModel(Action<CleanPlan> onPlanReady)
    {
        _onPlanReady = onPlanReady;
        Refresh();
    }

    [ObservableProperty]
    private string _driveDetail = "";

    [ObservableProperty]
    private double _usedPercent;

    [ObservableProperty]
    private string _usedPercentText = "0%";

    [ObservableProperty]
    private string _totalBytesText = "—";

    [ObservableProperty]
    private string _usedBytesText = "—";

    [ObservableProperty]
    private string _freeBytesText = "—";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "点击“一键体检”查找可安全清理的内容。";

    [ObservableProperty]
    private string _healthScoreText = "—";

    [ObservableProperty]
    private string _healthScoreLevel = "尚未体检";

    [ObservableProperty]
    private string _healthScoreSummary = "完成一次体检后，这里会给出 0–100 的健康分。";

    [ObservableProperty]
    private double _healthScoreValue = -1;

    [ObservableProperty]
    private string _planSummaryText = "尚未体检";

    [ObservableProperty]
    private string _lastCleanText = "尚未记录清理";

    [ObservableProperty]
    private string _trendSummaryText = "暂无清理记录";

    [ObservableProperty]
    private bool _hasTrendData;

    // ---------- OneDrive 云占位统计（F5：只统计、绝不触碰） ----------

    [ObservableProperty]
    private string _cloudStatsText = "正在检查 OneDrive…";

    [ObservableProperty]
    private bool _hasCloudData;

    /// <summary>最近 30 天每日释放字节数（0 补齐）。</summary>
    public ObservableCollection<double> TrendValues { get; } = new(Enumerable.Repeat(0.0, 30));

    public ObservableCollection<LargeFileRow> LargeFiles { get; } = new();

    public ObservableCollection<ExtensionRow> Extensions { get; } = new();

    public bool CanRun => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        HealthCheckCommand.NotifyCanExecuteChanged();
        DeepScanCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Refresh() => RefreshDashboard();

    /// <summary>刷新磁盘信息 + 清理趋势（导航到本页与启动时调用）。</summary>
    public void RefreshDashboard()
    {
        var info = VolumeInformation.Query(@"C:\");
        if (info is null)
        {
            DriveDetail = "无法读取 C 盘信息";
            return;
        }
        _usedBytes = info.UsedBytes;
        _totalBytes = info.TotalBytes;
        UsedPercent = info.TotalBytes == 0 ? 0 : info.UsedBytes * 100.0 / info.TotalBytes;
        UsedPercentText = $"{UsedPercent:F0}%";
        TotalBytesText = ByteSizeFormatter.Format(info.TotalBytes);
        UsedBytesText = ByteSizeFormatter.Format(info.UsedBytes);
        FreeBytesText = ByteSizeFormatter.Format(info.FreeBytes);
        DriveDetail = $"C 盘已用 {UsedBytesText} / 共 {TotalBytesText}，剩余 {FreeBytesText}";
        UpdateLastCleanText();
        LoadTrend();
        _ = LoadCloudStatsAsync();
    }

    /// <summary>后台统计 OneDrive 云占位文件（只读元数据，绝不触发下载）。</summary>
    private async Task LoadCloudStatsAsync()
    {
        try
        {
            var stats = await Task.Run(
                () => Cclear.Core.Cloud.CloudPlaceholderStatistics.ScanAll(CancellationToken.None));
            HasCloudData = stats is not null;
            CloudStatsText = stats is null
                ? "未发现 OneDrive（显示空状态）"
                : $"占位文件 {stats.PlaceholderFiles:N0} 个 / 逻辑大小 {ByteSizeFormatter.Format(stats.LogicalBytes)}"
                  + $"（云端按需文件，本工具只统计绝不触碰）";
        }
        catch (Exception)
        {
            HasCloudData = false;
            CloudStatsText = "OneDrive 统计失败（不影响清理功能）";
        }
    }

    /// <summary>读取清理历史并聚合 30 天趋势（F2）。</summary>
    private void LoadTrend()
    {
        IReadOnlyList<TrendPoint> trend;
        try
        {
            var entries = CleanHistoryStore.Read();
            trend = CleanHistoryStore.BuildDailyTrend(entries, 30, DateOnly.FromDateTime(DateTime.Now));
        }
        catch (Exception)
        {
            trend = Array.Empty<TrendPoint>();
        }
        TrendValues.Clear();
        foreach (var point in trend)
        {
            TrendValues.Add(point.Bytes);
        }
        var total = trend.Sum(p => p.Bytes);
        var activeDays = trend.Count(p => p.Bytes > 0);
        HasTrendData = activeDays > 0;
        TrendSummaryText = activeDays == 0
            ? "暂无清理记录"
            : $"近 30 天共清理 {ByteSizeFormatter.Format(total)}，{activeDays} 天有记录";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task HealthCheckAsync()
    {
        IsBusy = true;
        StatusText = "正在体检…";
        try
        {
            var rulesSource = RulesResolver.LoadActive();
            var rules = rulesSource.Rules;
            _analyzer.ExcludePaths = Cclear.App.Services.SettingsStore.Instance.NormalizedExclusions();
            var progress = new Progress<CleanAnalysisProgress>(p =>
                StatusText = $"体检中（{p.RulesDone}/{p.RulesTotal}）：{p.CurrentRule}");
            var plan = await _analyzer.BuildPlanAsync(rules, progress, CancellationToken.None);
            StatusText = $"体检完成（{rulesSource.Source}）：{plan.Categories.Count} 类，预计可释放 {ByteSizeFormatter.Format(plan.TotalEstimatedBytes)}";
            UpdateHealthScore(plan);
            _onPlanReady(plan);
        }
        catch (Exception ex)
        {
            StatusText = "体检失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task DeepScanAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描全 C 盘（大文件与类型分析）…";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
                StatusText = $"分析中：{ByteSizeFormatter.Format(p.BytesSeen)} / {p.FilesScanned:N0} 个文件");
            var result = await _scanner.ScanAsync(new ScanRequest(@"C:\", Cclear.App.Services.SettingsStore.Instance.NormalizedExclusions()), progress, CancellationToken.None);

            LargeFiles.Clear();
            foreach (var f in SpaceAnalysis.FindLargeFiles(result.Tree, SpaceAnalysis.DefaultLargeFileThresholdBytes, 50))
            {
                LargeFiles.Add(new LargeFileRow(f.Path, ByteSizeFormatter.Format(f.SizeBytes)));
            }
            Extensions.Clear();
            foreach (var e in SpaceAnalysis.ExtensionStatistics(result.Tree, 15))
            {
                Extensions.Add(new ExtensionRow(e.Extension, ByteSizeFormatter.Format(e.TotalBytes), e.FileCount));
            }
            StatusText = $"深度分析完成（{result.Tree.Count:N0} 个条目，用时 {result.Elapsed:hh\\:mm\\:ss}）";
        }
        catch (Exception ex)
        {
            StatusText = "深度分析失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateHealthScore(CleanPlan plan)
    {
        var safeBytes = plan.Categories
            .Where(c => c.Level == SafetyLevel.Safe)
            .Sum(c => c.EstimatedBytes);
        var score = HealthScoreCalculator.Calculate(
            safeBytes, _usedBytes, 0, Cclear.App.Services.SettingsStore.Instance.LastCleanAtUtc);
        HealthScoreValue = score.Score;
        HealthScoreText = score.Score.ToString();
        HealthScoreLevel = score.Level;
        HealthScoreSummary = score.Summary;
        PlanSummaryText = plan.Categories.Count == 0
            ? "没有发现可清理的内容"
            : $"预计可释放 {ByteSizeFormatter.Format(plan.TotalEstimatedBytes)}"
              + $"（其中 Safe {ByteSizeFormatter.Format(safeBytes)}）";
    }

    private void UpdateLastCleanText()
    {
        var lastClean = Cclear.App.Services.SettingsStore.Instance.LastCleanAtUtc;
        LastCleanText = lastClean is null
            ? "尚未记录清理"
            : $"上次清理：{lastClean.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }
}

public sealed record LargeFileRow(string Path, string SizeText);

public sealed record ExtensionRow(string Extension, string TotalText, int Count);
