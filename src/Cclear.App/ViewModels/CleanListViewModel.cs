using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.App.Services;
using Cclear.Core;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;

namespace Cclear.App.ViewModels;

/// <summary>清理清单页：体检结果展示、勾选与清理执行。</summary>
public sealed partial class CleanListViewModel : ObservableObject
{
    private readonly ICleaner _cleaner;
    private readonly ICleanDialogs _dialogs;

    public string Title => "清理清单";

    public CleanListViewModel() : this(new ShellCleaner(), new DialogService())
    {
    }

    public CleanListViewModel(ICleaner cleaner, ICleanDialogs dialogs)
    {
        _cleaner = cleaner;
        _dialogs = dialogs;
    }

    [ObservableProperty]
    private string _summaryText = "尚未体检。请到“总览”页点击“一键体检”。";

    [ObservableProperty]
    private bool _isCleaning;

    public ObservableCollection<CleanCategoryViewModel> Categories { get; } = new();

    public bool CanClean => !IsCleaning && Categories.Any(c => c.IsChecked && (c.Category.IsShellAction || c.Category.Items.Count > 0));

    partial void OnIsCleaningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClean));
        ExecuteCleanCommand.NotifyCanExecuteChanged();
    }

    public void LoadPlan(CleanPlan plan)
    {
        Categories.Clear();
        foreach (var category in plan.Categories.OrderByDescending(c => c.EstimatedBytes))
        {
            Categories.Add(new CleanCategoryViewModel(category));
        }
        SummaryText = Categories.Count == 0
            ? "没有发现可清理的内容。"
            : $"共 {Categories.Count} 类可清理，预计可释放 {ByteSizeFormatter.Format(plan.Categories.Sum(c => c.EstimatedBytes))}。删除默认进入回收站，可随时还原。";
        OnPropertyChanged(nameof(CanClean));
        ExecuteCleanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private void ExecuteClean()
    {
        var selected = Categories
            .Where(c => c.IsChecked && (c.Category.IsShellAction || c.Category.Items.Count > 0))
            .Select(c => c.Category)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        // 红线：Manual（仅报告）类不可执行；以上过滤已排除（Items 为空且非 ShellAction）
        bool permanent = Services.SettingsStore.Instance.PermanentDelete;
        IsCleaning = true;
        try
        {
            if (!_dialogs.ConfirmClean(selected, useRecycleBin: !permanent))
            {
                return;
            }
            var result = _dialogs.RunWithProgress((progress, ct) =>
                _cleaner.ExecuteAsync(selected, new CleanOptions(UseRecycleBin: !permanent), progress, ct)
                    .GetAwaiter().GetResult());
            _dialogs.ShowResult(result, (_cleaner as ShellCleaner)?.LastAuditLogPath ?? "",
                selected.Sum(c => c.EstimatedBytes));
            Categories.Clear();
            SummaryText = $"清理完成：实际释放 {ByteSizeFormatter.Format(result.FreedBytes)}"
                + $"（删除 {result.DeletedFiles:N0} 项，跳过 {result.SkippedFiles:N0} 项）。可到“总览”重新体检。";
        }
        catch (OperationCanceledException)
        {
            SummaryText = "清理已取消（已完成的删除保留）。";
        }
        catch (Exception ex)
        {
            SummaryText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsCleaning = false;
        }
    }
}

/// <summary>分类行 VM：勾选逻辑 = Safe 默认勾选 / Caution 默认勾选+红字 / Manual 永不自动勾选。</summary>
public sealed partial class CleanCategoryViewModel : ObservableObject
{
    public CleanCategoryViewModel(CleanCategory category)
    {
        Category = category;
        _isChecked = category.Level == SafetyLevel.Safe || category.Level == SafetyLevel.Caution;
        LevelText = category.Level switch
        {
            SafetyLevel.Safe => "安全",
            SafetyLevel.Caution => "谨慎",
            _ => "手动",
        };
        LevelBrush = category.Level switch
        {
            SafetyLevel.Safe => "#2E7D32",
            SafetyLevel.Caution => "#C62828",
            _ => "#6D4C41",
        };
        SizeText = ByteSizeFormatter.Format(category.EstimatedBytes);
        FileCountText = category.FileCount > 0 ? $"（含 {category.FileCount:N0} 个文件）" : "";
        ItemsPreview = category.Items.Take(20).Select(i => new CleanItemRow(i.Path, ByteSizeFormatter.Format(i.SizeBytes))).ToList();
        ItemsCountText = category.Items.Count > ItemsPreview.Count
            ? $"… 以及另外 {category.Items.Count - ItemsPreview.Count:N0} 个文件"
            : "";
    }

    public CleanCategory Category { get; }
    public string DisplayName => Category.DisplayName;
    public string LevelText { get; }
    public string LevelBrush { get; }
    public string SizeText { get; }
    public string FileCountText { get; }
    public string Explanation => Category.Explanation;
    public System.Collections.Generic.IReadOnlyList<CleanItemRow> ItemsPreview { get; }
    public string ItemsCountText { get; }

    [ObservableProperty]
    private bool _isChecked;
}

public sealed record CleanItemRow(string Path, string SizeText);
