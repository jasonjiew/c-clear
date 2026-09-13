using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Cclear.Core.Win32;
namespace Cclear.Core.Analyzer;

public sealed record CleanAnalysisProgress(string CurrentRule, int RulesDone, int RulesTotal);

/// <summary>体检引擎：规则包 × 文件系统 → CleanPlan（只读，不删任何东西）。</summary>
public sealed class CleanPlanAnalyzer
{
    private readonly IScanner _scanner;

    public CleanPlanAnalyzer()
        : this(new ManagedTreeScanner())
    {
    }

    public CleanPlanAnalyzer(IScanner scanner)
    {
        _scanner = scanner;
    }

    /// <summary>排除目录（来自设置）：传入每次规则扫描，命中的子树整体跳过。</summary>
    public IReadOnlyList<string>? ExcludePaths { get; set; }

    /// <summary>
    /// 目标盘根（如 "D:\"，V3 多盘）：设置后体检只面向该盘——
    /// 用户变量类规则跳过、系统盘硬编码路径改写到目标盘、回收站仅查询该卷、CLI 规则跳过；
    /// null 或系统盘 = 默认全量体检。
    /// </summary>
    public string? TargetDriveRoot { get; set; }

    public async Task<CleanPlan> BuildPlanAsync(IReadOnlyList<CleanupRule> rules,
        IProgress<CleanAnalysisProgress>? progress, CancellationToken ct)
    {
        var categories = new List<CleanCategory>();
        bool isAdmin = SystemCheck.IsAdministrator();
        var targetRoot = TargetDriveRoot is null || MultiDriveRules.IsSystemDrive(TargetDriveRoot)
            ? null
            : MultiDriveRules.NormalizeRoot(TargetDriveRoot);
        var effectiveRules = BuildEffectiveRules(rules, targetRoot);
        rules = effectiveRules;

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CleanAnalysisProgress(rule.Name, i, rules.Count));

            var notes = new List<string>();
            if (rule.PreconditionProcesses.Length > 0)
            {
                var running = rule.PreconditionProcesses.Where(SystemCheck.IsProcessRunning).ToList();
                if (running.Count > 0)
                {
                    notes.Add($"检测到 {string.Join("、", running)} 正在运行，清理时将自动跳过");
                }
            }
            if (rule.RequiresAdmin && !isAdmin)
            {
                notes.Add("需管理员权限才能完整统计与清理");
            }

