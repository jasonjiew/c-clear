using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Cclear.Core.Win32;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>
/// 清理执行器测试（红线回归）：
/// 0 误删（黑名单+占用文件跳过）、默认可回收站还原路径、JSONL 审计可回查。
/// </summary>
public sealed class CleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cclear-clean-" + Guid.NewGuid().ToString("N"));

    private string Root => _root;

    private string MakeFile(string relative, int bytes, char fill = 'a')
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string(fill, bytes));
        return path;
    }

    private CleanCategory CategoryOf(string ruleId, params string[] files)
    {
        var items = files.Select(f => new CleanItem(f, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).ToList();
        return new CleanCategory(ruleId, ruleId, SafetyLevel.Safe, items.Sum(i => i.SizeBytes),
            items.Count, items, "测试分类");
    }

    [Fact]
    public void OccupancyProbe_LockedFileIsRejected_FreeFilePasses()
    {
        var free = MakeFile("free.txt", 100);
        var locked = MakeFile("locked.txt", 100);

        Assert.True(OccupancyProbe.CanDelete(free, out _));

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(OccupancyProbe.CanDelete(locked, out var reason));
            Assert.Contains("占用", reason);
        }
        // 释放句柄后可删除
        Assert.True(OccupancyProbe.CanDelete(locked, out _));
    }

    [Fact]
    public async Task PermanentMode_DeletesFilesAndWritesAudit()
    {
        var f1 = MakeFile("a.log", 500);
        var f2 = MakeFile("b.log", 300);
        var cleaner = new ShellCleaner();

        var result = await cleaner.ExecuteAsync(
            [CategoryOf("test-rule", f1, f2)],
            new CleanOptions(UseRecycleBin: false), null, CancellationToken.None);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(0, result.SkippedFiles);
        Assert.False(File.Exists(f1));
        Assert.False(File.Exists(f2));
        Assert.True(result.FreedBytes >= 0);

        // JSONL 审计可回查：每个删除一行
        Assert.NotNull(cleaner.LastAuditLogPath);
        var lines = File.ReadAllLines(cleaner.LastAuditLogPath!);
        var entries = lines.Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(l)).ToList();
        Assert.Equal(4, entries.Count); // session 开始 + 2 文件 + session 结束
        Assert.Contains(entries, e => e!.Path == f1 && e.Result == "deleted" && e.SizeBytes == 500 && e.RuleId == "test-rule");
        Assert.Contains(entries, e => e!.Path == f2 && e.Result == "deleted");
        Assert.Contains(entries, e => e!.RuleId == "session" && e.Result.StartsWith("deleted=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LockedFile_NeverForceDeleted_GoesToSkipList()
    {
        var locked = MakeFile("held.log", 100);
        var cleaner = new ShellCleaner();
        var handle = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var result = await cleaner.ExecuteAsync(
                [CategoryOf("test-rule", locked)],
                new CleanOptions(UseRecycleBin: false), null, CancellationToken.None);

            // 红线：被占用文件 0 强删，全部进跳过清单
            Assert.Equal(0, result.DeletedFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.True(File.Exists(locked), "被占用文件绝不能被删除");
            Assert.Contains(locked, result.SkippedPaths);
            var entries = File.ReadAllLines(cleaner.LastAuditLogPath!)
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(l)).ToList();
            Assert.Contains(entries, e => e!.Path == locked && e.Result == "skipped");
        }
        finally
        {
            handle.Dispose();
        }
    }

    [Fact]
    public async Task BlacklistedPaths_AreNeverDeleted()
    {
        // 恶意构造：把 Documents 下的文件塞进清理项
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var victim = Path.Combine(documents, $"cclear-must-not-delete-{Guid.NewGuid():N}.txt");
        File.WriteAllText(victim, "重要文件");
        try
        {
            var cleaner = new ShellCleaner();
            var result = await cleaner.ExecuteAsync(
                [CategoryOf("evil-rule", victim)],
                new CleanOptions(UseRecycleBin: false), null, CancellationToken.None);

            Assert.Equal(0, result.DeletedFiles);
            Assert.True(File.Exists(victim), "黑名单文件被删除——红线失守！");
            Assert.Contains(victim, result.SkippedPaths);
        }
        finally
        {
            File.Delete(victim);
        }
    }

    [Fact]
    public async Task RecycleBinMode_DeletesViaShellAndBinGrows()
    {
        var victim = MakeFile("to-bin.txt", 2048, 'z');
        var before = RecycleBin.Query(Path.GetPathRoot(victim)!);
        var cleaner = new ShellCleaner();

        var result = await cleaner.ExecuteAsync(
            [CategoryOf("bin-rule", victim)],
            new CleanOptions(UseRecycleBin: true), null, CancellationToken.None);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(victim));
        var after = RecycleBin.Query(Path.GetPathRoot(victim)!);
        Assert.True(after.ItemCount >= before.ItemCount + 1
                    || after.SizeBytes >= before.SizeBytes,
            "回收站应包含新删除的文件（可还原）");
        // 审计记录为 recycled
        var entries = File.ReadAllLines(cleaner.LastAuditLogPath!)
            .Select(l => System.Text.Json.JsonSerializer.Deserialize<AuditEntry>(l)).ToList();
        Assert.Contains(entries, e => e!.Path == victim && e.Result == "recycled");
    }

    [Fact]
    public async Task PreconditionRunningProcess_SkipsWholeCategory()
    {
        var f = MakeFile("x.tmp", 10);
        var category = new CleanCategory("precond", "precond", SafetyLevel.Safe, 10, 1,
            [new CleanItem(f, 10, File.GetLastWriteTimeUtc(f))], "",
            PreconditionProcesses: ["definitely-not-running-cclear-proc", "explorer"]);
        var cleaner = new ShellCleaner();

        var result = await cleaner.ExecuteAsync([category], new CleanOptions(false), null, CancellationToken.None);

        // explorer.exe 几乎总在运行 → 整类跳过（模拟浏览器缓存保护）
        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(f));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
