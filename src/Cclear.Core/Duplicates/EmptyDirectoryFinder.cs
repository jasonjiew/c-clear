using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cclear.Core.Cleaner;
using Cclear.Core.Scanner;

namespace Cclear.Core.Duplicates;

/// <summary>
/// 空目录查找：自底向上折叠——某目录若不含任何文件（且所有子目录同样为空），
/// 则只报告该链的最顶端目录（删除一次即可整链清掉）。
/// 排除：reparse 目录、黑名单目录、用户配置的排除目录；根目录本身永不报告。
/// </summary>
public static class EmptyDirectoryFinder
{
    public static IReadOnlyList<string> Find(string root, IReadOnlyList<string>? excludePaths, CancellationToken ct)
    {
        var scan = new ManagedTreeScanner()
            .ScanAsync(new ScanRequest(root, excludePaths), null, ct).GetAwaiter().GetResult();
        var tree = scan.Tree;

        // hasContent[dirIndex]：该目录自身含文件 / reparse 子目录 / 含内容的子目录
        var hasContent = new Dictionary<int, bool>();
        Compute(tree, tree.RootIndex, hasContent, ct);

        var excludes = (excludePaths ?? Array.Empty<string>()).ToList();
        var result = new List<string>();
        ReportTopmostEmpty(tree, tree.RootIndex, hasContent, excludes, result, ct);
        return result;
    }

    /// <summary>返回 true 表示该目录“非空”（自身或子树含文件）。</summary>
    private static bool Compute(FileTree tree, int dirIndex, Dictionary<int, bool> hasContent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = tree.GetNode(dirIndex);
        bool content = false;
        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                var childNode = tree.GetNode(child);
                if (childNode.IsDirectory)
                {
                    // reparse 子目录视为“有内容”（不触碰也不折叠）
                    content |= childNode.IsReparsePoint || Compute(tree, child, hasContent, ct);
                }
                else
                {
                    content = true; // 有文件
                }
            }
        }
        hasContent[dirIndex] = content;
        return content;
    }

    private static void ReportTopmostEmpty(FileTree tree, int dirIndex, Dictionary<int, bool> hasContent,
        List<string> excludes, List<string> result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var node = tree.GetNode(dirIndex);
        if (node.Children == null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (!childNode.IsDirectory || childNode.IsReparsePoint)
            {
                continue;
            }
            if (!hasContent[child])
            {
                // 整个子树为空：报告该链顶端，不再深入
                var path = tree.GetPath(child);
                if (!IsExcludedOrProtected(path, excludes))
                {
                    result.Add(path);
                }
                continue;
            }
            ReportTopmostEmpty(tree, child, hasContent, excludes, result, ct);
        }
    }

    private static bool IsExcludedOrProtected(string path, List<string> excludes)
    {
        if (GlobalBlacklist.IsProtected(path))
        {
            return true;
        }
        foreach (var excluded in excludes)
        {
            if (path.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(excluded + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