            try
            {
                CleanCategory? category = rule.ShellAction ? BuildShellActionCategory(rule, targetRoot)
                    : rule.ReportOnly ? BuildReportOnlyCategory(rule)
                    : await BuildFileCategoryAsync(rule, ct);
                if (category is null)
                {
                    continue;
                }
                if (notes.Count > 0)
                {
                    category = category with { Explanation = category.Explanation + "（提示：" + string.Join("；", notes) + "）" };
                }
                // CLI 类即使估算为 0 也要保留（执行时由 CLI 清理，如 docker）
                if (category.FileCount > 0 || category.EstimatedBytes > 0 || rule.Cli is not null)
                {
                    categories.Add(category);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单规则失败不拖垮整体体检
                System.Diagnostics.Debug.WriteLine($"[rule-failed] {rule.Id}: {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("CCLEAR_DEBUG_RULES") == "1")
                {
                    Console.Error.WriteLine($"[rule-failed] {rule.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        progress?.Report(new CleanAnalysisProgress("", rules.Count, rules.Count));
        return new CleanPlan(categories, categories.Sum(c => c.EstimatedBytes));
    }

    /// <summary>多盘模式（targetRoot 非 null）下构建目标盘有效的规则集。</summary>
    private static List<CleanupRule> BuildEffectiveRules(IReadOnlyList<CleanupRule> rules, string? targetRoot)
    {
        if (targetRoot is null)
        {
            return [.. rules];
        }
        var list = new List<CleanupRule>();
        foreach (var rule in rules)
        {
            if (rule.ShellAction)
            {
                list.Add(rule); // 回收站：执行层按目标卷过滤
                continue;
            }
            if (rule.Cli is not null || rule.Paths.Length == 0)
            {
                continue; // CLI 是机器级动作；无路径规则不适用于非系统盘
            }
            var rewritten = MultiDriveRules.RewritePaths(rule.Paths, targetRoot);
            if (rewritten.Length > 0)
            {
                list.Add(rule with { Paths = rewritten });
            }
        }
        list.AddRange(MultiDriveRules.ForDrive(targetRoot));
        return list;
    }

    /// <summary>普通规则：展开根目录 → 扫描 → leaf/include/exclude/年龄/黑名单过滤 → CleanItem 列表。</summary>
    private async Task<CleanCategory?> BuildFileCategoryAsync(CleanupRule rule, CancellationToken ct)
    {
        var roots = RulePathExpander.Expand(rule);
        if (roots.Count == 0 && rule.Cli is null)
        {
            return null;
        }
        var items = new List<CleanItem>();
        var includeRegexes = rule.IncludePatterns.Select(GlobMatcher.ToRegex).ToList();
        var excludeRegexes = rule.ExcludePatterns.Select(GlobMatcher.ToRegex).ToList();
        DateTimeOffset cutoff = rule.MinAgeDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-rule.MinAgeDays)
            : DateTimeOffset.MaxValue;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _scanner.ScanAsync(new ScanRequest(root.BaseDir, ExcludePaths), null, ct);
            CollectMatching(result.Tree, result.Tree.RootIndex, result.Tree.RootIndex, root,
                includeRegexes, excludeRegexes, cutoff, rule, items);
        }

        if (items.Count == 0 && rule.Cli is null)
        {
            return null;
        }
        items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return new CleanCategory(
            rule.Id,
            rule.Name,
            rule.Level,
            items.Sum(i => i.SizeBytes),
            items.Count,
            items,
            BuildExplanation(rule),
            rule.PreconditionProcesses,
            IsShellAction: false,
            rule.Cli?.Command,
            rule.Cli?.Args);
    }

    /// <summary>shellAction 规则（清空回收站）：经 SHQueryRecycleBin 报告每卷合计；targetRoot 非 null 时仅该卷。</summary>
    private CleanCategory? BuildShellActionCategory(CleanupRule rule, string? targetRoot = null)
    {
        long bytes = 0;
        long items = 0;
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);
        if (targetRoot is not null)
        {
            var normalized = targetRoot.TrimEnd('\\');
            drives = drives.Where(d => string.Equals(d.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var drive in drives)
        {
            var info = RecycleBin.Query(drive);
            bytes += info.SizeBytes;
            items += info.ItemCount;
        }
        if (items == 0)
        {
            return null;
        }
        return new CleanCategory(rule.Id, rule.Name, rule.Level, bytes,
            (int)Math.Min(int.MaxValue, items), Array.Empty<CleanItem>(), BuildExplanation(rule),
            rule.PreconditionProcesses, IsShellAction: true, ShellActionVolumeRoot: targetRoot);
    }

    private void CollectMatching(FileTree tree, int dirIndex, int rootIndex, RuleRoot root,
        List<Regex> includeRegexes, List<Regex> excludeRegexes, DateTimeOffset cutoff,
        CleanupRule rule, List<CleanItem> items)
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
                    CollectMatching(tree, child, rootIndex, root, includeRegexes, excludeRegexes, cutoff, rule, items);
                }
                continue;
            }
            var item = MatchFile(tree, child, rootIndex, root, includeRegexes, excludeRegexes, cutoff, rule);
            if (item is not null)
            {
                items.Add(item);
            }
        }
    }

    private static CleanItem? MatchFile(FileTree tree, int fileIndex, int rootIndex, RuleRoot root,
        List<Regex> includeRegexes, List<Regex> excludeRegexes, DateTimeOffset cutoff, CleanupRule rule)
    {
        var node = tree.GetNode(fileIndex);
        var path = tree.GetPath(fileIndex);

        // 纯符号链接计 0，不产生清理项；OneDrive 占位保留
        if (node.IsReparsePoint && !node.IsOneDrivePlaceholder)
        {
            return null;
        }
        if (GlobalBlacklist.IsProtected(path))
        {
            return null;
        }

        string relative = GetRelativePath(path, root.BaseDir);
        if (root.LeafPattern is not null)
        {
            // 尾部命名模式：仅匹配根目录的直接子文件（如 thumbcache_*.db）
            if (node.ParentIndex != rootIndex || !GlobMatcher.IsMatch(root.LeafPattern, node.Name))
            {
                return null;
            }
        }
        else if (includeRegexes.Count > 0 && !includeRegexes.Any(r => r.IsMatch(relative)))
        {
            return null;
        }

        if (excludeRegexes.Any(r => r.IsMatch(node.Name) || r.IsMatch(relative)))
        {
            return null;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (rule.MinAgeDays > 0 && new DateTimeOffset(lastWrite, TimeSpan.Zero) > cutoff)
        {
            return null;
        }
        return new CleanItem(path, node.SizeBytes, lastWrite);
    }

    /// <summary>reportOnly 规则：整棵子树只报告总量（windows-old / windows-logs 等）；
    /// 叶子模式（如 D:\found.*）仅统计 BaseDir 下匹配的直接子项，绝不报告整个盘根。</summary>
    private CleanCategory? BuildReportOnlyCategory(CleanupRule rule)
    {
        long total = 0;
        long files = 0;
        foreach (var root in RulePathExpander.Expand(rule))
        {
            var scan = _scanner.ScanAsync(new ScanRequest(root.BaseDir), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (root.LeafPattern is null)
            {
                total += scan.Tree.GetSize(scan.Tree.RootIndex);
                files += scan.Tree.GetFileCount(scan.Tree.RootIndex);
            }
            else
            {
                var node = scan.Tree.GetNode(scan.Tree.RootIndex);
                if (node.Children is null)
                {
                    continue;
                }
                foreach (var child in node.Children)
                {
                    var childNode = scan.Tree.GetNode(child);
                    if (!GlobMatcher.IsMatch(root.LeafPattern, childNode.Name))
                    {
                        continue;
                    }
                    total += scan.Tree.GetSize(child);
                    files += scan.Tree.GetFileCount(child);
                }
            }
        }
        if (total == 0)
        {
            return null;
        }
        return new CleanCategory(rule.Id, rule.Name, rule.Level, total, (int)Math.Min(int.MaxValue, files),
            Array.Empty<CleanItem>(), BuildExplanation(rule), rule.PreconditionProcesses, IsShellAction: false);
    }

    internal static string BuildExplanation(CleanupRule rule)
    {
        var sb = new StringBuilder(rule.Explanation);
        if (rule.MinAgeDays > 0)
        {
            sb.Append($"；仅统计修改时间超过 {rule.MinAgeDays} 天的文件");
        }
        if (rule.RequiresAdmin)
        {
            sb.Append("；需要管理员权限");
        }
        return sb.ToString();
    }

    private static string GetRelativePath(string fullPath, string baseDir)
    {
        var normalizedBase = baseDir.TrimEnd('\\');
        if (fullPath.Length > normalizedBase.Length
            && fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase)
            && fullPath[normalizedBase.Length] == '\\')
        {
            return fullPath[(normalizedBase.Length + 1)..];
        }
        return fullPath;
    }
}
