using System;
using System.Collections.Generic;
using System.Linq;
using Cclear.Core;
using Cclear.Core.Scanner;

namespace Cclear.App.ViewModels;

/// <summary>目录树节点 VM：懒加载 children（首次展开时构建并排序）。</summary>
public sealed class TreeNodeViewModel
{
    private readonly FileTree _tree;
    private IReadOnlyList<TreeNodeViewModel>? _children;

    public TreeNodeViewModel(FileTree tree, int index)
    {
        _tree = tree;
        Index = index;
        var node = tree.GetNode(index);
        IsDirectory = node.IsDirectory;
        IsReparsePoint = node.IsReparsePoint;
        IsOneDrivePlaceholder = node.IsOneDrivePlaceholder;
        Name = index == tree.RootIndex ? node.Name : node.Name + (IsDirectory ? "\\" : "");
        SizeText = ByteSizeFormatter.Format(node.SizeBytes);
        CountText = IsDirectory && node.FileCount > 0 ? $"{node.FileCount:N0} 个文件" : "";
        Badge = node.IsReparsePoint ? (node.IsOneDrivePlaceholder ? " [云占位]" : " [联接]") : "";
    }

    public int Index { get; }
    public bool IsDirectory { get; }
    public bool IsReparsePoint { get; }
    public bool IsOneDrivePlaceholder { get; }
    public string Name { get; }
    public string SizeText { get; }
    public string CountText { get; }
    public string Badge { get; }

    /// <summary>节点图标：目录/文件/云占位/联接（U3 交互打磨项，随 Fluent 主题）。</summary>
    public Wpf.Ui.Controls.SymbolRegular IconSymbol => IsReparsePoint
        ? (IsOneDrivePlaceholder ? Wpf.Ui.Controls.SymbolRegular.Cloud24 : Wpf.Ui.Controls.SymbolRegular.Link24)
        : IsDirectory ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Document24;

    public IReadOnlyList<TreeNodeViewModel> Children => _children ??= BuildChildren();

    private IReadOnlyList<TreeNodeViewModel> BuildChildren()
    {
        var node = _tree.GetNode(Index);
        if (node.Children is not { Count: > 0 })
        {
            return Array.Empty<TreeNodeViewModel>();
        }
        var vms = node.Children
            .Select(i => _tree.GetNode(i))
            .OrderByDescending(n => n.IsDirectory)
            .ThenByDescending(n => n.SizeBytes)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new TreeNodeViewModel(_tree, n.Index))
            .ToList();
        return vms;
    }
}
