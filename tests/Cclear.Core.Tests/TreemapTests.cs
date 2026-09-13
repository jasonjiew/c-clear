using System;
using System.Linq;
using Cclear.Core.Analysis;
using Cclear.Core.Scanner;
using Xunit;

namespace Cclear.Core.Tests;

public class TreemapLayoutTests
{
    private static FileTree BuildTree(params (string Name, long SizeBytes)[] children)
    {
        var tree = new FileTree();
        tree.AddRoot("ROOT");
        foreach (var (name, size) in children)
        {
            tree.AddNode(name, tree.RootIndex, FileAttributes.Normal, NodeFlags.None, size);
        }
        return tree;
    }

    [Fact]
    public void Squarify_TwoEqualItems_PerfectHalves()
    {
        var items = new[]
        {
            new TreemapItem("a", "ROOT\\a", 100, 1, IsDirectory: false),
            new TreemapItem("b", "ROOT\\b", 100, 2, IsDirectory: false),
        };
        var rects = TreemapLayout.Squarify(items, 100, 100);
        Assert.Equal(2, rects.Count);
        foreach (var rect in rects)
        {
            // 每块恰好一半面积；两等分正方形的最优宽高比就是 2（1:1 无法铺满）
            Assert.Equal(5000, rect.Width * rect.Height, 6);
            Assert.True(Math.Max(rect.Width / rect.Height, rect.Height / rect.Width) <= 2 + 1e-6);
            Assert.True(rect.X >= -1e-6 && rect.Y >= -1e-6);
            Assert.True(rect.X + rect.Width <= 100 + 1e-6);
            Assert.True(rect.Y + rect.Height <= 100 + 1e-6);
        }
        var first = rects[0];
        var second = rects[1];
        // 两块并排不重叠（x 或 y 方向依次排开）
        var separated = second.X + 1e-6 >= first.X + first.Width || second.Y + 1e-6 >= first.Y + first.Height;
        Assert.True(separated, "两块矩形不应重叠");
    }

    [Fact]
    public void Squarify_AreaPreserved_AndWithinCanvas()
    {
        var sizes = new long[] { 5000, 3200, 1200, 700, 250, 120, 60, 30, 10 };
        var items = sizes.Select((s, i) => new TreemapItem($"f{i}", $"ROOT\\f{i}", s, i, false)).ToArray();
        const double width = 640, height = 480;
        var rects = TreemapLayout.Squarify(items, width, height);

        Assert.Equal(items.Length, rects.Count);
        var totalArea = rects.Sum(r => r.Width * r.Height);
        Assert.Equal(width * height, totalArea, 3);
        foreach (var rect in rects)
        {
            Assert.True(rect.X >= -1e-6 && rect.Y >= -1e-6, "矩形不得越出画布左上");
            Assert.True(rect.X + rect.Width <= width + 1e-6, "矩形不得越出画布右侧");
            Assert.True(rect.Y + rect.Height <= height + 1e-6, "矩形不得越出画布底部");
        }
    }

    [Fact]
    public void Squarify_ClassicDistribution_AspectRatiosBounded()
    {
        // 经典 squarify 论文分布（6,6,4,3,2,2,1 / 总 24）在 640x480 下应接近 1
        var sizes = new long[] { 6, 6, 4, 3, 2, 2, 1 };
        var items = sizes.Select((s, i) => new TreemapItem($"f{i}", $"ROOT\\f{i}", s, i, false)).ToArray();
        var rects = TreemapLayout.Squarify(items, 640, 480);
        var worst = rects.Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
        Assert.True(worst <= 3.0, $"最差宽高比 {worst:0.##} 超出预期（应 ≤ 3）");
    }

    [Fact]
    public void Squarify_EmptyItems_OrDegenerateCanvas_ReturnsEmpty()
    {
        Assert.Empty(TreemapLayout.Squarify(Array.Empty<TreemapItem>(), 100, 100));
        var items = new[] { new TreemapItem("a", "a", 10, 1, false) };
        Assert.Empty(TreemapLayout.Squarify(items, 0, 100));
        Assert.Empty(TreemapLayout.Squarify(items, 100, 0));
        var zeroItems = new[] { new TreemapItem("a", "a", 0, 1, false) };
        Assert.Empty(TreemapLayout.Squarify(zeroItems, 100, 100));
    }

    [Fact]
    public void SelectTopChildren_FiltersZeroSizes_AndSortsDescending()
    {
        var tree = BuildTree(("big", 100), ("tiny", 0), ("mid", 50), ("empty", 0));
        var items = TreemapLayout.SelectTopChildren(tree, tree.RootIndex);
        Assert.Equal(2, items.Count);
        Assert.Equal("big", items[0].Name);
        Assert.Equal("mid", items[1].Name);
        Assert.All(items, i => Assert.False(i.IsAggregate));
    }

    [Fact]
    public void SelectTopChildren_ExceedingMax_AggregatesRestAsOther()
    {
        var tree = BuildTree(("a", 10), ("b", 8), ("c", 3), ("d", 1));
        var items = TreemapLayout.SelectTopChildren(tree, tree.RootIndex, maxBlocks: 3);
        Assert.Equal(3, items.Count);
        Assert.Equal("a", items[0].Name);
        Assert.Equal("b", items[1].Name);
        var other = items[2];
        Assert.True(other.IsAggregate);
        Assert.Equal(4, other.SizeBytes);
        Assert.Equal(-1, other.NodeIndex);
        Assert.Contains("2", other.Name);
    }

    [Fact]
    public void SelectTopChildren_WithinMax_NoAggregate()
    {
        var tree = BuildTree(("a", 10), ("b", 8));
        var items = TreemapLayout.SelectTopChildren(tree, tree.RootIndex, maxBlocks: 400);
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.IsAggregate);
    }

    [Fact]
    public void SelectTopChildren_DirectoryFlags_AndSubtreeSizes_PassThrough()
    {
        var tree = new FileTree();
        tree.AddRoot("C:");
        var dir = tree.AddNode("Windows", tree.RootIndex, FileAttributes.Directory, NodeFlags.Directory, 0);
        tree.AddSizeTo(dir, 40);
        tree.AddNode("pagefile.sys", tree.RootIndex, FileAttributes.Normal, NodeFlags.None, 60);
        var items = TreemapLayout.SelectTopChildren(tree, tree.RootIndex);
        Assert.Equal(2, items.Count);
        Assert.Equal(60, items[0].SizeBytes);
        Assert.False(items[0].IsDirectory);
        Assert.Equal("Windows", items[1].Name);
        Assert.True(items[1].IsDirectory);
        Assert.Equal(40, items[1].SizeBytes);
        Assert.Equal("C:\\Windows", items[1].Path);
    }
}
