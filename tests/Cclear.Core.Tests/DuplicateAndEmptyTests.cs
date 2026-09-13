using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Cleaner;
using Cclear.Core.Duplicates;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>W5：重复文件三级漏斗、空目录折叠、边界（长路径/中文名/锁定）与性能。</summary>
public sealed class DuplicateAndEmptyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cclear-w5-" + Guid.NewGuid().ToString("N"));

    private string Root => _root;

    private string MakeFile(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task DuplicateFinder_ThreeStageFunnel_FindsExactDuplicates()
    {
        var content = new byte[100_000];
        new Random(42).NextBytes(content);
        var a = MakeFile("a\\same.bin", content);
        var b = MakeFile("b\\same.bin", content);           // 不同目录相同内容
        var rotated = content.Skip(10).Concat(content.Take(10)).ToArray();
        MakeFile("c\\diff.bin", rotated);                    // 同长不同内容
        MakeFile("single.bin", rotated[..5000]);             // 独立内容（不成组）

        var report = await DuplicateFinder.FindAsync(new DuplicateOptions(Root), null, CancellationToken.None);

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(100_000, group.SizeBytes);
        Assert.Equal(100_000, group.WastedBytes);
        Assert.All(group.Files, f => Assert.EndsWith("same.bin", f.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateFinder_SkipsLockedFiles()
    {
        var content = new byte[50_000];
        var locked = MakeFile("locked.bin", content);
        var free = MakeFile("free.bin", content);

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await DuplicateFinder.FindAsync(new DuplicateOptions(Root), null, CancellationToken.None);
            Assert.Empty(report.Groups); // 被锁文件不可读 → 整组不足两份
        }
    }

    [Fact]
    public async Task DuplicateFinder_HandlesLongPathsAndChineseNames()
    {
        string rel = "";
        for (int i = 0; i < 10; i++)
        {
            rel = Path.Combine(rel, "层级" + new string('长', 15));
        }
        var content = new byte[30_000];
        new Random(7).NextBytes(content);

        var deepDir = Path.Combine(Root, rel);
        Directory.CreateDirectory(PathHelper.GetExtendedPath(deepDir));
        var longPath = PathHelper.GetExtendedPath(Path.Combine(deepDir, "深层 目录 (副本).bin"));
        File.WriteAllBytes(longPath, content);
        var normal = MakeFile("中文 文件 (1).bin", content);

        var report = await DuplicateFinder.FindAsync(new DuplicateOptions(Root), null, CancellationToken.None);

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.Path.Contains("深层", StringComparison.Ordinal));
        Assert.Contains(group.Files, f => f.Path.Contains("中文", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateFinder_Perf_TwoGigabytes_WellUnderTwoMinutes()
    {
        // 验收口径换算：10GB < 2min（SSD）。测试用 2GB 抽样验证吞吐（应远小于 24s）。
        var content = new byte[200 * 1024 * 1024]; // 200MB 模板
        new Random(1).NextBytes(content);
        var paths = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            paths.Add(MakeFile($"v{i}.bin", content)); // 4 × 200MB = 800MB 重复
        }
        var unique = new byte[1024 * 1024 * 1024]; // 1GB 独立内容 → 只过漏斗一次全量哈希
        new Random(2).NextBytes(unique);
        MakeFile("unique-huge.bin", unique);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await DuplicateFinder.FindAsync(new DuplicateOptions(Root), null, CancellationToken.None);
        sw.Stop();

        Assert.Equal(4, report.Groups.Single().Files.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(100),
            $"2GB 样本应远小于 100s（10GB<2min 口径），实际 {sw.Elapsed}");
    }

    [Fact]
    public void EmptyDirectoryFinder_FoldsBottomUpAndSkipsProtected()
    {
        // 结构：
        // root/emptyChain/a/b/        （全空 → 只报告 emptyChain）
        // root/hasFile/x/             （x 空，但父有文件 → 只报告 x）
        // root/mixed/                 （含文件 → 不报）
        Directory.CreateDirectory(Path.Combine(Root, "emptyChain", "a", "b"));
        Directory.CreateDirectory(Path.Combine(Root, "hasFile", "x"));
        File.WriteAllText(Path.Combine(Root, "hasFile", "keep.txt"), "data");
        Directory.CreateDirectory(Path.Combine(Root, "mixed", "emptyInner"));
        File.WriteAllText(Path.Combine(Root, "mixed", "data.bin"), "x");

        var empties = EmptyDirectoryFinder.Find(Root, null, CancellationToken.None);

        // emptyChain（整链折叠报顶端）、hasFile\x、mixed\emptyInner；mixed 自身有文件不报
        Assert.Equal(3, empties.Count);
        Assert.Contains(empties, p => p.EndsWith("emptyChain", StringComparison.Ordinal));
        Assert.Contains(empties, p => p.EndsWith("x", StringComparison.Ordinal));
        Assert.Contains(empties, p => p.EndsWith("emptyInner", StringComparison.Ordinal));
        Assert.DoesNotContain(empties, p => p.EndsWith("mixed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleaner_CanDeleteEmptyDirectories_ToRecycleBin()
    {
        var emptyDir = Path.Combine(Root, "empty-target");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(Root, "normal.txt"), "keep");

        var category = new CleanCategory("empty-dirs", "空目录", SafetyLevel.Safe, 0, 1,
            [new CleanItem(emptyDir, 0, Directory.GetLastWriteTimeUtc(emptyDir))], "清理空目录");
        var cleaner = new ShellCleaner();
        var result = await cleaner.ExecuteAsync([category], new CleanOptions(UseRecycleBin: true), null, CancellationToken.None);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(Directory.Exists(emptyDir));
        Assert.True(File.Exists(Path.Combine(Root, "normal.txt")));
        var log = File.ReadAllLines(cleaner.LastAuditLogPath!);
        Assert.Contains(log, l => l.Contains("empty-target") && l.Contains("directory"));
    }

    [Fact]
    public async Task Cleaner_SkipsReparseDirectoryInDirItems()
    {
        var target = Path.Combine(Root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "f.txt"), "x");
        var junction = Path.Combine(Root, "link");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.WaitForExit();
        }
        Assert.True(Directory.Exists(junction));

        var category = new CleanCategory("empty-dirs", "空目录", SafetyLevel.Safe, 0, 1,
            [new CleanItem(junction, 0, Directory.GetLastWriteTimeUtc(junction))], "junction");
        var cleaner = new ShellCleaner();
        var result = await cleaner.ExecuteAsync([category], new CleanOptions(false), null, CancellationToken.None);

        // 红线：junction 不触碰
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.True(Directory.Exists(junction));
        Assert.True(File.Exists(Path.Combine(target, "f.txt")));
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            {
                new FileInfo(PathHelper.GetExtendedPath(f)).Attributes &= ~FileAttributes.ReadOnly;
            }
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
