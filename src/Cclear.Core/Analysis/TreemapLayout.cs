using System;
using System.Collections.Generic;
using System.Linq;
using Cclear.Core.Scanner;

namespace Cclear.Core.Analysis;

/// <summary>矩形树图数据项（一个展示块）。</summary>
public sealed record TreemapItem(
    string Name,
    string Path,
    long SizeBytes,
    int NodeIndex,
    bool IsDirectory,
    /// <summary>聚合块（“其他”，代表多个被截断的小项，不可钻取）。</summary>
    bool IsAggregate = false);

/// <summary>布局结果：项 + 分配到的矩形（坐标原点左上，单位与画布一致）。</summary>
public sealed record TreemapRect(TreemapItem Item, double X, double Y, double Width, double Height);

/// <summary>
/// Squarified Treemap 布局（Bruls/Huizing/van Wijk）：
/// 面积 ∝ 大小，宽高比尽量接近 1；只处理直接子节点（深度 1），
/// 更深层级由 UI 钻取（双击进入子目录）按需展开。
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// 挑选当前节点的直接子节点作为树图数据：0 尺寸过滤、按大小降序、
    /// 超过 maxBlocks 时尾部聚合为“其他”块（性能护栏：渲染块数有上界）。
    /// </summary>
    public static IReadOnlyList<TreemapItem> SelectTopChildren(FileTree tree, int nodeIndex, int maxBlocks = 400)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (maxBlocks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBlocks));
        }
        var node = tree.GetNode(nodeIndex);
        if (node.Children is null || node.Children.Count == 0)
        {
            return Array.Empty<TreemapItem>();
        }

        var items = new List<TreemapItem>();
        foreach (var childIndex in node.Children)
        {
            var child = tree.GetNode(childIndex);
            var size = Math.Max(0, tree.GetSize(childIndex));
            if (size <= 0)
            {
                continue;
            }
            items.Add(new TreemapItem(child.Name, tree.GetPath(childIndex), size, childIndex, child.IsDirectory));
        }
        if (items.Count == 0)
        {
            return items;
        }
        items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        if (items.Count <= maxBlocks)
        {
            return items;
        }

        var top = new List<TreemapItem>(maxBlocks);
        top.AddRange(items.Take(maxBlocks - 1));
        long restBytes = 0;
        var rest = items.Skip(maxBlocks - 1).ToList();
        foreach (var item in rest)
        {
            restBytes += item.SizeBytes;
        }
        top.Add(new TreemapItem($"其他（{rest.Count:N0} 项）", "", restBytes, -1, IsDirectory: false, IsAggregate: true));
        return top;
    }

    /// <summary>Squarify 布局：面积按 SizeBytes 占比分配整个画布。</summary>
    public static IReadOnlyList<TreemapRect> Squarify(IReadOnlyList<TreemapItem> items, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new List<TreemapRect>();
        if (items.Count == 0 || width <= 0 || height <= 0)
        {
            return result;
        }
        long total = 0;
        foreach (var item in items)
        {
            total += Math.Max(0, item.SizeBytes);
        }
        if (total <= 0)
        {
            return result;
        }

        var canvasArea = width * height;
        var areas = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            areas[i] = canvasArea * Math.Max(0, items[i].SizeBytes) / total;
        }

        double x = 0, y = 0, w = width, h = height;
        var row = new List<int>(16);
        double rowArea = 0;
        var index = 0;
        while (index < items.Count)
        {
            var shortestSide = Math.Min(w, h);
            var worstCurrent = row.Count > 0 ? WorstRatio(row, areas, rowArea, shortestSide, -1) : double.MaxValue;
            var worstIfAdded = WorstRatio(row, areas, rowArea + areas[index], shortestSide, index);
            if (row.Count == 0 || worstIfAdded <= worstCurrent)
            {
                row.Add(index);
                rowArea += areas[index];
                index++;
            }
            else
            {
                PlaceRow(result, items, row, areas, ref x, ref y, ref w, ref h, rowArea);
                row.Clear();
                rowArea = 0;
            }
        }
        if (row.Count > 0)
        {
            PlaceRow(result, items, row, areas, ref x, ref y, ref w, ref h, rowArea);
        }
        return result;
    }

    /// <summary>当前行的最差宽高比（extraIndex >= 0 表示试探把该项加入行内）。</summary>
    private static double WorstRatio(List<int> row, double[] areas, double rowArea, double shortestSide, int extraIndex)
    {
        if (rowArea <= 0 || shortestSide <= 0 || (row.Count == 0 && extraIndex < 0))
        {
            return double.MaxValue;
        }
        var thickness = rowArea / shortestSide;
        var worst = 0.0;
        foreach (var index in row)
        {
            worst = Math.Max(worst, ItemRatio(areas[index], thickness));
        }
        if (extraIndex >= 0)
        {
            worst = Math.Max(worst, ItemRatio(areas[extraIndex], thickness));
        }
        return worst;
    }

    private static double ItemRatio(double area, double thickness)
    {
        if (area <= 0 || thickness <= 0)
        {
            return double.MaxValue;
        }
        var length = area / thickness;
        return Math.Max(thickness / length, length / thickness);
    }

    /// <summary>把一行贴着剩余矩形的短边放下（h<=w → 左侧竖条，否则顶部横条）。</summary>
    private static void PlaceRow(List<TreemapRect> result, IReadOnlyList<TreemapItem> items, List<int> row,
        double[] areas, ref double x, ref double y, ref double w, ref double h, double rowArea)
    {
        if (rowArea <= 0)
        {
            return;
        }
        if (h <= w)
        {
            var thickness = Math.Min(rowArea / h, w);
            var offsetY = y;
            foreach (var index in row)
            {
                var itemHeight = areas[index] / thickness;
                result.Add(new TreemapRect(items[index], x, offsetY, thickness, itemHeight));
                offsetY += itemHeight;
            }
            x += thickness;
            w -= thickness;
        }
        else
        {
            var thickness = Math.Min(rowArea / w, h);
            var offsetX = x;
            foreach (var index in row)
            {
                var itemWidth = areas[index] / thickness;
                result.Add(new TreemapRect(items[index], offsetX, y, itemWidth, thickness));
                offsetX += itemWidth;
            }
            y += thickness;
            h -= thickness;
        }
    }
}
