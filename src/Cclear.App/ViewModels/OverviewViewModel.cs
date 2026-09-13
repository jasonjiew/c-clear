using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.Core;
using Cclear.Core.Analysis;
using Cclear.Core.Analyzer;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Cclear.Core.Win32;

namespace Cclear.App.ViewModels;

/// <summary>总览页：C 盘仪表 + 一键体检 + 深度分析（大文件/类型统计）。</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly Action<CleanPlan> _onPlanReady;
    private readonly CleanPlanAnalyzer _analyzer = new();
    private readonly ManagedTreeScanner _scanner = new();

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
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "点击“一键体检”查找可安全清理的内容。";

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
    private void Refresh()
    {
        var info = VolumeInformation.Query(@"C:\");
        if (info is null)
        {
            DriveDetail = "无法读取 C 盘信息";
            return;
        }
        UsedPercent = info.TotalBytes == 0 ? 0 : info.UsedBytes * 100.0 / info.TotalBytes;
        DriveDetail = $"C 盘已用 {ByteSizeFormatter.Format(info.UsedBytes)} / 共 {ByteSizeFormatter.Format(info.TotalBytes)}"
            + $"，剩余 {ByteSizeFormatter.Format(info.FreeBytes)}";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task HealthCheckAsync()
    {
        IsBusy = true;
        StatusText = "正在体检…";
        try
        {
            var rules = RulePackLoader.LoadEmbedded();
            System.IO.File.AppendAllText(@"D:\ai_develop_project_space\c-clear\uidebug.log",
                $"[{DateTime.Now:HH:mm:ss}] rules={rules.Count}\n");
            var progress = new Progress<CleanAnalysisProgress>(p =>
                StatusText = $"体检中（{p.RulesDone}/{p.RulesTotal}）：{p.CurrentRule}");
            var plan = await _analyzer.BuildPlanAsync(rules, progress, CancellationToken.None);
            System.IO.File.AppendAllText(@"D:\ai_develop_project_space\c-clear\uidebug.log",
                $"[{DateTime.Now:HH:mm:ss}] plan={plan.Categories.Count} total={plan.TotalEstimatedBytes}\n");
            StatusText = $"体检完成：{plan.Categories.Count} 类，预计可释放 {ByteSizeFormatter.Format(plan.TotalEstimatedBytes)}";
            _onPlanReady(plan);
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(@"D:\ai_develop_project_space\c-clear\uidebug.log",
                $"[{DateTime.Now:HH:mm:ss}] EXC {ex}\n");
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
            var result = await _scanner.ScanAsync(new ScanRequest(@"C:\"), progress, CancellationToken.None);

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
}

public sealed record LargeFileRow(string Path, string SizeText);

public sealed record ExtensionRow(string Extension, string TotalText, int Count);
