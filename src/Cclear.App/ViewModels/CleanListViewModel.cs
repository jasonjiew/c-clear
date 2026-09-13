using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Cclear.Core;
using Cclear.Core.Rules;

namespace Cclear.App.ViewModels;

/// <summary>清理清单页：体检结果展示与勾选（v0；清理执行在 W3 接入）。</summary>
public sealed partial class CleanListViewModel : ObservableObject
{
    public string Title => "清理清单";

    [ObservableProperty]
    private string _summaryText = "尚未体检。请到“总览”页点击“一键体检”。";

    public ObservableCollection<CleanCategoryViewModel> Categories { get; } = new();

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
