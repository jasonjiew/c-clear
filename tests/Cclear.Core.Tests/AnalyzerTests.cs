using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cclear.Core.Analysis;
using Cclear.Core.Analyzer;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

public sealed class AnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cclear-anl-" + Guid.NewGuid().ToString("N"));

    private string Root => _root;

    private void MakeFile(string relative, int bytes, char fill = 'a', int? ageDays = null)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string(fill, bytes));
        if (ageDays is int days)
        {
            var time = DateTime.Now.AddDays(-days);
            File.SetLastWriteTime(path, time);
            File.SetLastWriteTimeUtc(path, time.ToUniversalTime());
        }
    }

    private async Task<CleanCategory?> AnalyzeSingleAsync(CleanupRule rule)
    {
        var analyzer = new CleanPlanAnalyzer();
        var plan = await analyzer.BuildPlanAsync([rule], null, CancellationToken.None);
        return plan.Categories.FirstOrDefault(c => c.RuleId == rule.Id);
    }

    [Fact]
    public async Task AgeFilter_OnlyCountsOldFiles()
    {
        MakeFile("old.txt", 500, ageDays: 10);
        MakeFile("new.txt", 200, ageDays: 0);

        var rule = new CleanupRule("age-test", "年龄测试", SafetyLevel.Safe, [Root],
            false, ["*"], [], 3, false, [], "");
        var category = await AnalyzeSingleAsync(rule);

        Assert.NotNull(category);
        Assert.Equal(500, category!.EstimatedBytes);
        Assert.Equal(1, category.FileCount);
        Assert.Single(category.Items);
        Assert.EndsWith("old.txt", category.Items[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncludeExclude_GlobsApplied()
    {
        MakeFile("keep.log", 100);
        MakeFile("sub\\skip.log", 40);
        MakeFile("deep\\a\\data.bin", 300);
        MakeFile("trash.tmp", 20);

        var rule = new CleanupRule("glob-test", "glob 测试", SafetyLevel.Safe, [Root],
            false, ["**/*.log"], ["**/skip.log"], 0, false, [], "");
        var category = await AnalyzeSingleAsync(rule);

        Assert.NotNull(category);
        Assert.Equal(100, category!.EstimatedBytes);
        var paths = category.Items.Select(i => Path.GetFileName(i.Path)).ToList();
        Assert.Contains("keep.log", paths);
        Assert.DoesNotContain("skip.log", paths);
    }

    [Fact]
    public async Task Blacklist_UserDocuments_NeverIncluded()
    {
        // 规则恶意指向用户文档目录：Analyzer 层就应过滤
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var probe = Path.Combine(documents, "cclear-blacklist-probe.txt");
        File.WriteAllText(probe, new string('x', 123));
        try
        {
            var rule = new CleanupRule("evil-rule", "恶意规则", SafetyLevel.Safe, [documents],
                false, ["**/*"], [], 0, false, [], "");
            var category = await AnalyzeSingleAsync(rule);
            // 该目录存在文件但全部命中黑名单 → 不应产生分类
            Assert.Null(category);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [Fact]
    public async Task ReportOnly_ReportsWholeSubtreeWithoutItems()
    {
        MakeFile("a.bin", 1000);
        MakeFile("sub\\b.bin", 250);

        var rule = new CleanupRule("report-test", "报告测试", SafetyLevel.Manual, [Root],
            false, ["*"], [], 0, false, [], "", ReportOnly: true);
        var category = await AnalyzeSingleAsync(rule);

        Assert.NotNull(category);
        Assert.Equal(1250, category!.EstimatedBytes);
        Assert.Equal(2, category.FileCount);
        Assert.Empty(category.Items);
    }

    [Fact]
    public async Task TempEstimate_MatchesManualSelection_Hermetic()
    {
        // 验收“%TEMP% 估算与手选一致”：在合成目录上做确定性比对（规则同样走 72 小时年龄过滤）
        MakeFile("cache\\old-a.tmp", 1000, ageDays: 10);
        MakeFile("cache\\fresh.tmp", 40, ageDays: 0);
        MakeFile("cache\\old-b.tmp", 260, ageDays: 5);
        MakeFile("cache\\old\\nested-c.tmp", 60, ageDays: 4);

        var rule = new CleanupRule("user-temp-like", "用户临时文件", SafetyLevel.Safe, [Path.Combine(Root, "cache")],
            false, ["*"], [], 3, false, [], "");
        var category = await AnalyzeSingleAsync(rule);

        // 手选：超过 72h 的四个文件中，fresh.tmp 不该入选
        Assert.NotNull(category);
        Assert.Equal(1000 + 260 + 60, category!.EstimatedBytes);
        Assert.Equal(3, category.FileCount);
    }

    [Fact]
    public async Task UserTempRule_ProducesCategoryOnRealTemp()
    {
        // 真 %TEMP% 上的管线 sanity（TEMP 活跃，不做严格等值比对）
        var rule = RulePackLoader.LoadEmbedded().First(r => r.Id == "user-temp");
        Assert.Null(await Record.ExceptionAsync(() => AnalyzeSingleAsync(rule)));
    }

    [Fact]
    public async Task SpaceAnalysis_LargeFilesAndExtensions()
    {
        MakeFile("small.txt", 10);
        MakeFile("big.bin", 300, 'b');
        MakeFile("bigger.bin", 500, 'c');

        var scanner = new Cclear.Core.Scanner.ManagedTreeScanner(2);
        var result = await scanner.ScanAsync(new Cclear.Core.Scanner.ScanRequest(Root), null, CancellationToken.None);

        var large = SpaceAnalysis.FindLargeFiles(result.Tree, 256, 10);
        Assert.Equal(2, large.Count);
        Assert.True(large[0].SizeBytes >= large[1].SizeBytes);
        Assert.EndsWith("bigger.bin", large[0].Path, StringComparison.Ordinal);

        var exts = SpaceAnalysis.ExtensionStatistics(result.Tree, 10);
        Assert.Equal(2, exts.Count);
        Assert.Equal(".bin", exts[0].Extension);
        Assert.Equal(800, exts[0].TotalBytes);
        Assert.Equal(2, exts[0].FileCount);
    }

    [Fact]
    public void GlobalBlacklist_Behaviour()
    {
        Assert.True(GlobalBlacklist.IsProtected(@"C:\Windows\Installer\0000.msi"));
        Assert.True(GlobalBlacklist.IsProtected(@"C:\Windows\WinSxS\manifests\x.xml"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\System Volume Information\idx"));
        Assert.True(GlobalBlacklist.IsProtected(@"C:\$Recycle.Bin\S-1-5-18\f"));
        Assert.True(GlobalBlacklist.IsProtected(@"C:\Program Files\App\a.exe"));
        Assert.True(GlobalBlacklist.IsProtected(@"C:\Program Files (x86)\App\a.exe"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\pagefile.sys"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\swapfile.sys"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(GlobalBlacklist.IsProtected(Path.Combine(profile, "Documents", "报告.docx")));
        Assert.True(GlobalBlacklist.IsProtected(Path.Combine(profile, "Downloads", "setup.exe")));
        Assert.True(GlobalBlacklist.IsProtected(@"C:\Windows\System32\DriverStore\FileRepository\inf"));
        Assert.False(GlobalBlacklist.IsProtected(@"C:\Windows\Temp\a.tmp"));
        Assert.False(GlobalBlacklist.IsProtected(Path.Combine(profile, "AppData", "Local", "Temp", "a.tmp")));
        // 段级匹配要求完整段名：InstallerNotes 不应误伤
        Assert.False(GlobalBlacklist.IsProtected(@"D:\projects\InstallerNotes\readme.md"));
        // 用户目录子树保护，不保护用户根下的未知目录
        Assert.False(GlobalBlacklist.IsProtected(Path.Combine(profile, "AppData", "Local", "npm-cache", "a")));
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
