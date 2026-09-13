using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cclear.Core.Analyzer;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Cclear.Core.Settings;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>W4：开发缓存规则（官方 CLI 优先）、排除目录、设置。</summary>
public sealed class DevCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cclear-w4-" + Guid.NewGuid().ToString("N"));

    private string Root => _root;

    [Fact]
    public void EmbeddedPack_ContainsAllDevCacheAndBrowserRules()
    {
        var rules = RulePackLoader.LoadEmbedded();
        string[] expectedDev =
        [
            "npm-cache", "pnpm-store", "yarn-cache", "pip-cache", "poetry-cache", "uv-cache",
            "gradle-caches", "maven-repository", "nuget-packages", "cargo-registry-cache",
            "huggingface-cache", "jetbrains-caches", "vscode-cache", "docker-system",
        ];
        string[] expectedBrowsers = ["edge-cache", "chrome-cache", "firefox-cache"];

        foreach (var id in expectedDev)
        {
            var rule = rules.FirstOrDefault(r => r.Id == id);
            Assert.True(rule is not null, $"缺少开发缓存规则 {id}");
            Assert.Equal(SafetyLevel.Caution, rule!.Level);
            Assert.True(rule.Explanation.Length > 10, $"{id} 应说明重建代价");
        }
        foreach (var id in expectedBrowsers)
        {
            var rule = rules.FirstOrDefault(r => r.Id == id);
            Assert.True(rule is not null, $"缺少浏览器规则 {id}");
            Assert.Equal(SafetyLevel.Safe, rule!.Level);
            Assert.NotEmpty(rule.PreconditionProcesses);
        }
        // CLI 标注
        Assert.Equal("npm", rules.First(r => r.Id == "npm-cache").Cli!.Command);
        Assert.Equal("docker", rules.First(r => r.Id == "docker-system").Cli!.Command);
        Assert.Empty(rules.First(r => r.Id == "docker-system").Paths);
        // VS Code 不触碰用户数据
        var vscode = rules.First(r => r.Id == "vscode-cache");
        Assert.All(vscode.Paths, p => Assert.DoesNotContain("User", p, StringComparison.OrdinalIgnoreCase));

        // 路径完整性：含盘符或环境变量前缀的 Windows 路径必须包含反斜杠（防 JSON 转义吞掉 \n \u 等）
        foreach (var rule in rules)
        {
            foreach (var path in rule.Paths)
            {
                if (path.StartsWith('%') && path != "%TEMP%")
                {
                    Assert.True(path.Contains('\\'), $"规则 {rule.Id} 的路径反斜杠丢失（JSON 转义问题）：{path}");
                }
            }
        }
    }

    [Fact]
    public async Task CliRule_ExecutesCommandOnSuccess()
    {
        var file = Path.Combine(Root, "x.bin");
        Directory.CreateDirectory(Root);
        File.WriteAllText(file, new string('a', 100));

        // cmd /c exit 0 总是成功；条目保持原样（CLI 负责清理）
        var rule = new CleanupRule("cli-ok", "cli", SafetyLevel.Caution, [Root],
            false, ["*"], [], 0, false, [], "", Cli: new CleanupCli("cmd", "/c exit 0"));
        var category = await AnalyzeAsync(rule);
        Assert.NotNull(category);
        Assert.NotNull(category!.CliCommand);

        var cleaner = new ShellCleaner();
        var result = await cleaner.ExecuteAsync([category], new CleanOptions(false), null, CancellationToken.None);

        // CLI 成功：条目按已删计数（由 CLI 清理），文件本体未被动过
        Assert.Equal(1, result.DeletedFiles);
        Assert.True(File.Exists(file));
        var log = File.ReadAllLines(cleaner.LastAuditLogPath!);
        Assert.Contains(log, l => l.Contains("\"cli-ok\""));
    }

    [Fact]
    public async Task CliRule_FallsBackToPathDeletion_WhenCommandMissing()
    {
        var file = Path.Combine(Root, "y.bin");
        Directory.CreateDirectory(Root);
        File.WriteAllText(file, new string('b', 100));

        var rule = new CleanupRule("cli-miss", "cli", SafetyLevel.Caution, [Root],
            false, ["*"], [], 0, false, [], "", Cli: new CleanupCli("cclear-definitely-not-on-path-xyz", ""));
        var category = await AnalyzeAsync(rule);

        var cleaner = new ShellCleaner();
        var result = await cleaner.ExecuteAsync([category!], new CleanOptions(false), null, CancellationToken.None);

        // 命令缺失 → 回退目录删除
        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(file));
        var log = File.ReadAllLines(cleaner.LastAuditLogPath!);
        Assert.Contains(log, l => l.Contains("\"cli-failed\""));
    }

    [Fact]
    public async Task ScannerExcludes_Subtree()
    {
        Directory.CreateDirectory(Path.Combine(Root, "keep"));
        Directory.CreateDirectory(Path.Combine(Root, "skipme"));
        File.WriteAllText(Path.Combine(Root, "keep", "a.txt"), new string('a', 50));
        File.WriteAllText(Path.Combine(Root, "skipme", "b.txt"), new string('b', 500));

        var scanner = new ManagedTreeScanner(2);
        var result = await scanner.ScanAsync(
            new ScanRequest(Root, [Path.Combine(Root, "skipme")]), null, CancellationToken.None);

        var tree = result.Tree;
        Assert.Equal(50, tree.GetSize(tree.RootIndex)); // skipme 整树未计入
        Assert.Equal(1, tree.GetFileCount(tree.RootIndex));
        var skipNode = FindDir(tree, tree.RootIndex, "skipme");
        Assert.True(skipNode >= 0, "排除目录外壳节点保留以便 UI 展示");
        Assert.Equal(0, tree.GetSize(skipNode));
    }

    [Fact]
    public async Task Analyzer_ExcludesConfiguredDirs()
    {
        Directory.CreateDirectory(Path.Combine(Root, "cache1"));
        Directory.CreateDirectory(Path.Combine(Root, "cache2"));
        File.WriteAllText(Path.Combine(Root, "cache1", "a.tmp"), new string('a', 100));
        File.WriteAllText(Path.Combine(Root, "cache2", "b.tmp"), new string('b', 200));

        var analyzer = new CleanPlanAnalyzer
        {
            ExcludePaths = [Path.Combine(Root, "cache1")],
        };
        var rule = new CleanupRule("excl-test", "excl", SafetyLevel.Safe, [Root], false, ["*"], [], 0, false, [], "");
        var plan = await analyzer.BuildPlanAsync([rule], null, CancellationToken.None);
        var category = plan.Categories.FirstOrDefault(c => c.RuleId == "excl-test");

        Assert.NotNull(category);
        Assert.Equal(200, category!.EstimatedBytes);
        Assert.DoesNotContain(category.Items, i => i.Path.Contains("cache1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Settings_Roundtrip()
    {
        var path = Path.Combine(Root, "settings.json");
        Directory.CreateDirectory(Root);
        var settings = new AppSettings();
        settings.ExcludedDirs.Add(@"C:\MyExcluded");
        settings.PermanentDelete = true;
        settings.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal([@"C:\MyExcluded"], loaded.ExcludedDirs);
        Assert.True(loaded.PermanentDelete);
        Assert.Equal([@"C:\MyExcluded"], loaded.NormalizedExclusions());

        // 损坏文件按默认处理
        File.WriteAllText(path, "{ not json");
        Assert.False(AppSettings.Load(path).PermanentDelete);
    }

    private static async Task<CleanCategory?> AnalyzeAsync(CleanupRule rule)
    {
        var analyzer = new CleanPlanAnalyzer();
        var plan = await analyzer.BuildPlanAsync([rule], null, CancellationToken.None);
        return plan.Categories.FirstOrDefault(c => c.RuleId == rule.Id);
    }

    private static int FindDir(FileTree tree, int parent, string name)
    {
        var node = tree.GetNode(parent);
        if (node.Children == null)
        {
            return -1;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (childNode.IsDirectory && string.Equals(childNode.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
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
        }
    }
}
