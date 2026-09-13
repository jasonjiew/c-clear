using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Scanner;
using Xunit;

namespace Cclear.Core.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cclear-scan-" + Guid.NewGuid().ToString("N"));

    private string Root => _root;

    private void MakeDir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative));

    private string MakeFile(string relative, int bytes, char fill = 'a')
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string(fill, bytes));
        return path;
    }

    private ScanResult Scan()
    {
        var scanner = new ManagedTreeScanner(4);
        return scanner.ScanAsync(new ScanRequest(Root), null, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void SyntheticTree_SizesAndCounts_AreCorrect()
    {
        MakeFile("rootfile.txt", 1000);
        MakeFile("a\\one.txt", 100);
        MakeFile("a\\two.txt", 200);
        MakeFile("a\\three.txt", 300);
        MakeFile("b\\c\\deep.txt", 50);

        var result = Scan();
        var tree = result.Tree;

        Assert.Empty(result.AccessDeniedDirs);
        Assert.Equal(1650, tree.GetSize(tree.RootIndex));
        Assert.Equal(5, tree.GetFileCount(tree.RootIndex));

        var a = FindByName(tree, tree.RootIndex, "a");
        Assert.True(a >= 0, "目录 a 应存在");
        Assert.Equal(600, tree.GetSize(a));
        Assert.Equal(3, tree.GetFileCount(a));

        var b = FindByName(tree, tree.RootIndex, "b");
        Assert.True(b >= 0, "目录 b 应存在");

        var c = FindByName(tree, b, "c");
        Assert.True(c >= 0, "目录 c 应存在");
        Assert.Equal(50, tree.GetSize(c));

        var deep = FindByName(tree, c, "deep.txt");
        Assert.True(deep >= 0, "deep.txt 应存在");
        Assert.Equal(50, tree.GetSize(deep));
        Assert.False(tree.GetNode(deep).IsDirectory);

        // 路径重建
        Assert.EndsWith(Path.Combine("b", "c", "deep.txt"), tree.GetPath(deep), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Junction_IsRecordedButNotRecounted()
    {
        MakeFile("real\\payload.bin", 500, 'x');
        var link = Path.Combine(Root, "link");
        var target = Path.Combine(Root, "real");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "mklink /J 创建失败（需要 NTFS）");
        }

        var result = Scan();
        var tree = result.Tree;

        // junction 指向的内容只计一次；junction 节点本身大小为 0 且标记 reparse
        Assert.Equal(500, tree.GetSize(tree.RootIndex));
        var junction = FindByName(tree, tree.RootIndex, "link");
        Assert.True(junction >= 0, "junction 节点应被记录");
        Assert.True(tree.GetNode(junction).IsReparsePoint);
        Assert.Equal(0, tree.GetSize(junction));
        Assert.Equal(0, tree.GetFileCount(junction));
    }

    [Fact]
    public void LongPath_IsEnumerated()
    {
        // 总长度 > 260 字符的深层路径
        string rel = "";
        for (int i = 0; i < 12; i++)
        {
            rel = Path.Combine(rel, "level" + new string('x', 18));
        }
        string deepDir = Path.Combine(Root, rel);
        string extendedDir = PathHelper.GetExtendedPath(deepDir);
        Directory.CreateDirectory(extendedDir);
        File.WriteAllText(PathHelper.GetExtendedPath(Path.Combine(deepDir, "deep.txt")), new string('z', 123));
        Assert.True(new FileInfo(PathHelper.GetExtendedPath(Path.Combine(deepDir, "deep.txt"))).Length == 123);

        var result = Scan();
        var tree = result.Tree;
        Assert.Equal(123, tree.GetSize(tree.RootIndex));

        var found = FindDeepFile(tree, tree.RootIndex, "deep.txt");
        Assert.True(found >= 0, "长路径文件应被枚举到");
        Assert.Equal(123, tree.GetSize(found));
    }

    [Fact]
    public void Classifier_Flags_AreCorrect()
    {
        var dir = NodeClassifier.Classify(FileAttributes.Directory, isDirectory: true);
        Assert.Equal(NodeFlags.Directory, dir);

        var junction = NodeClassifier.Classify(FileAttributes.Directory | FileAttributes.ReparsePoint, true);
        Assert.Equal(NodeFlags.Directory | NodeFlags.ReparsePoint, junction);
        Assert.False(junction.HasFlag(NodeFlags.OneDrivePlaceholder));

        var oneDrivePlaceholder = NodeClassifier.Classify((FileAttributes)0x400000, false);
        Assert.True(oneDrivePlaceholder.HasFlag(NodeFlags.OneDrivePlaceholder));

        var oneDrivePlaceholder2 = NodeClassifier.Classify((FileAttributes)0x400004, false);
        Assert.True(oneDrivePlaceholder2.HasFlag(NodeFlags.OneDrivePlaceholder));

        var symlinkFile = NodeClassifier.Classify(FileAttributes.ReparsePoint, false);
        Assert.True(symlinkFile.HasFlag(NodeFlags.ReparsePoint));
        Assert.False(symlinkFile.HasFlag(NodeFlags.OneDrivePlaceholder));
    }

    [Fact]
    public async Task PreCanceledToken_ThrowsOperationCanceled()
    {
        MakeFile("x.txt", 10);
        var scanner = new ManagedTreeScanner(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(new ScanRequest(Root), null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void AccessDeniedDir_IsCollectedAndScanContinues()
    {
        var lockedDir = Path.Combine(Root, "locked");
        Directory.CreateDirectory(lockedDir);
        File.WriteAllText(Path.Combine(lockedDir, "secret.txt"), "secret");
        MakeFile("ok.txt", 400, 'k');

        bool deniedBlocked = false;
        try
        {
            var psi = new ProcessStartInfo("icacls.exe", $"\"{lockedDir}\" /deny *S-1-1-0:(OI)(CI)F")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi)!)
            {
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    return; // 无法设置 ACL（环境限制），跳过验证
                }
            }
            try
            {
                Directory.EnumerateFileSystemEntries(lockedDir).Any();
                // 仍能枚举（如特权进程），跳过验证
            }
            catch (UnauthorizedAccessException)
            {
                deniedBlocked = true;
            }

            Assert.True(deniedBlocked, "前置条件失败：deny 后仍可枚举");

            var result = Scan();
            var tree = result.Tree;
            // 无权限目录被汇总，扫描不中断；其内容未被计数
            Assert.Contains(result.AccessDeniedDirs, d => d.EndsWith("locked", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(400, tree.GetSize(tree.RootIndex));
            // 根下其他文件仍在
            Assert.True(FindByName(tree, tree.RootIndex, "ok.txt") >= 0);
        }
        finally
        {
            try
            {
                var reset = new ProcessStartInfo("icacls.exe", $"\"{lockedDir}\" /reset")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(reset)!;
                p.WaitForExit();
            }
            catch
            {
                // 清理尽力而为
            }
        }
    }

    [Fact]
    public void ByteSizeFormatter_FormatsHumanReadable()
    {
        Assert.Equal("0 B", Core.ByteSizeFormatter.Format(0));
        Assert.Equal("512 B", Core.ByteSizeFormatter.Format(512));
        Assert.Contains("KB", Core.ByteSizeFormatter.Format(2048));
        Assert.Contains("MB", Core.ByteSizeFormatter.Format(5 * 1024 * 1024));
        Assert.Contains("GB", Core.ByteSizeFormatter.Format(3L * 1024 * 1024 * 1024));
    }

    private static int FindByName(FileTree tree, int parentIndex, string name)
    {
        var node = tree.GetNode(parentIndex);
        if (node.Children == null)
        {
            return -1;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (string.Equals(childNode.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
            if (childNode.IsDirectory && !childNode.IsReparsePoint)
            {
                var deep = FindByName(tree, child, name);
                if (deep >= 0)
                {
                    return deep;
                }
            }
        }
        return -1;
    }

    private static int FindDeepFile(FileTree tree, int parentIndex, string name)
    {
        var node = tree.GetNode(parentIndex);
        if (node.Children == null)
        {
            return -1;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (string.Equals(childNode.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (childNode.IsDirectory)
            {
                var deep = FindDeepFile(tree, child, name);
                if (deep >= 0)
                {
                    return deep;
                }
            }
        }
        return -1;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理尽力而为
        }
    }
}
