using System;
using System.IO;
using Cclear.Core.Leftovers;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P5 卸载残留扫描：注册表比对 / 活跃排除 / 黑名单只提示 / 空目录忽略。</summary>
public class UninstallLeftoverScannerTests : IDisposable
{
    private readonly string _root;
    private readonly FakeRegistry _registry = new();

    public UninstallLeftoverScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cclear-leftover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeRegistry : IUninstallRegistry
    {
        public System.Collections.Generic.List<UninstallEntry> Entries { get; } = new();

        public System.Collections.Generic.IReadOnlyList<UninstallEntry> GetUninstallEntries() => Entries;

        public void AddLocation(string location) =>
            Entries.Add(new UninstallEntry("", "", [location]));
    }

    private string MakeDir(string name, TimeSpan age, params (string Name, int Kb)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (name2, kb) in files)
        {
            var file = Path.Combine(dir, name2);
            File.WriteAllBytes(file, new byte[kb * 1024]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        }
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public void Scan_OrphanOldDirectory_ReportedAsDeletable()
    {
        var orphan = MakeDir("AppGone", TimeSpan.FromDays(60), ("cache.dat", 100));
        _registry.AddLocation(@"C:\Program Files\OtherApp");
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(orphan, candidate.Path);
        Assert.True(candidate.Deletable);
        Assert.Equal("AppGone", candidate.InferredName);
        Assert.True(candidate.SizeBytes >= 100 * 1024);
    }

    [Fact]
    public void Scan_RegisteredByNameOrPath_Excluded()
    {
        // 目录名与登记位置叶子名一致（InstallLocation=Program Files\AppKept，数据目录=%APPDATA%\AppKept）
        MakeDir("AppKept", TimeSpan.FromDays(60), ("cfg.ini", 10));
        // 目录被登记位置前缀覆盖
        MakeDir("Bundle", TimeSpan.FromDays(60), ("x.bin", 10));
        _registry.AddLocation(@"C:\Program Files\AppKept");
        _registry.AddLocation(_root + @"\Bundle\tools");
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        Assert.DoesNotContain(report.Candidates, c => c.InferredName is "AppKept" or "Bundle");
    }

    [Fact]
    public void Scan_RecentlyModifiedDirectory_ExcludedAsActive()
    {
        MakeDir("AppInstalling", TimeSpan.FromDays(1), ("setup.tmp", 10));
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void Scan_EmptyDirectory_NotReported()
    {
        MakeDir("AppEmpty", TimeSpan.FromDays(90));
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void Scan_BlacklistedRoot_MarkedNotDeletable()
    {
        // Program Files 下的孤儿目录：仅提示（红线：黑名单目录不可经本工具删除）
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles) || !Directory.Exists(programFiles))
        {
            return; // 环境无 Program Files（不应发生在 Windows CI）
        }
        var candidateDir = Path.Combine(programFiles, "cclear-leftover-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(candidateDir);
        }
        catch (UnauthorizedAccessException)
        {
            return; // 非管理员无法写入 Program Files：跳过该场景
        }
        try
        {
            Directory.SetLastWriteTimeUtc(candidateDir, DateTime.UtcNow - TimeSpan.FromDays(60));
            var report = UninstallLeftoverScanner.Scan(new FakeRegistry(), [programFiles],
                new LeftoverScanOptions(TimeSpan.FromDays(14), 500));
            var candidate = report.Candidates.FirstOrDefault(c =>
                string.Equals(c.Path, candidateDir, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(candidate);
            Assert.False(candidate!.Deletable);
        }
        finally
        {
            try { Directory.Delete(candidateDir, true); } catch { }
        }
    }

    [Fact]
    public void Scan_MaxCandidates_StopsEarly()
    {
        for (var i = 0; i < 5; i++)
        {
            MakeDir("Old" + i, TimeSpan.FromDays(30), ("f.bin", 1));
        }
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 2));
        Assert.True(report.Candidates.Count <= 2);
    }

    [Fact]
    public void Scan_PublisherOrDisplayNameEvidence_ExcludesInstalledApps()
    {
        // 真实场景：数据目录名与安装目录名不同，但与发布者/显示名匹配
        // （如 kingsoft ↔ 发布者 Kingsoft Corp、QQ ↔ 显示名 QQ）
        MakeDir("kingsoft", TimeSpan.FromDays(60), ("cache.dat", 10));
        MakeDir("DingTalk", TimeSpan.FromDays(60), ("cache.dat", 10));
        _registry.Entries.Add(new UninstallEntry("WPS Office", "Kingsoft Corp.", []));
        _registry.Entries.Add(new UninstallEntry("钉钉", "DingTalk (China) Information Technology Co., Ltd.", []));
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        Assert.DoesNotContain(report.Candidates, c => c.InferredName.Equals("kingsoft", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Candidates, c => c.InferredName.Equals("DingTalk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsSystemDataName_BlocksWindowsInternalDirectories()
    {
        Assert.True(UninstallLeftoverScanner.IsSystemDataName("Microsoft"));
        Assert.True(UninstallLeftoverScanner.IsSystemDataName("USOShared"));
        Assert.True(UninstallLeftoverScanner.IsSystemDataName("USOPrivate"));
        Assert.True(UninstallLeftoverScanner.IsSystemDataName("Windows"));
        Assert.True(UninstallLeftoverScanner.IsSystemDataName("Packages"));
        Assert.False(UninstallLeftoverScanner.IsSystemDataName("kingsoft"));
        Assert.False(UninstallLeftoverScanner.IsSystemDataName("DingTalk"));
    }

    [Fact]
    public void Scan_RunningProcessName_ExcludesLiveAppData()
    {
        // 真实场景复现：钉钉装在 DingDing 目录，数据目录 DingTalk 拼写不同，
        // 位置/名称证据全不命中——但 DingTalk 进程正在运行
        MakeDir("DingTalk", TimeSpan.FromDays(60), ("cache.dat", 10));
        _registry.Entries.Add(new UninstallEntry("钉钉", "Alibaba (China) Network Technology Co.,Ltd.",
            [@"D:\Program Files (x86)\DingDing\uninst.exe"]));
        // 当前测试进程无法保证某第三方应用在跑：确定性断言用进程自身名字
        var selfName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        MakeDir(selfName, TimeSpan.FromDays(60), ("cache.dat", 10));
        var report = UninstallLeftoverScanner.Scan(_registry, [_root], new LeftoverScanOptions(TimeSpan.FromDays(14), 100));
        Assert.DoesNotContain(report.Candidates, c => c.InferredName.Equals(selfName, StringComparison.OrdinalIgnoreCase));
        if (System.Diagnostics.Process.GetProcessesByName("DingTalk").Length > 0)
        {
            Assert.DoesNotContain(report.Candidates, c => c.InferredName.Equals("DingTalk", StringComparison.OrdinalIgnoreCase));
        }
    }
}
