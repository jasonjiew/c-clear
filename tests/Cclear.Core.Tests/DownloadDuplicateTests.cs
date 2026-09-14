using System;
using System.IO;
using System.Linq;
using Cclear.Core.Duplicates;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P8 下载重复检测："(1)" 后缀模式、保留最新、跨子目录同名。</summary>
public class DownloadDuplicateDetectorTests : IDisposable
{
    private readonly string _root;

    public DownloadDuplicateDetectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cclear-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Write(string relative, long sizeKb, DateTime? lastWrite = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeKb * 1024]);
        if (lastWrite is not null)
        {
            File.SetLastWriteTimeUtc(path, lastWrite.Value);
        }
        return path;
    }

    [Fact]
    public void ParseCopyStem_StripCopySuffix()
    {
        Assert.Equal("setup", DownloadDuplicateDetector.ParseCopyStem("setup (1).exe"));
        Assert.Equal("setup", DownloadDuplicateDetector.ParseCopyStem("setup (23).exe"));
        Assert.Equal("setup", DownloadDuplicateDetector.ParseCopyStem("setup.exe"));
        Assert.Equal("archive (x)", DownloadDuplicateDetector.ParseCopyStem("archive (x).zip")); // 非数字后缀不剥离
        // 已知边界：多扩展名（.tar.gz）只按最后一个扩展名剥离一次
        Assert.Equal("my file (2).tar", DownloadDuplicateDetector.ParseCopyStem("my file (2).tar.gz"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_CopySuffixGroup_KeepsNewest()
    {
        var baseTime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Write("setup.exe", 100, baseTime);
        Write("setup (1).exe", 100, baseTime.AddDays(1));
        Write("setup (2).exe", 100, baseTime.AddDays(2)); // 最新 → 保留
        var report = await DownloadDuplicateDetector.FindAsync(
            new DownloadDuplicateOptions(_root), null, CancellationToken.None);
        var group = Assert.Single(report.Groups);
        Assert.Equal("setup.exe", group.Pattern);
        Assert.Equal(3, group.Files.Count);
        var keeper = Assert.Single(group.Files, f => f.IsKeeper);
        Assert.EndsWith("setup (2).exe", keeper.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(200 * 1024L, group.WastedBytes);
        Assert.True(group.SameContent);
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_SameNameInDifferentSubdirs_Grouped()
    {
        var baseTime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Write("app.exe", 50, baseTime);
        Write(@"old\app.exe", 60, baseTime.AddDays(-3)); // 不同子目录同名，内容不同大小
        var report = await DownloadDuplicateDetector.FindAsync(
            new DownloadDuplicateOptions(_root), null, CancellationToken.None);
        var group = Assert.Single(report.Groups);
        Assert.Equal("app.exe", group.Pattern);
        Assert.False(group.SameContent); // 大小不一致 → "可能内容不同"提示
        Assert.True(group.Files.Single(f => f.IsKeeper).SizeBytes == 50 * 1024); // 保留最新
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_SingleFiles_NoGroups()
    {
        Write("only-one.exe", 10);
        Write("readme.txt", 1);
        var report = await DownloadDuplicateDetector.FindAsync(
            new DownloadDuplicateOptions(_root), null, CancellationToken.None);
        Assert.Empty(report.Groups);
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_ZeroByteAndUnreadable_Skipped()
    {
        var baseTime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Write("empty (1).bin", 0, baseTime);
        Write("empty (2).bin", 0, baseTime.AddDays(1));
        var report = await DownloadDuplicateDetector.FindAsync(
            new DownloadDuplicateOptions(_root), null, CancellationToken.None);
        Assert.Empty(report.Groups); // 0 字节文件不参与（无回收价值）
    }

    [Fact]
    public async System.Threading.Tasks.Task Scan_ExcludePaths_Respected()
    {
        var baseTime = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        Write("keep.exe", 10, baseTime);
        Write(@"sub\keep (1).exe", 10, baseTime.AddDays(1));
        // 排除目录为前缀匹配（与扫描器语义一致）：排除 sub 整个子树
        var report = await DownloadDuplicateDetector.FindAsync(
            new DownloadDuplicateOptions(_root, [Path.Combine(_root, "sub")]), null, CancellationToken.None);
        Assert.Empty(report.Groups);
    }
}
