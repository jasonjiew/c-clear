using System;
using System.Collections.Generic;
using Cclear.Core.Scanner;

namespace Cclear.Core.Analysis;

public sealed record LargeFileInfo(string Path, long SizeBytes);

public sealed record ExtensionStat(string Extension, long TotalBytes, int FileCount);

/// <summary>空间分析：大文件 TopN 与扩展名统计（基于 FileTree，只读遍历）。</summary>
public static class SpaceAnalysis
{
    public const long DefaultLargeFileThresholdBytes = 100L * 1024 * 1024; // 100 MB

    public static IReadOnlyList<LargeFileInfo> FindLargeFiles(FileTree tree, long minBytes, int maxCount)
    {
        var list = new List<LargeFileInfo>();
        Collect(tree, tree.RootIndex, f =>
        {
            if (f.SizeBytes >= minBytes)
            {
                list.Add(new LargeFileInfo(tree.GetPath(f.Index), f.SizeBytes));
            }
        });
        list.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return maxCount < list.Count ? list.GetRange(0, maxCount) : list;
    }

    public static IReadOnlyList<ExtensionStat> ExtensionStatistics(FileTree tree, int maxCount)
    {
        var totals = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
        Collect(tree, tree.RootIndex, f =>
        {
            var ext = System.IO.Path.GetExtension(f.Name);
            if (ext.Length == 0)
            {
                ext = "(无扩展名)";
            }
            else
            {
                ext = ext.ToLowerInvariant();
            }
            var entry = totals.TryGetValue(ext, out var v) ? v : (0, 0);
            totals[ext] = (entry.Bytes + f.SizeBytes, entry.Count + 1);
        });
        var list = new List<ExtensionStat>(totals.Count);
        foreach (var (ext, (bytes, count)) in totals)
        {
            list.Add(new ExtensionStat(ext, bytes, count));
        }
        list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return maxCount < list.Count ? list.GetRange(0, maxCount) : list;
    }

    private static void Collect(FileTree tree, int dirIndex, Action<FileNode> visitFile)
    {
        var node = tree.GetNode(dirIndex);
        if (node.Children == null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (childNode.IsDirectory)
            {
                if (!childNode.IsReparsePoint)
                {
                    Collect(tree, child, visitFile);
                }
            }
            else
            {
                visitFile(childNode);
            }
        }
    }
}
